using System.Security.Cryptography;
using System.Text;
using BackupPlatform.Encryption;

namespace BackupPlatform.UnitTests.Encryption;

/// <summary>Key-loss and recipient-selection scenarios of the plan (E1 section 5): which key material can, and cannot, open an artifact.</summary>
public sealed class BpeRecoveryTests
{
    private static readonly BpeRecoveryCeremony Ceremony = BpeRecoveryKey.Generate(4096, 3, 5);
    private static readonly RSA TenantA = RSA.Create(4096);
    private static readonly RSA TenantB = RSA.Create(4096);
    private static readonly RSA TenantRecovery = RSA.Create(4096);

    private static RSA RecoveryPublic()
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(Ceremony.PublicKeyPem);
        return rsa;
    }

    private static async Task<byte[]> SealAsync(byte[] data, RSA tenantWrap, bool withTenantRecovery = false)
    {
        using var recovery = RecoveryPublic();
        var recipients = new List<BpeRecipient> { new(BpeRecipientType.TenantWrap, 1, tenantWrap), new(BpeRecipientType.PlatformRecovery, 1, recovery) };
        if (withTenantRecovery)
        {
            recipients.Add(new BpeRecipient(BpeRecipientType.TenantRecovery, 1, TenantRecovery));
        }

        using var output = new MemoryStream();
        await BpeEncryptor.EncryptAsync(new MemoryStream(data), output, data.Length, recipients, new BpeEncryptOptions(1024));
        return output.ToArray();
    }

    private static async Task<byte[]> OpenAsync(byte[] artifact, RSA key)
    {
        using var output = new MemoryStream();
        await BpeDecryptor.DecryptAsync(new MemoryStream(artifact), output, key);
        return output.ToArray();
    }

    [Fact]
    public async Task TenantWrapPrivateKeyMissingAndRecoveryKeyMissingCannotDecrypt()
    {
        var artifact = await SealAsync(RandomNumberGenerator.GetBytes(3000), TenantA);

        // No TWK-priv (an unrelated key stands in for "whatever is left"), and the RK is not reconstructible from fewer than 3 shares.
        await Assert.ThrowsAsync<BpeFormatException>(() => OpenAsync(artifact, TenantB));
        Assert.Throws<BpeShareException>(() => BpeRecoveryKey.FromShares([.. Ceremony.Shares.Take(2)]));
        // Shares of an unrelated ceremony do not help either.
        var other = BpeRecoveryKey.Generate(4096, 3, 5);
        using var wrongRecovery = BpeRecoveryKey.FromShares([.. other.Shares.Take(3)]);
        await Assert.ThrowsAsync<BpeFormatException>(() => OpenAsync(artifact, wrongRecovery));
    }

    [Fact]
    public async Task TenantWrapPrivateKeyMissingButRecoveryKeyPresentDecrypts()
    {
        var data = RandomNumberGenerator.GetBytes(3000);
        var artifact = await SealAsync(data, TenantA);

        using var recovery = BpeRecoveryKey.FromShares([Ceremony.Shares[4], Ceremony.Shares[1], Ceremony.Shares[2]]);

        Assert.Equal(data, await OpenAsync(artifact, recovery));
    }

    [Fact]
    public async Task TenantARecipientKeyCannotOpenTenantBArtifact()
    {
        var artifact = await SealAsync(RandomNumberGenerator.GetBytes(100), TenantB);

        await Assert.ThrowsAsync<BpeFormatException>(() => OpenAsync(artifact, TenantA));
    }

    [Fact]
    public async Task TenantRecoveryRecipientIsSelectedByItsKeyId()
    {
        var data = RandomNumberGenerator.GetBytes(2048);
        var artifact = await SealAsync(data, TenantA, withTenantRecovery: true);

        Assert.Equal(data, await OpenAsync(artifact, TenantRecovery));
        Assert.Equal(
            [BpeRecipientType.TenantWrap, BpeRecipientType.PlatformRecovery, BpeRecipientType.TenantRecovery],
            (await BpeDecryptor.ReadRecipientsAsync(new MemoryStream(artifact))).Select(recipient => recipient.Type));
    }

    [Fact]
    public async Task ATamperedKeyIdMatchesNoRecipient()
    {
        var artifact = await SealAsync(RandomNumberGenerator.GetBytes(100), TenantA);
        var keyId = Encoding.ASCII.GetBytes(BpeFormat.KeyId(TenantA));
        var at = artifact.AsSpan().IndexOf(keyId);
        artifact[at + keyId.Length - 1] = (byte)(artifact[at + keyId.Length - 1] == '0' ? '1' : '0');

        var failure = await Assert.ThrowsAsync<BpeFormatException>(() => OpenAsync(artifact, TenantA));
        Assert.Contains("No recipient matches", failure.Message);
    }

    [Fact]
    public async Task LastChunkTagFlipIsRejected()
    {
        var artifact = await SealAsync(RandomNumberGenerator.GetBytes(2500), TenantA);
        artifact[^1] ^= 0x80;

        await Assert.ThrowsAsync<BpeFormatException>(() => OpenAsync(artifact, TenantA));
    }

    [Fact]
    public void CeremonyReturnsTheKeyIdOfThePublicKeyAndOnlyShares()
    {
        using var recovery = RecoveryPublic();

        Assert.Equal(BpeFormat.KeyId(recovery), Ceremony.KeyId);
        Assert.Equal(5, Ceremony.Shares.Count);
        Assert.Equal(3, Ceremony.Threshold);
        Assert.DoesNotContain("PRIVATE", Ceremony.PublicKeyPem);
    }

    [Fact]
    public void RecoveryKeysBelowThe4096BitFloorAreRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => BpeRecoveryKey.Generate(3072));
}
