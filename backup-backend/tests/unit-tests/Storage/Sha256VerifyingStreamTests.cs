using System.Security.Cryptography;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.UnitTests.Storage;

public sealed class Sha256VerifyingStreamTests
{
    [Fact]
    public async Task HashesExactlyTheBytesReadAndCountsThem()
    {
        var data = Enumerable.Range(0, 10_000).Select(value => (byte)value).ToArray();
        await using var stream = new Sha256VerifyingStream(new MemoryStream(data));
        var buffer = new byte[4096];
        while (await stream.ReadAsync(buffer) > 0) { }

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), stream.GetHashHex());
        Assert.Equal(data.Length, stream.Position);
    }

    [Fact]
    public void PositionStaysZeroWhenNothingWasRead()
    {
        using var stream = new Sha256VerifyingStream(new MemoryStream([1, 2, 3]));
        Assert.Equal(0, stream.Position);
    }
}
