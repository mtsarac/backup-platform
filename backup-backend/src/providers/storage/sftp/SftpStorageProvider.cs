using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using BackupPlatform.StorageProviders.Abstractions;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace BackupPlatform.StorageProviders.Sftp;

public sealed class SftpStorageProvider : IStorageProvider
{
    public static StorageProviderId ProviderId { get; } = new("sftp");

    public static StorageCapabilities SftpCapabilities { get; } = new([
        StorageCapabilityIds.Read,
        StorageCapabilityIds.Delete,
        StorageCapabilityIds.AtomicPublish,
        StorageCapabilityIds.Resume,
        StorageCapabilityIds.VerifyAbsence
    ]);

    private const int StreamBufferSize = 64 * 1024;

    private readonly SftpStorageOptions _options;
    private readonly SftpNetworkOptions _network;
    private readonly SftpPathResolver _pathResolver;
    private readonly SftpSessionManager _sessionManager;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyedLocks = new(StringComparer.Ordinal);

    public SftpStorageProvider(
        SftpStorageOptions options,
        Func<CancellationToken, ValueTask<SftpCredential>> credentials,
        SftpNetworkOptions? network = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentials);

        _network = network ?? new SftpNetworkOptions();
        SftpConfiguration.Validate(options, _network);

        _options = options;
        _pathResolver = new SftpPathResolver(options);
        _sessionManager = new SftpSessionManager(options, credentials, _network);
    }

    public StorageProviderId Id => ProviderId;

    public StorageTopology Topology => StorageTopology.BackendRelayed;

    public StorageCapabilities Capabilities => SftpCapabilities;

    public async Task<StorageObjectMetadata> PutAsync(
        StorageObjectKey key,
        Stream content,
        StoragePutOptions options,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);

        if (!content.CanRead)
        {
            throw new ArgumentException("The content stream must be readable.", nameof(content));
        }

        if (options.ExpectedLength is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The expected length must not be negative.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var finalRemotePath = _pathResolver.ResolveCheckedRemotePath(key);
        var tempRemotePath = SftpPathResolver.DeterministicTempPath(finalRemotePath);

        var keyLock = _keyedLocks.GetOrAdd(finalRemotePath, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        SftpClient? client = null;
        var uploadedTemp = false;
        var published = false;
        try
        {
            client = await _sessionManager.CreateConnectedClientAsync(cancellationToken).ConfigureAwait(false);
            // Verify existing target
            var existing = await TryGetItemAsync(client, finalRemotePath, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (options.Overwrite == StorageOverwritePolicy.Fail)
                {
                    // Check if existing file matches expected checksum (idempotency)
                    if (options.ExpectedChecksum is not null)
                    {
                        var verified = await VerifyInternalAsync(client, key, finalRemotePath, new StorageVerificationExpectation(existing.Attributes.Size, options.ExpectedChecksum), cancellationToken).ConfigureAwait(false);
                        if (verified.Status == StorageVerificationStatus.Verified)
                        {
                            return new StorageObjectMetadata(
                                new StorageLocation(Id, key),
                                existing.Attributes.Size,
                                options.ExpectedChecksum,
                                new DateTimeOffset(existing.LastWriteTimeUtc, TimeSpan.Zero));
                        }
                    }

                    throw new StorageException(StorageFailureKind.AlreadyExists, "Target object already exists.");
                }

                if (existing.IsSymbolicLink || existing.IsDirectory)
                {
                    throw new StorageException(StorageFailureKind.AccessDenied, "Existing target is not a regular file.");
                }
            }

            // A retained partial is trusted only after comparing every prefix byte with the immutable source.
            var existingTemp = await TryGetItemAsync(client, tempRemotePath, cancellationToken).ConfigureAwait(false);
            if (existingTemp is not null)
            {
                if (existingTemp.IsSymbolicLink || existingTemp.IsDirectory)
                {
                    throw new StorageException(StorageFailureKind.AccessDenied, "Stale partial upload is not a regular file.");
                }

                if (options.RecordCheckpointAsync is null) await client.DeleteFileAsync(tempRemotePath, cancellationToken).ConfigureAwait(false);
            }

            // Ensure parent directory exists
            var directoryPath = finalRemotePath[..finalRemotePath.LastIndexOf('/')];
            await EnsureDirectoryRecursiveAsync(client, directoryPath, cancellationToken).ConfigureAwait(false);

            // Stream content to tempRemotePath with SHA256 computation
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long totalWritten = 0;
            if (options.RecordCheckpointAsync is not null && existingTemp is not null)
            {
                if (existingTemp.Attributes.Size > options.ExpectedLength)
                    throw new StorageException(StorageFailureKind.IntegrityMismatch, "The retained SFTP prefix cannot be verified.");
                if (content.CanSeek) content.Position = 0;
                await using var prefix = await client.OpenAsync(tempRemotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
                var local = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
                var remote = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
                try
                {
                    while (totalWritten < existingTemp.Attributes.Size)
                    {
                        var count = (int)Math.Min(StreamBufferSize, existingTemp.Attributes.Size - totalWritten);
                        await content.ReadExactlyAsync(local.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        await prefix.ReadExactlyAsync(remote.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        if (!CryptographicOperations.FixedTimeEquals(local.AsSpan(0, count), remote.AsSpan(0, count)))
                            throw new StorageException(StorageFailureKind.IntegrityMismatch, "The retained SFTP prefix does not match the artifact.");
                        hasher.AppendData(local, 0, count);
                        totalWritten += count;
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(local); ArrayPool<byte>.Shared.Return(remote); }
                await options.RecordCheckpointAsync(new(totalWritten, "sftp-v1"), cancellationToken).ConfigureAwait(false);
            }

            using (var transferCts = new CancellationTokenSource(_network.TransferTimeout + options.TransferAllowance))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transferCts.Token))
            {
                await using (var remoteStream = await client.OpenAsync(tempRemotePath,
                    options.RecordCheckpointAsync is not null && existingTemp is not null ? FileMode.Open : FileMode.CreateNew, FileAccess.Write, linkedCts.Token).ConfigureAwait(false))
                {
                    uploadedTemp = true;
                    remoteStream.Position = totalWritten;
                    var buffer = ArrayPool<byte>.Shared.Rent(StreamBufferSize);
                    try
                    {
                        int bytesRead;
                        while ((bytesRead = await content.ReadAsync(buffer.AsMemory(0, StreamBufferSize), linkedCts.Token).ConfigureAwait(false)) > 0)
                        {
                            await remoteStream.WriteAsync(buffer.AsMemory(0, bytesRead), linkedCts.Token).ConfigureAwait(false);
                            hasher.AppendData(buffer, 0, bytesRead);
                            totalWritten += bytesRead;
                            if (options.RecordCheckpointAsync is { } checkpoint)
                            {
                                await remoteStream.FlushAsync(linkedCts.Token).ConfigureAwait(false);
                                await checkpoint(new(totalWritten, "sftp-v1"), linkedCts.Token).ConfigureAwait(false);
                            }
                            progress?.Report(totalWritten);
                        }

                        await remoteStream.FlushAsync(linkedCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
            }

            var actualChecksum = ContentChecksum.Sha256(hasher.GetHashAndReset());

            if (options.ExpectedLength is not null && options.ExpectedLength.Value != totalWritten)
            {
                throw new StorageException(
                    StorageFailureKind.IntegrityMismatch,
                    $"Uploaded length {totalWritten} does not match expected length {options.ExpectedLength.Value}.");
            }

            if (options.ExpectedChecksum is not null && options.ExpectedChecksum != actualChecksum)
            {
                throw new StorageException(
                    StorageFailureKind.IntegrityMismatch,
                    $"Uploaded checksum {actualChecksum} does not match expected checksum {options.ExpectedChecksum}.");
            }

            // Independent readback verification of temp file before publish
            using (var readbackCts = new CancellationTokenSource(_network.TransferTimeout))
            using (var linkedReadbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, readbackCts.Token))
            {
                await using var readbackStream = await client.OpenAsync(tempRemotePath, FileMode.Open, FileAccess.Read, linkedReadbackCts.Token).ConfigureAwait(false);
                var readbackChecksum = await ContentHasher.ComputeSha256Async(readbackStream, null, linkedReadbackCts.Token).ConfigureAwait(false);
                if (readbackChecksum != actualChecksum)
                {
                    throw new StorageException(
                        StorageFailureKind.IntegrityMismatch,
                        "Readback verification of uploaded file failed.");
                }
            }

            // Atomic rename onto final name
            if (options.Overwrite == StorageOverwritePolicy.Replace && existing is not null)
            {
                await client.DeleteFileAsync(finalRemotePath, cancellationToken).ConfigureAwait(false);
            }

            await client.RenameFileAsync(tempRemotePath, finalRemotePath, cancellationToken).ConfigureAwait(false);
            uploadedTemp = false;
            published = true;

            var finalItem = await TryGetItemAsync(client, finalRemotePath, cancellationToken).ConfigureAwait(false);
            var lastModified = finalItem is not null
                ? new DateTimeOffset(finalItem.LastWriteTimeUtc, TimeSpan.Zero)
                : DateTimeOffset.UtcNow;

            return new StorageObjectMetadata(new StorageLocation(Id, key), totalWritten, actualChecksum, lastModified);
        }
        catch (Exception ex)
        {
            if (uploadedTemp && options.RecordCheckpointAsync is null)
            {
                try
                {
                    await client!.DeleteFileAsync(tempRemotePath, CancellationToken.None).ConfigureAwait(false);
                    uploadedTemp = false;
                }
                catch (SftpPathNotFoundException)
                {
                    uploadedTemp = false;
                }
                catch
                {
                    // Retain the exact temporary key when cleanup cannot prove absence.
                }
            }

            var mapped = SftpExceptionMapper.Map(ex, "Failed to upload object to SFTP.");
            if (uploadedTemp || published)
            {
                var remainingKey = published ? key : new StorageObjectKey(tempRemotePath[(_pathResolver.ConnectionRoot.Length + 1)..]);
                mapped = new StorageException(mapped.Kind, mapped.Message, ex, true, remainingKey);
            }
            if (ex is OperationCanceledException)
            {
                if (mapped.ObjectMayRemain) ex.Data[nameof(StorageException)] = mapped;
                throw;
            }
            throw mapped;
        }
        finally
        {
            client?.Dispose();
            keyLock.Release();
        }
    }

    public async Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        var finalRemotePath = _pathResolver.ResolveCheckedRemotePath(key);
        using var client = await _sessionManager.CreateConnectedClientAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var item = await TryGetItemAsync(client, finalRemotePath, cancellationToken).ConfigureAwait(false);
            return item is not null && !item.IsDirectory && !item.IsSymbolicLink;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw SftpExceptionMapper.Map(ex, "Failed to check object existence on SFTP.");
        }
    }

    public async Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        var finalRemotePath = _pathResolver.ResolveCheckedRemotePath(key);
        using var client = await _sessionManager.CreateConnectedClientAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var item = await TryGetItemAsync(client, finalRemotePath, cancellationToken).ConfigureAwait(false);
            if (item is null || item.IsDirectory || item.IsSymbolicLink)
            {
                return null;
            }

            return new StorageObjectMetadata(
                new StorageLocation(Id, key),
                item.Attributes.Size,
                null,
                new DateTimeOffset(item.LastWriteTimeUtc, TimeSpan.Zero));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw SftpExceptionMapper.Map(ex, "Failed to get object metadata from SFTP.");
        }
    }

    public async Task<StorageVerificationResult> VerifyAsync(
        StorageObjectKey key,
        StorageVerificationExpectation expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(expected);
        cancellationToken.ThrowIfCancellationRequested();

        var finalRemotePath = _pathResolver.ResolveCheckedRemotePath(key);
        using var client = await _sessionManager.CreateConnectedClientAsync(cancellationToken).ConfigureAwait(false);

        return await VerifyInternalAsync(client, key, finalRemotePath, expected, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        var finalRemotePath = _pathResolver.ResolveCheckedRemotePath(key);
        var client = await _sessionManager.CreateConnectedClientAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var item = await TryGetItemAsync(client, finalRemotePath, cancellationToken).ConfigureAwait(false);
            if (item is null || item.IsDirectory || item.IsSymbolicLink)
            {
                client.Dispose();
                throw new StorageException(StorageFailureKind.NotFound, "The stored object does not exist on SFTP.");
            }

            var remoteStream = await client.OpenAsync(finalRemotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false);
            return new SftpReadStream(client, remoteStream, _network.TransferTimeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw SftpExceptionMapper.Map(ex, "Failed to open SFTP object for read.");
        }
    }

    public async Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        var finalRemotePath = _pathResolver.ResolveCheckedRemotePath(key);
        var keyLock = _keyedLocks.GetOrAdd(finalRemotePath, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var client = await _sessionManager.CreateConnectedClientAsync(cancellationToken).ConfigureAwait(false);
            var deleted = await DeleteFileIfPresentAsync(client, finalRemotePath, cancellationToken).ConfigureAwait(false);
            var fileName = key.Segments[^1];
            if (fileName.StartsWith('.') && fileName.EndsWith(".partial", StringComparison.Ordinal))
            {
                return deleted;
            }

            // A crashed upload may retain the atomic-publish alternative instead of the final object.
            var deletedPartial = await DeleteFileIfPresentAsync(client,
                SftpPathResolver.DeterministicTempPath(finalRemotePath), cancellationToken).ConfigureAwait(false);
            return deleted || deletedPartial;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw SftpExceptionMapper.Map(ex, "Failed to delete SFTP object.");
        }
        finally
        {
            keyLock.Release();
        }
    }

    private static async Task<bool> DeleteFileIfPresentAsync(SftpClient client, string remotePath, CancellationToken token)
    {
        var item = await TryGetItemAsync(client, remotePath, token).ConfigureAwait(false);
        if (item is null) return false;
        if (item.IsDirectory || item.IsSymbolicLink)
            throw new StorageException(StorageFailureKind.AccessDenied, "Target to delete is not a regular file.");

        try
        {
            await client.DeleteFileAsync(remotePath, token).ConfigureAwait(false);
        }
        catch (SftpPathNotFoundException)
        {
            // A concurrent remover already established absence.
        }
        if (await TryGetItemAsync(client, remotePath, token).ConfigureAwait(false) is not null)
            throw new StorageException(StorageFailureKind.Io, "File still exists after deletion.");
        return true;
    }

    public IAsyncEnumerable<StorageEntry> EnumerateAsync(
        StorageObjectKey? prefix,
        DateTimeOffset modifiedBefore,
        CancellationToken cancellationToken)
    {
        throw new StorageException(StorageFailureKind.NotSupported, "Enumeration is not supported for SFTP backend relayed storage.");
    }

    private async Task<StorageVerificationResult> VerifyInternalAsync(
        SftpClient client,
        StorageObjectKey key,
        string remotePath,
        StorageVerificationExpectation expected,
        CancellationToken cancellationToken)
    {
        var location = new StorageLocation(Id, key);
        try
        {
            var item = await TryGetItemAsync(client, remotePath, cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                return new StorageVerificationResult(location, StorageVerificationStatus.Missing, StorageVerificationMethod.ReadBack, null, null);
            }

            if (item.IsDirectory || item.IsSymbolicLink)
            {
                return new StorageVerificationResult(location, StorageVerificationStatus.Inconclusive, StorageVerificationMethod.ReadBack, null, null);
            }

            var size = item.Attributes.Size;
            if (size != expected.SizeBytes)
            {
                return new StorageVerificationResult(location, StorageVerificationStatus.SizeMismatch, StorageVerificationMethod.ReadBack, size, null);
            }

            using var transferCts = new CancellationTokenSource(_network.TransferTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transferCts.Token);

            await using var stream = await client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, linkedCts.Token).ConfigureAwait(false);
            var actual = await ContentHasher.ComputeSha256Async(stream, null, linkedCts.Token).ConfigureAwait(false);

            var status = actual == expected.Checksum ? StorageVerificationStatus.Verified : StorageVerificationStatus.ChecksumMismatch;
            return new StorageVerificationResult(location, status, StorageVerificationMethod.ReadBack, size, actual);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw SftpExceptionMapper.Map(ex, "Failed to verify SFTP object.");
        }
    }

    private static async Task<ISftpFile?> TryGetItemAsync(SftpClient client, string remotePath, CancellationToken cancellationToken)
    {
        var lastSlash = remotePath.LastIndexOf('/');
        var parentDir = remotePath[..lastSlash];
        var fileName = remotePath[(lastSlash + 1)..];

        try
        {
            // OPENDIR may report generic failure for a missing parent; LSTAT distinguishes absence from denial.
            if (!await client.ExistsAsync(parentDir, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            await foreach (var item in client.ListDirectoryAsync(parentDir, cancellationToken).ConfigureAwait(false))
            {
                if (item.Name == fileName)
                {
                    return item;
                }
            }

            return null;
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }

    private static async Task EnsureDirectoryRecursiveAsync(SftpClient client, string directoryPath, CancellationToken cancellationToken)
    {
        var segments = directoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = string.Empty;

        foreach (var segment in segments)
        {
            current += "/" + segment;
            try
            {
                var attrs = await client.GetAttributesAsync(current, cancellationToken).ConfigureAwait(false);
                if (attrs.IsSymbolicLink)
                {
                    throw new StorageException(StorageFailureKind.AccessDenied, "Directory path traverses symbolic link.");
                }
            }
            catch (SftpPathNotFoundException)
            {
                try
                {
                    await client.CreateDirectoryAsync(current, cancellationToken).ConfigureAwait(false);
                }
                catch (SftpPermissionDeniedException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Check if created concurrently
                    try
                    {
                        var attrs = await client.GetAttributesAsync(current, cancellationToken).ConfigureAwait(false);
                        if (attrs.IsSymbolicLink)
                        {
                            throw new StorageException(StorageFailureKind.AccessDenied, "Directory path traverses symbolic link.");
                        }
                    }
                    catch
                    {
                        throw;
                    }
                }
            }
        }
    }

    private sealed class SftpReadStream : Stream
    {
        private readonly SftpClient _client;
        private readonly Stream _innerStream;
        private readonly CancellationTokenSource _deadlineCts;
        private readonly CancellationTokenRegistration _callerRegistration;
        private bool _disposed;

        public SftpReadStream(SftpClient client, Stream innerStream, TimeSpan transferTimeout, CancellationToken callerToken)
        {
            _client = client;
            _innerStream = innerStream;
            _deadlineCts = new CancellationTokenSource(transferTimeout);
            _callerRegistration = callerToken.Register(() =>
            {
                try { _deadlineCts.Cancel(); } catch { }
            });
        }

        public override bool CanRead => !_disposed && _innerStream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _innerStream.Length;
        public override long Position
        {
            get => _innerStream.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _innerStream.Flush();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), _deadlineCts.Token).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadlineCts.Token);
            try
            {
                return await _innerStream.ReadAsync(buffer, combined.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_deadlineCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new StorageException(StorageFailureKind.Timeout, "SFTP read operation exceeded deadline.");
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                _callerRegistration.Dispose();
                _deadlineCts.Dispose();
                try { _innerStream.Dispose(); } catch { }
                try { _client.Dispose(); } catch { }
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                _callerRegistration.Dispose();
                _deadlineCts.Dispose();
                try { await _innerStream.DisposeAsync().ConfigureAwait(false); } catch { }
                try { _client.Dispose(); } catch { }
            }

            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
