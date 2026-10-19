using System.IO.Pipes;
using System.Security.Cryptography;
using BackupPlatform.Encryption;

namespace BackupPlatform.UnitTests.Encryption;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StreamingMemoryCollection
{
    public const string Name = "bpe-streaming-memory";
}

/// <summary>
/// Plan E1 section 5: a stream larger than 4 GiB must flow through encrypt and decrypt with memory bounded by the chunk size. Runs alone (no
/// other test allocates concurrently) so the process-wide allocation counter is attributable to the streaming code.
/// </summary>
[Collection(StreamingMemoryCollection.Name)]
[Trait("Category", "Slow")]
public sealed class BpeStreamingMemoryTests
{
    private const long PlaintextSize = (4L << 30) + 12345;
    private const long AllocationBudget = 64L << 20;

    [Fact]
    public async Task MoreThanFourGibStreamsThroughEncryptAndDecryptWithBoundedAllocations()
    {
        using var recipient = RSA.Create(4096);
        using var generated = new PatternStream(PlaintextSize);
        using var server = new AnonymousPipeServerStream(PipeDirection.Out);
        await using var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);
        var sink = new HashingSink();
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);

        var encrypt = Task.Run(async () =>
        {
            try
            {
                await BpeEncryptor.EncryptAsync(generated, server, PlaintextSize, [new BpeRecipient(BpeRecipientType.TenantWrap, 1, recipient)]);
            }
            finally
            {
                server.Dispose();
            }
        });
        // Awaiting both reports an encryptor failure instead of the reader's misleading EOF error.
        await Task.WhenAll(encrypt, BpeDecryptor.DecryptAsync(client, sink, recipient));

        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.Equal(PlaintextSize, sink.Length);
        Assert.Equal(generated.Sha256(), sink.Sha256());
        Assert.True(allocated < AllocationBudget, $"Streaming {PlaintextSize} bytes allocated {allocated} bytes; expected < {AllocationBudget}.");
    }

    /// <summary>Deterministic read-only stream that produces its bytes on demand and hashes what it handed out.</summary>
    private sealed class PatternStream(long length) : Stream
    {
        // A block length that is not a multiple of the chunk size keeps consecutive chunks different without per-byte generation cost.
        private static readonly byte[] Block = MakeBlock();
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long position;

        private static byte[] MakeBlock()
        {
            var block = new byte[(1 << 20) + 7];
            new Random(42).NextBytes(block);
            return block;
        }

        public byte[] Sha256() => hash.GetHashAndReset();

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - position);
            for (var written = 0; written < count;)
            {
                var offset = (int)((position + written) % Block.Length);
                var take = Math.Min(count - written, Block.Length - offset);
                Block.AsSpan(offset, take).CopyTo(buffer[written..]);
                written += take;
            }

            hash.AppendData(buffer[..count]);
            position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Write-only null sink that only hashes and counts.</summary>
    private sealed class HashingSink : Stream
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public byte[] Sha256() => hash.GetHashAndReset();

        private long length;

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            hash.AppendData(buffer);
            length += buffer.Length;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get => length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
