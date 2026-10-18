using System.Security.Cryptography;
using BackupPlatform.Encryption;

namespace BackupPlatform.UnitTests.Encryption;

public sealed class BpeHeaderApiTests
{
    private static readonly RSA TenantKey = RSA.Create(3072);
    private static readonly RSA RecoveryKey = RSA.Create(3072);

    private static async Task<byte[]> SealAsync(byte[] data, byte[] dek)
    {
        using var output = new MemoryStream();
        await BpeEncryptor.EncryptAsync(new MemoryStream(data), output, data.Length,
            [new(BpeRecipientType.TenantWrap, 7, TenantKey), new(BpeRecipientType.PlatformRecovery, 2, RecoveryKey)],
            new BpeEncryptOptions(1024, (byte[])dek.Clone()));
        return output.ToArray();
    }

    private static async Task<byte[]> OpenAsync(byte[] artifact, Func<IReadOnlyList<BpeRecipientEntry>, CancellationToken, Task<byte[]>> unwrap)
    {
        using var plain = new MemoryStream();
        await BpeDecryptor.DecryptAsync(new MemoryStream(artifact), plain, unwrap);
        return plain.ToArray();
    }

    [Fact]
    public async Task ReadRecipientsReturnsEveryEntryWithItsWrappedDekWithoutAnyKey()
    {
        var artifact = await SealAsync(RandomNumberGenerator.GetBytes(10), RandomNumberGenerator.GetBytes(32));

        var recipients = await BpeDecryptor.ReadRecipientsAsync(new MemoryStream(artifact));

        Assert.Equal([BpeRecipientType.TenantWrap, BpeRecipientType.PlatformRecovery], recipients.Select(recipient => recipient.Type));
        Assert.Equal([7, 2], recipients.Select(recipient => recipient.KeyVersion));
        Assert.Equal(BpeFormat.KeyId(TenantKey), recipients[0].KeyId);
        Assert.All(recipients, recipient => Assert.Equal(384, recipient.WrappedDek.Length));
        Assert.Equal(32, TenantKey.Decrypt(recipients[0].WrappedDek, RSAEncryptionPadding.OaepSHA256).Length);
    }

    [Fact]
    public async Task ReadRecipientsRejectsNonArtifacts()
    {
        await Assert.ThrowsAsync<BpeFormatException>(() => BpeDecryptor.ReadRecipientsAsync(new MemoryStream(new byte[64])));
        await Assert.ThrowsAsync<BpeFormatException>(() => BpeDecryptor.ReadRecipientsAsync(new MemoryStream()));
    }

    [Fact]
    public async Task DecryptsWithADekFromTheCallbackAndZeroesIt()
    {
        var data = RandomNumberGenerator.GetBytes(3 * 1024 + 5);
        var dek = RandomNumberGenerator.GetBytes(32);
        var artifact = await SealAsync(data, dek);
        var handedOut = (byte[])dek.Clone();
        IReadOnlyList<BpeRecipientEntry>? seen = null;

        var plain = await OpenAsync(artifact, (recipients, _) =>
        {
            seen = recipients;
            return Task.FromResult(handedOut);
        });

        Assert.Equal(data, plain);
        Assert.Equal(2, seen!.Count);
        Assert.All(handedOut, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task AWrongDekFailsAuthenticationAndIsStillZeroed()
    {
        var artifact = await SealAsync(RandomNumberGenerator.GetBytes(100), RandomNumberGenerator.GetBytes(32));
        var wrong = RandomNumberGenerator.GetBytes(32);

        await Assert.ThrowsAsync<BpeFormatException>(() => OpenAsync(artifact, (_, _) => Task.FromResult(wrong)));

        Assert.All(wrong, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task ADekOfTheWrongLengthIsRejected()
    {
        var artifact = await SealAsync(RandomNumberGenerator.GetBytes(100), RandomNumberGenerator.GetBytes(32));

        await Assert.ThrowsAsync<BpeFormatException>(() => OpenAsync(artifact, (_, _) => Task.FromResult(new byte[16])));
    }

    [Fact]
    public async Task ACallbackFailureIsNotMistakenForATamperedArtifact()
    {
        var artifact = await SealAsync(RandomNumberGenerator.GetBytes(100), RandomNumberGenerator.GetBytes(32));

        await Assert.ThrowsAsync<TimeoutException>(() => OpenAsync(artifact, (_, _) => throw new TimeoutException()));
    }

    [Fact]
    public async Task ATamperedChunkIsStillRejectedWhenTheCallbackSuppliesTheRightDek()
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        var artifact = await SealAsync(RandomNumberGenerator.GetBytes(3 * 1024), dek);
        artifact[^10] ^= 1;

        await Assert.ThrowsAsync<BpeFormatException>(() => OpenAsync(artifact, (_, _) => Task.FromResult((byte[])dek.Clone())));
    }
}
