using System.Security.Cryptography;
using BackupPlatform.Encryption;

namespace BackupPlatform.UnitTests.Encryption;

public sealed class BpeFormatTests
{
    private static readonly RSA TenantKey = RSA.Create(3072);
    private static readonly RSA RecoveryKey = RSA.Create(3072);

    private static BpeRecipient[] Recipients() =>
    [
        new(BpeRecipientType.TenantWrap, 1, TenantKey),
        new(BpeRecipientType.PlatformRecovery, 1, RecoveryKey)
    ];

    private static async Task<byte[]> EncryptAsync(byte[] data, int chunkSize = 1024, BpeEncryptOptions? options = null)
    {
        using var output = new MemoryStream();
        await BpeEncryptor.EncryptAsync(new MemoryStream(data), output, data.Length, Recipients(), options ?? new BpeEncryptOptions(chunkSize));
        return output.ToArray();
    }

    private static async Task<byte[]> DecryptAsync(byte[] artifact, RSA key)
    {
        using var output = new MemoryStream();
        await BpeDecryptor.DecryptAsync(new MemoryStream(artifact), output, key);
        return output.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1024)] // exact chunk boundary
    [InlineData(3 * 1024)]
    [InlineData(3 * 1024 + 7)]
    public async Task RoundTripsThroughEitherRecipient(int size)
    {
        var data = RandomNumberGenerator.GetBytes(size);
        var artifact = await EncryptAsync(data);

        Assert.Equal(data, await DecryptAsync(artifact, TenantKey));
        Assert.Equal(data, await DecryptAsync(artifact, RecoveryKey));
    }

    [Fact]
    public async Task UnrelatedKeyCannotDecrypt()
    {
        var artifact = await EncryptAsync(RandomNumberGenerator.GetBytes(100));
        using var other = RSA.Create(3072);

        await Assert.ThrowsAsync<BpeFormatException>(() => DecryptAsync(artifact, other));
    }

    [Theory]
    [InlineData(1024)] // drops the whole final chunk of a chunk-aligned artifact
    [InlineData(5)]
    public async Task TruncationIsRejected(int cut)
    {
        var artifact = await EncryptAsync(RandomNumberGenerator.GetBytes(3 * 1024));
        var truncated = artifact[..^(cut == 1024 ? 1024 + 16 : cut)];

        await Assert.ThrowsAsync<BpeFormatException>(() => DecryptAsync(truncated, TenantKey));
    }

    [Fact]
    public async Task ReorderedChunksAreRejected()
    {
        var artifact = await EncryptAsync(RandomNumberGenerator.GetBytes(3 * 1024 + 1));
        var chunkStart = artifact.Length - (3 * (1024 + 16) + 17);
        var swapped = (byte[])artifact.Clone();
        artifact.AsSpan(chunkStart + 1040, 1040).CopyTo(swapped.AsSpan(chunkStart));
        artifact.AsSpan(chunkStart, 1040).CopyTo(swapped.AsSpan(chunkStart + 1040));

        await Assert.ThrowsAsync<BpeFormatException>(() => DecryptAsync(swapped, TenantKey));
    }

    [Fact]
    public async Task FlippedBitInAnyRegionIsRejected()
    {
        var artifact = await EncryptAsync(RandomNumberGenerator.GetBytes(2000));
        // core (artifact id), recipients MAC (last header byte), ciphertext
        foreach (var offset in new[] { 12 + 3, 12 + 33 + 4 + RecipientsLength(artifact) + 31, artifact.Length - 40 })
        {
            var tampered = (byte[])artifact.Clone();
            tampered[offset] ^= 1;
            await Assert.ThrowsAsync<BpeFormatException>(() => DecryptAsync(tampered, TenantKey));
        }
    }

    [Fact]
    public async Task DeterministicOptionsProduceStableCoreAndChunks()
    {
        var options = new BpeEncryptOptions(1024, new byte[32], new byte[16], [1, 2, 3, 4]);
        var data = new byte[2500];
        var a = await EncryptAsync(data, options: options);
        var b = await EncryptAsync(data, options: options);

        // Header carries randomized RSA-OAEP wraps; the chunk region must be identical.
        var chunkBytes = 3 * 16 + 2500;
        Assert.Equal(a[^chunkBytes..], b[^chunkBytes..]);
    }

    [Fact]
    public async Task WrongDeclaredSourceSizeFails()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BpeEncryptor.EncryptAsync(new MemoryStream(new byte[10]), new MemoryStream(), 11, Recipients()));
    }

    private static int RecipientsLength(byte[] artifact) =>
        (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(artifact.AsSpan(12 + 33));
}
