using System.Security.Cryptography;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.UnitTests.Storage;

public sealed class ContentHasherTests
{
    private const int MiB = 1024 * 1024;

    [Fact]
    public async Task HashesTheKnownSha256TestVector()
    {
        using var stream = new MemoryStream("abc"u8.ToArray());

        var checksum = await ContentHasher.ComputeSha256Async(stream, null, CancellationToken.None);

        Assert.Equal(ChecksumAlgorithm.Sha256, checksum.Algorithm);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", checksum.Hex);
    }

    [Fact]
    public async Task HashesAnEmptyStream()
    {
        var checksum = await ContentHasher.ComputeSha256Async(Stream.Null, null, CancellationToken.None);

        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", checksum.Hex);
    }

    [Fact]
    public async Task HashesAMultiBlockStreamLikeTheOneShotHash()
    {
        var data = new byte[(2 * MiB) + 12345];
        new Random(42).NextBytes(data);
        using var stream = new MemoryStream(data);

        var checksum = await ContentHasher.ComputeSha256Async(stream, null, CancellationToken.None);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), checksum.Hex);
    }

    [Fact]
    public async Task HashesAStreamThatReturnsTinyReads()
    {
        var data = new byte[5000];
        new Random(7).NextBytes(data);
        await using var stream = new TrickleStream(data, 13);

        var checksum = await ContentHasher.ComputeSha256Async(stream, null, CancellationToken.None);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), checksum.Hex);
    }

    [Fact]
    public async Task ReportsCumulativeProgressAfterEveryBlock()
    {
        var data = new byte[(2 * MiB) + 10];
        using var stream = new MemoryStream(data);
        var reports = new RecordingProgress();

        await ContentHasher.ComputeSha256Async(stream, reports, CancellationToken.None);

        Assert.Equal([MiB, 2L * MiB, (2L * MiB) + 10], reports.Values);
    }

    [Fact]
    public async Task HonoursCancellationBetweenBlocks()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new MemoryStream(new byte[3 * MiB]);
        var reports = new RecordingProgress(onReport: _ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ContentHasher.ComputeSha256Async(stream, reports, cancellation.Token));

        Assert.Single(reports.Values);
    }

    [Fact]
    public async Task AlreadyCancelledTokenStopsBeforeReading()
    {
        using var stream = new MemoryStream(new byte[10]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ContentHasher.ComputeSha256Async(stream, null, new CancellationToken(true)));

        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task NullStreamIsRejected() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() => ContentHasher.ComputeSha256Async(null!, null, CancellationToken.None));

    private sealed class RecordingProgress(Action<long>? onReport = null) : IProgress<long>
    {
        public List<long> Values { get; } = [];

        public void Report(long value)
        {
            Values.Add(value);
            onReport?.Invoke(value);
        }
    }

    private sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
    }
}
