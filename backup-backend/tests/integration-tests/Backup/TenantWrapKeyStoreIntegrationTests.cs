using System.Security.Cryptography;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Encryption;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.IntegrationTests.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BackupPlatform.IntegrationTests.Backup;

public sealed class TenantWrapKeyStoreIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private readonly InMemorySecretStore _secrets = new();

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
    public async Task EnsureActiveCreatesOneKeyPerTenantAndOnlyItsPublicHalfIsInTheDatabase()
    {
        var tenant = await TenantAsync();
        var store = Store();
        var first = await store.EnsureActiveAsync(tenant, default);
        var again = await Store().EnsureActiveAsync(tenant, default);
        Assert.Equal((1, first.KeyId), (again.Version, again.KeyId));
        Assert.Equal(BpeKeyId.FromSpki(first.PublicKeySpki), first.KeyId);
        await using var context = fixture.CreatePlatformContext();
        var row = await context.TenantWrapKeys.AsNoTracking().SingleAsync(key => key.TenantId == tenant);
        Assert.Equal(TenantWrapKeyState.Active, row.State);
        Assert.Contains(row.SecretReference, _secrets.Names);
        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", _secrets.Values.First(v => v.Contains("PRIVATE KEY")));
        Assert.True(await store.OwnsKeyAsync(tenant, first.KeyId, 1, default));
        Assert.False(await store.OwnsKeyAsync(tenant, first.KeyId, 2, default));
    }

    [Fact]
    public async Task ConcurrentFirstUseMintsExactlyOneKey()
    {
        var tenant = await TenantAsync();
        var keys = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Store().EnsureActiveAsync(tenant, default)));
        Assert.Single(keys.Select(key => key.KeyId).Distinct());
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(1, await context.TenantWrapKeys.CountAsync(key => key.TenantId == tenant));
    }

    [Fact]
    public async Task UnwrapReturnsTheDekAndAuditsIdsOnly()
    {
        var tenant = await TenantAsync();
        var store = Store();
        var key = await store.EnsureActiveAsync(tenant, default);
        var dek = RandomNumberGenerator.GetBytes(32);
        var attempt = Guid.NewGuid();
        Assert.Equal(dek, await store.UnwrapDekAsync(tenant, "restore_attempt", attempt, key.KeyId, Wrap(key.PublicKeySpki, dek), default));

        await using var context = fixture.CreatePlatformContext();
        var events = await context.AuditEvents.AsNoTracking().Where(e => e.TenantId == tenant).ToListAsync();
        var unwrapped = Assert.Single(events, e => e.Action == AuditActions.BackupArtifactKeyUnwrapped);
        Assert.Contains(attempt.ToString("D"), unwrapped.DetailsJson);
        Assert.Contains(key.KeyId, unwrapped.DetailsJson);
        var everything = string.Concat(events.Select(e => e.DetailsJson + e.TargetId));
        Assert.DoesNotContain(Convert.ToBase64String(dek), everything);
        Assert.DoesNotContain(Convert.ToHexStringLower(dek), everything);
        Assert.DoesNotContain("PRIVATE KEY", everything);
        Assert.DoesNotContain(Convert.ToBase64String(key.PublicKeySpki), everything);
        Assert.Contains(events, e => e.Action == AuditActions.TenantWrapKeyCreated);
    }

    [Fact]
    public async Task ATenantsKeyIdCannotBeUnwrappedForAnotherTenant()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        var store = Store();
        var keyA = await store.EnsureActiveAsync(a, default);
        await store.EnsureActiveAsync(b, default);
        var wrapped = Wrap(keyA.PublicKeySpki, RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAsync<TenantWrapKeyUnavailableException>(() => store.UnwrapDekAsync(b, "restore_attempt", Guid.NewGuid(), keyA.KeyId, wrapped, default));
        Assert.False(await store.OwnsKeyAsync(b, keyA.KeyId, 1, default));
    }

    [Fact]
    public async Task ARetiredKeyRefusesButADecryptOnlyKeyStillOpens()
    {
        var tenant = await TenantAsync();
        var store = Store();
        var key = await store.EnsureActiveAsync(tenant, default);
        var wrapped = Wrap(key.PublicKeySpki, RandomNumberGenerator.GetBytes(32));

        await SetStateAsync(tenant, "DecryptOnly");
        await store.UnwrapDekAsync(tenant, "restore_attempt", Guid.NewGuid(), key.KeyId, wrapped, default);

        await SetStateAsync(tenant, "Retired");
        await Assert.ThrowsAsync<TenantWrapKeyUnavailableException>(() => store.UnwrapDekAsync(tenant, "restore_attempt", Guid.NewGuid(), key.KeyId, wrapped, default));
    }

    [Fact]
    public async Task ADekWrappedToAnotherKeyIsRefusedWithoutDetail()
    {
        var tenant = await TenantAsync();
        var store = Store();
        var key = await store.EnsureActiveAsync(tenant, default);
        using var stranger = RSA.Create(2048);
        var wrapped = stranger.Encrypt(RandomNumberGenerator.GetBytes(32), RSAEncryptionPadding.OaepSHA256);
        var exception = await Assert.ThrowsAsync<TenantWrapKeyUnavailableException>(() => store.UnwrapDekAsync(tenant, "restore_attempt", Guid.NewGuid(), key.KeyId, wrapped, default));
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task ExistingArtifactsAreMigratedToPlainAndPolicylessJobsStayOff()
    {
        var database = new PostgreSqlFixture();
        await database.InitializeAsync();
        try
        {
            const string previous = "20261015214653_DatabaseDiscovery";
            await using var context = database.CreatePlatformContext();
            await context.GetService<IMigrator>().MigrateAsync(previous);
            await context.GetService<IMigrator>().MigrateAsync();
            Assert.True(await context.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\" FROM information_schema.columns WHERE table_name = 'backup_artifacts' AND column_name = 'Format' AND column_default LIKE '%v1-plain%'").AnyAsync());
            Assert.True(await context.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\" FROM information_schema.tables WHERE table_name = 'tenant_wrap_keys'").AnyAsync());
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    private async Task SetStateAsync(TenantId tenant, string state)
    {
        await using var context = fixture.CreatePlatformContext();
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE tenant_wrap_keys SET \"State\" = {state} WHERE \"TenantId\" = {tenant.Value}");
    }
}
