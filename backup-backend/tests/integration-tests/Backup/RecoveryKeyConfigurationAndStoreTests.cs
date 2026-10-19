using System.Security.Cryptography;
using BackupPlatform.Api.Backups;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Encryption;
using BackupPlatform.Infrastructure.Backups;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BackupPlatform.IntegrationTests.Backup;

/// <summary>The one door for recovery public keys (platform configuration and tenant API): what passes, what is refused, and that nothing secret is stored, audited or echoed.</summary>
public sealed class RecoveryKeyConfigurationAndStoreTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static string PublicPem(int bits, out string keyId)
    {
        using var rsa = RSA.Create(bits);
        keyId = BpeKeyId.FromSpki(rsa.ExportSubjectPublicKeyInfo());
        return rsa.ExportSubjectPublicKeyInfoPem();
    }

    private static InvalidRecoveryKeyException Refused(string? pem) => Assert.Throws<InvalidRecoveryKeyException>(() => RecoveryPublicKeyParser.Parse(pem));

    [Theory]
    [InlineData(4096)]
    public void ARsaPublicKeyOfAtLeast4096BitsIsAcceptedAndItsIdIsTheSpkiHash(int bits)
    {
        var pem = PublicPem(bits, out var keyId);

        var (spki, parsedId) = RecoveryPublicKeyParser.Parse(pem);

        Assert.Equal(keyId, parsedId);
        Assert.Equal(BpeKeyId.FromSpki(spki), parsedId);
    }

    [Fact]
    public void APkcs1RsaPublicKeyIsNormalizedToTheSameSpkiAndId()
    {
        using var rsa = RSA.Create(4096);

        var (_, keyId) = RecoveryPublicKeyParser.Parse(rsa.ExportRSAPublicKeyPem());

        Assert.Equal(BpeKeyId.FromSpki(rsa.ExportSubjectPublicKeyInfo()), keyId);
    }

    [Fact]
    public void APrivateKeyIsRejectedWithoutBeingParsedOrEchoed()
    {
        using var rsa = RSA.Create(4096);
        var privatePem = rsa.ExportPkcs8PrivateKeyPem();

        var exception = Refused(privatePem + rsa.ExportSubjectPublicKeyInfoPem());

        Assert.Equal("private_key_rejected", exception.Code);
        Assert.DoesNotContain(privatePem[40..100], exception.Message);
    }

    [Theory]
    [InlineData(3072, "too_weak")]
    [InlineData(2048, "too_weak")]
    [InlineData(1024, "too_weak")]
    public void AWeakRsaKeyIsRefused(int bits, string code) => Assert.Equal(code, Refused(PublicPem(bits, out _)).Code);

    [Fact]
    public void ANonRsaPublicKeyIsRefused()
    {
        using var ec = ECDsa.Create();
        Assert.Equal("not_rsa", Refused(ec.ExportSubjectPublicKeyInfoPem()).Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a pem")]
    [InlineData("-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----")]
    [InlineData("-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----")]
    public void AnythingElseIsNotAPublicKey(string? pem) => Assert.Equal("not_a_public_key", Refused(pem).Code);

    [Fact]
    public void TwoPemBlocksAreNotOneKey()
    {
        var pem = PublicPem(4096, out _);
        Assert.Equal("not_a_public_key", Refused(pem + "\n" + PublicPem(4096, out _)).Code);
    }

    // ---- platform recovery configuration ----------------------------------------------------------------------------------------------------

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void WithoutAConfiguredKeyThereIsNoPlatformRecoveryRecipient()
    {
        Assert.Null(ArtifactRecoveryConfiguration.Load(Config()).PlatformRecovery);
        Assert.Null(ArtifactRecoveryConfiguration.Load(Config((ArtifactRecoveryConfiguration.PemKey, "  "))).PlatformRecovery);
    }

    [Fact]
    public void AConfiguredPublicKeyBecomesTheRecipientWithItsVersionAndDerivedId()
    {
        var pem = PublicPem(4096, out var keyId);

        var key = ArtifactRecoveryConfiguration.Load(Config((ArtifactRecoveryConfiguration.PemKey, pem), (ArtifactRecoveryConfiguration.VersionKey, "4"))).PlatformRecovery!;

        Assert.Equal((4, keyId), (key.Version, key.KeyId));
        Assert.Equal(keyId, BpeKeyId.FromSpki(key.PublicKeySpki));
    }

    [Fact]
    public void AnEnvironmentStylePemWithEscapedNewlinesIsAccepted()
    {
        var pem = PublicPem(4096, out var keyId).Replace("\r", "").Replace("\n", "\\n");

        Assert.Equal(keyId, ArtifactRecoveryConfiguration.Load(Config((ArtifactRecoveryConfiguration.PemKey, pem))).PlatformRecovery!.KeyId);
    }

    [Fact]
    public void AnUnusableConfiguredKeyFailsStartupWithoutTheKeyInTheMessage()
    {
        using var rsa = RSA.Create(4096);
        var privatePem = rsa.ExportPkcs8PrivateKeyPem();

        var privateFailure = Assert.Throws<InvalidOperationException>(() => ArtifactRecoveryConfiguration.Load(Config((ArtifactRecoveryConfiguration.PemKey, privatePem))));
        var weakFailure = Assert.Throws<InvalidOperationException>(() => ArtifactRecoveryConfiguration.Load(Config((ArtifactRecoveryConfiguration.PemKey, PublicPem(2048, out _)))));
        var versionFailure = Assert.Throws<InvalidOperationException>(() => ArtifactRecoveryConfiguration.Load(
            Config((ArtifactRecoveryConfiguration.PemKey, PublicPem(4096, out _)), (ArtifactRecoveryConfiguration.VersionKey, "0"))));

        Assert.Contains("private_key_rejected", privateFailure.Message);
        Assert.Contains("too_weak", weakFailure.Message);
        Assert.DoesNotContain(privatePem[40..100], privateFailure.Message);
        Assert.Contains("must be at least 1", versionFailure.Message);
    }

    // ---- tenant recovery key store ------------------------------------------------------------------------------------------------------------

    private PostgreSqlTenantRecoveryKeyStore Store() => new(fixture.Options, TimeProvider.System);

    private async Task<TenantId> TenantAsync()
    {
        var tenant = new Tenant(TenantId.New(), $"Tenant {Guid.NewGuid():N}");
        await using var context = fixture.CreatePlatformContext();
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<UserId> SeededActorAsync()
    {
        var user = new User(UserId.New(), "Actor");
        await using var context = fixture.CreatePlatformContext();
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task SettingReplacingAndRemovingTheTenantRecoveryKeyVersionsAndAuditsIdsOnly()
    {
        var tenant = await TenantAsync();
        var actor = await SeededActorAsync();
        var store = Store();
        var first = PublicPem(4096, out var firstId);
        var second = PublicPem(4096, out var secondId);

        var set = await store.SetAsync(tenant, actor, first, default);
        var replaced = await store.SetAsync(tenant, actor, second, default);

        Assert.Equal((1, firstId), (set.Version, set.KeyId));
        Assert.Equal((2, secondId), (replaced.Version, replaced.KeyId));
        Assert.Equal(secondId, (await store.GetAsync(tenant, default))!.KeyId);
        Assert.True(await store.RemoveAsync(tenant, actor, default));
        Assert.False(await store.RemoveAsync(tenant, actor, default));
        Assert.Null(await store.GetAsync(tenant, default));

        await using var context = fixture.CreatePlatformContext();
        var events = await context.AuditEvents.AsNoTracking().Where(e => e.TenantId == tenant).OrderBy(e => e.At).ToListAsync();
        Assert.Equal([AuditActions.TenantRecoveryKeySet, AuditActions.TenantRecoveryKeySet, AuditActions.TenantRecoveryKeyRemoved], events.Select(e => e.Action));
        Assert.All(events, e => Assert.Equal(actor, e.ActorUserId));
        var everything = string.Concat(events.Select(e => e.DetailsJson + e.TargetId));
        Assert.Contains(firstId, everything);
        Assert.DoesNotContain("PUBLIC KEY", everything);
        Assert.DoesNotContain(first.Split('\n')[1], everything);
    }

    [Fact]
    public async Task AnInvalidKeyStoresAndAuditsNothing()
    {
        var tenant = await TenantAsync();
        var actor = await SeededActorAsync();
        using var rsa = RSA.Create(4096);

        await Assert.ThrowsAsync<InvalidRecoveryKeyException>(() => Store().SetAsync(tenant, actor, rsa.ExportPkcs8PrivateKeyPem(), default));
        await Assert.ThrowsAsync<InvalidRecoveryKeyException>(() => Store().SetAsync(tenant, actor, PublicPem(2048, out _), default));

        Assert.Null(await Store().GetAsync(tenant, default));
        await using var context = fixture.CreatePlatformContext();
        Assert.Empty(await context.AuditEvents.AsNoTracking().Where(e => e.TenantId == tenant).ToListAsync());
    }

    [Fact]
    public async Task OneTenantsRecoveryKeyIsInvisibleToAnotherTenant()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        var actor = await SeededActorAsync();
        await Store().SetAsync(a, actor, PublicPem(4096, out _), default);

        Assert.Null(await Store().GetAsync(b, default));
        Assert.False(await Store().RemoveAsync(b, actor, default));
        Assert.NotNull(await Store().GetAsync(a, default));
    }
}
