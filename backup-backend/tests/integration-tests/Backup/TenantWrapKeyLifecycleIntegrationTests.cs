using System.Security.Cryptography;
using System.Text.Json;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.IntegrationTests.Notifications;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupPlatform.IntegrationTests.Backup;

/// <summary>Tenant wrap key rotation, retirement and listing: states, the one-Active rule, tenant isolation, what the audit and the views may contain.</summary>
public sealed class TenantWrapKeyLifecycleIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private readonly InMemorySecretStore _secrets = new();
    private readonly UserId _admin = UserId.New();

    private PostgreSqlTenantWrapKeyStore Store() => new(fixture.Options, _secrets, TimeProvider.System);

    private async Task<TenantId> TenantAsync()
    {
        var tenant = new Tenant(TenantId.New(), $"Tenant {Guid.NewGuid():N}");
        await using var context = fixture.CreatePlatformContext();
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();
        return tenant.Id;
    }

    private static byte[] Wrap(byte[] spki, byte[] dek)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(spki, out _);
        return rsa.Encrypt(dek, RSAEncryptionPadding.OaepSHA256);
    }

    [Fact]
    public async Task RotationMakesTheNewKeyActiveAndTheOldOneDecryptOnlyWhichStillOpensItsArtifacts()
    {
        var tenant = await TenantAsync();
        var store = Store();
        var first = await store.EnsureActiveAsync(tenant, default);
        var dekOld = RandomNumberGenerator.GetBytes(32);
        var wrappedOld = Wrap(first.PublicKeySpki, dekOld);

        var rotation = await store.RotateAsync(tenant, _admin, default);

        Assert.Equal((2, TenantWrapKeyState.Active), (rotation.Key.Version, rotation.Key.State));
        Assert.Equal((1, TenantWrapKeyState.DecryptOnly, first.KeyId), (rotation.PreviousKey!.Version, rotation.PreviousKey.State, rotation.PreviousKey.KeyId));
        // New artifacts seal to the new key, old ones keep opening with the old one.
        var active = await store.EnsureActiveAsync(tenant, default);
        Assert.Equal((2, rotation.Key.KeyId), (active.Version, active.KeyId));
        Assert.NotEqual(first.KeyId, active.KeyId);
        Assert.Equal(dekOld, await store.UnwrapDekAsync(tenant, "restore_attempt", Guid.NewGuid(), first.KeyId, wrappedOld, default));
        Assert.True(await store.OwnsKeyAsync(tenant, first.KeyId, 1, default));
        Assert.Equal([2, 1], (await store.ListAsync(tenant, default)).Select(key => key.Version));
    }

    [Fact]
    public async Task RotatingATenantWithoutKeysCreatesItsFirstKeyWithNoPreviousOne()
    {
        var tenant = await TenantAsync();

        var rotation = await Store().RotateAsync(tenant, _admin, default);

        Assert.Equal((1, TenantWrapKeyState.Active), (rotation.Key.Version, rotation.Key.State));
        Assert.Null(rotation.PreviousKey);
    }

    [Fact]
    public async Task ConcurrentRotationsLeaveExactlyOneActiveKeyAndDistinctVersions()
    {
        var tenant = await TenantAsync();
        await Store().EnsureActiveAsync(tenant, default);

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Store().RotateAsync(tenant, _admin, default)));

        var keys = await Store().ListAsync(tenant, default);
        Assert.Equal([4, 3, 2, 1], keys.Select(key => key.Version));
        Assert.Single(keys, key => key.State == TenantWrapKeyState.Active);
        Assert.Equal(3, keys.Count(key => key.State == TenantWrapKeyState.DecryptOnly));
        Assert.Equal(4, keys.Select(key => key.KeyId).Distinct().Count());
    }

    [Fact]
    public async Task OnlyADecryptOnlyKeyCanBeRetiredAndRetiringDestroysItsPrivateHalf()
    {
        var tenant = await TenantAsync();
        var store = Store();
        var first = await store.EnsureActiveAsync(tenant, default);
        var wrapped = Wrap(first.PublicKeySpki, RandomNumberGenerator.GetBytes(32));
        await store.RotateAsync(tenant, _admin, default);
        var oldSecret = $"twk-{tenant.Value:N}-1";
        Assert.Contains(oldSecret, _secrets.Names);

        // The Active key is never retired directly: the tenant keeps one key that seals new artifacts.
        Assert.Equal(TenantWrapKeyRetireOutcome.ActiveKey, (await store.RetireAsync(tenant, 2, _admin, default)).Outcome);

        var retired = await store.RetireAsync(tenant, 1, _admin, default);
        Assert.Equal(TenantWrapKeyRetireOutcome.Retired, retired.Outcome);
        Assert.Equal(TenantWrapKeyState.Retired, retired.Key!.State);
        Assert.DoesNotContain(oldSecret, _secrets.Names);
        await Assert.ThrowsAsync<TenantWrapKeyUnavailableException>(() => store.UnwrapDekAsync(tenant, "restore_attempt", Guid.NewGuid(), first.KeyId, wrapped, default));
        Assert.False(await store.OwnsKeyAsync(tenant, first.KeyId, 1, default));
        // The active key is untouched.
        Assert.Equal(TenantWrapKeyState.Active, (await store.ListAsync(tenant, default)).Single(key => key.Version == 2).State);
    }

    [Fact]
    public async Task RetiringAnUnknownOrAlreadyRetiredKeyChangesNothing()
    {
        var tenant = await TenantAsync();
        var store = Store();
        await store.EnsureActiveAsync(tenant, default);
        await store.RotateAsync(tenant, _admin, default);
        await store.RetireAsync(tenant, 1, _admin, default);
        await using var before = fixture.CreatePlatformContext();
        var auditsBefore = await before.AuditEvents.CountAsync(e => e.TenantId == tenant && e.Action == AuditActions.TenantWrapKeyRetired);

        Assert.Equal(TenantWrapKeyRetireOutcome.NotFound, (await store.RetireAsync(tenant, 9, _admin, default)).Outcome);
        Assert.Equal(TenantWrapKeyRetireOutcome.AlreadyRetired, (await store.RetireAsync(tenant, 1, _admin, default)).Outcome);

        await using var after = fixture.CreatePlatformContext();
        Assert.Equal(auditsBefore, await after.AuditEvents.CountAsync(e => e.TenantId == tenant && e.Action == AuditActions.TenantWrapKeyRetired));
    }

    [Fact]
    public async Task OneTenantsRotationAndRetirementNeverTouchAnotherTenantsKeys()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        var store = Store();
        var keyA = await store.EnsureActiveAsync(a, default);
        var keyB = await store.EnsureActiveAsync(b, default);
        var wrappedB = Wrap(keyB.PublicKeySpki, RandomNumberGenerator.GetBytes(32));

        await store.RotateAsync(a, _admin, default);
        await store.RetireAsync(a, 1, _admin, default);

        var listB = await store.ListAsync(b, default);
        var only = Assert.Single(listB);
        Assert.Equal((keyB.KeyId, TenantWrapKeyState.Active), (only.KeyId, only.State));
        Assert.DoesNotContain(keyA.KeyId, listB.Select(key => key.KeyId));
        await store.UnwrapDekAsync(b, "restore_attempt", Guid.NewGuid(), keyB.KeyId, wrappedB, default);
        // Another tenant's version number is not found: retirement is scoped by tenant.
        Assert.Equal(TenantWrapKeyRetireOutcome.NotFound, (await store.RetireAsync(b, 2, _admin, default)).Outcome);
        Assert.Equal(TenantWrapKeyRetireOutcome.ActiveKey, (await store.RetireAsync(b, 1, _admin, default)).Outcome);
    }

    [Fact]
    public async Task AuditAndViewsCarryIdsOnlyAndNeverKeyMaterial()
    {
        var tenant = await TenantAsync();
        var store = Store();
        var first = await store.EnsureActiveAsync(tenant, default);
        var rotation = await store.RotateAsync(tenant, _admin, default);
        await store.RetireAsync(tenant, 1, _admin, default);

        await using var context = fixture.CreatePlatformContext();
        var events = await context.AuditEvents.AsNoTracking().Where(e => e.TenantId == tenant).ToListAsync();
        var rotated = Assert.Single(events, e => e.Action == AuditActions.TenantWrapKeyRotated);
        var retired = Assert.Single(events, e => e.Action == AuditActions.TenantWrapKeyRetired);
        Assert.All(new[] { rotated, retired }, e => Assert.Equal((_admin, AuditActorKind.PlatformAdmin), (e.ActorUserId, e.ActorKind)));
        using var rotatedDetails = JsonDocument.Parse(rotated.DetailsJson);
        Assert.Equal((rotation.Key.KeyId, 2, first.KeyId), (rotatedDetails.RootElement.GetProperty("keyId").GetString(), rotatedDetails.RootElement.GetProperty("keyVersion").GetInt32(),
            rotatedDetails.RootElement.GetProperty("previousKeyId").GetString()));
        using var retiredDetails = JsonDocument.Parse(retired.DetailsJson);
        Assert.Equal((first.KeyId, 0), (retiredDetails.RootElement.GetProperty("keyId").GetString(), retiredDetails.RootElement.GetProperty("artifactsAffected").GetInt32()));

        var everything = string.Concat(events.Select(e => e.DetailsJson + e.TargetId)) + JsonSerializer.Serialize(await store.ListAsync(tenant, default));
        Assert.DoesNotContain("PRIVATE KEY", everything);
        Assert.DoesNotContain(Convert.ToBase64String(first.PublicKeySpki), everything);
        foreach (var privateKey in _secrets.Values) Assert.DoesNotContain(privateKey[..64], everything);
    }

    [Fact]
    public async Task TheDatabaseItselfRefusesASecondActiveKeyForATenant()
    {
        var tenant = await TenantAsync();
        await Store().EnsureActiveAsync(tenant, default);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync($"""
            INSERT INTO tenant_wrap_keys ("Id","TenantId","Version","KeyId","PublicKeySpki","SecretReference","State","CreatedAt")
            VALUES ('{Guid.NewGuid()}','{tenant.Value}',2,'{Guid.NewGuid():N}','\x01','twk-x','Active', now())
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
    }
}
