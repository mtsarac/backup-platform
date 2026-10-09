using System.Buffers;
using System.Security.Cryptography;

namespace BackupPlatform.StorageProviders.Abstractions.FileSystem;

/// <summary>
/// The shared engine of every provider that stores objects as files below a directory the agent can open (a local disk, a mounted
/// volume, a network share). The concrete providers only decide the identity, the topology and an optional key prefix; all file
/// handling lives here once so it can be reviewed and tested once.
/// <list type="bullet">
/// <item><description><b>Atomic publish.</b> Content is written to a hidden sibling file named <c>.&lt;name&gt;.&lt;guid&gt;.partial</c> in the final directory,
/// flushed to disk, validated against the expected length and checksum, and only then renamed onto the final name. A reader therefore sees
/// the complete object or nothing. Leftovers of a crashed process keep the <c>.partial</c> suffix so a sweeper can recognise them. The directory entry
/// itself is not fsynced; after a power loss the rename may be lost, never half-applied.</description></item>
/// <item><description><b>Containment.</b> A key is mapped to <c>root/prefix/segments</c> and the result must lie strictly inside the root. Because
/// <see cref="StorageObjectKey"/> already forbids <c>..</c>, rooted and drive-qualified keys, this is defence in depth. In addition,
/// no existing directory below the root and no existing target file may be a symbolic link or reparse point: such an entry fails the
/// operation with <see cref="StorageFailureKind.AccessDenied"/> (the layout was tampered with; it is not something a configuration change fixes).
/// The root itself is whatever the operator configured and may be a link. The check is made right before use and is not a defence against an
/// attacker who can concurrently modify the storage tree.</description></item>
/// <item><description><b>Missing root.</b> The root is never created implicitly: an unmounted share would otherwise be replaced by a fresh empty
/// directory on the system disk and backups would silently land there. The constructor validates only the shape (absolute path); every operation
/// checks that the root directory exists and fails with <see cref="StorageFailureKind.InvalidConfiguration"/> otherwise, so a share that vanishes
/// after start-up is detected too. Directories below the root are created on demand.</description></item>
/// <item><description><b>Messages.</b> Exception messages and <see cref="StorageLocation"/>s never contain a file system path.</description></item>
/// </list>
/// Advertised capabilities are exactly <see cref="StorageCapabilityIds.Read"/>, <see cref="StorageCapabilityIds.Delete"/>, <see cref="StorageCapabilityIds.AtomicPublish"/>,
/// <see cref="StorageCapabilityIds.Enumerate"/> and <see cref="StorageCapabilityIds.VerifyAbsence"/>. The last one is honest because a local or
/// mounted file system reports the truth at once: after <see cref="File.Delete(string)"/> returns, <see cref="File.Exists"/> is false (no eventual
/// consistency). Checksums are verified by reading back; resume, multipart, immutability and capacity queries are not implemented.
/// </summary>
public abstract class FileSystemStorageProvider : IStorageProvider
{
    private const int BlockSize = 1024 * 1024;

    /// <summary>
    /// What every file-system provider honestly supports. One definition, so a catalog that describes the provider before an instance exists (a
    /// connection descriptor) can never disagree with the instance.
    /// </summary>
    public static StorageCapabilities FileSystemCapabilities { get; } = new([
        StorageCapabilityIds.Delete,
        StorageCapabilityIds.AtomicPublish,
        StorageCapabilityIds.Enumerate,
        StorageCapabilityIds.VerifyAbsence,
        StorageCapabilityIds.Read
    ]);

    private const string TemporarySuffix = ".partial";
    private const int TemporaryTokenLength = 32;

    private static readonly EnumerationOptions ListOptions = new()
    {
        // The default skips hidden entries, which on Unix means every dot-file: exactly the .partial leftovers a sweep looks for.
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false
    };

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly string _root;
    private readonly string _rootWithSeparator;
    private readonly string[] _keyPrefix;

    /// <param name="id">The identity reported by <see cref="Id"/>.</param>
    /// <param name="topology">How bytes reach the storage.</param>
    /// <param name="rootDirectory">Absolute path of the root directory; it must exist when an operation runs.</param>
    /// <param name="keyPrefix">
    /// Fixed leading path segments (each a valid single <see cref="StorageObjectKey"/> segment) under which every key is stored, for providers that
    /// scope their objects, for example to one tenant. The prefix directories are created on demand and are not part of the key.
    /// </param>
    /// <exception cref="ArgumentException">The root is not an absolute path, or a prefix segment is invalid.</exception>
    protected FileSystemStorageProvider(
        StorageProviderId id,
        StorageTopology topology,
        string rootDirectory,
        IReadOnlyList<string>? keyPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        Id = id;
        Topology = topology;
        _root = FileSystemStorageRoot.Normalize(rootDirectory);
        _rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        _keyPrefix = [.. (keyPrefix ?? []).Select(ValidatePrefixSegment)];
        Capabilities = FileSystemCapabilities;
    }

    public StorageProviderId Id { get; }

    public StorageTopology Topology { get; }

    public StorageCapabilities Capabilities { get; }

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
        var published = false;
        string? temporaryPath = null;
        try
        {
            var finalPath = ResolveCheckedPath(key);
            EnsureKeyIsFree(finalPath, options.Overwrite);

            var directory = Path.GetDirectoryName(finalPath)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, TemporaryName(key.FileName));

            var (length, checksum) = await WriteTemporaryAsync(temporaryPath, content, options, progress, cancellationToken).ConfigureAwait(false);

            Publish(temporaryPath, finalPath, options.Overwrite);
            published = true;
            return new StorageObjectMetadata(new StorageLocation(Id, key), length, checksum, new DateTimeOffset(File.GetLastWriteTimeUtc(finalPath), TimeSpan.Zero));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw StorageIoErrors.Map(exception);
        }
        finally
        {
            if (!published && temporaryPath is not null)
            {
                DeleteQuietly(temporaryPath);
            }
        }
    }

    public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Execute(cancellationToken, () => File.Exists(ResolveCheckedPath(key)));
    }

    /// <summary>
    /// Size and last-modified time come from the file system. <see cref="StorageObjectMetadata.Checksum"/> is always null: stating it would
    /// require reading the whole object again, which is what <see cref="VerifyAsync"/> is for.
    /// </summary>
    public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Execute(cancellationToken, () => ReadMetadata(key, ResolveCheckedPath(key)));
    }

    /// <summary>
    /// A missing object is <see cref="StorageVerificationStatus.Missing"/>; a different size is <see cref="StorageVerificationStatus.SizeMismatch"/>
    /// decided without reading the content; otherwise the stored file is streamed through SHA-256 (<see cref="StorageVerificationMethod.ReadBack"/>).
    /// </summary>
    public async Task<StorageVerificationResult> VerifyAsync(
        StorageObjectKey key,
        StorageVerificationExpectation expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(expected);
        cancellationToken.ThrowIfCancellationRequested();
        var location = new StorageLocation(Id, key);
        try
        {
            var path = ResolveCheckedPath(key);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new StorageVerificationResult(location, StorageVerificationStatus.Missing, StorageVerificationMethod.ReadBack, null, null);
            }

            var size = info.Length;
            if (size != expected.SizeBytes)
            {
                return new StorageVerificationResult(location, StorageVerificationStatus.SizeMismatch, StorageVerificationMethod.ReadBack, size, null);
            }

            await using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 0
            });
            var actual = await ContentHasher.ComputeSha256Async(stream, null, cancellationToken).ConfigureAwait(false);
            var status = actual == expected.Checksum ? StorageVerificationStatus.Verified : StorageVerificationStatus.ChecksumMismatch;
            return new StorageVerificationResult(location, status, StorageVerificationMethod.ReadBack, size, actual);
        }
        catch (FileNotFoundException)
        {
            return new StorageVerificationResult(location, StorageVerificationStatus.Missing, StorageVerificationMethod.ReadBack, null, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw StorageIoErrors.Map(exception);
        }
    }

    /// <summary>Opens the object file for a sequential read under the same containment rules as every other operation. Sharing is read-only so a concurrent verification can read it too.</summary>
    public Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Execute<Stream>(cancellationToken, () =>
        {
            var path = ResolveCheckedPath(key);
            try
            {
                return new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    BufferSize = 0
                });
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new StorageException(StorageFailureKind.NotFound, "The stored object does not exist.", exception);
            }
        });
    }

    /// <summary>Deletes only the object file. Directories created for its key are left in place (they hold no data, and removing them would race with concurrent writes into the same directory).</summary>
    public Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Execute(cancellationToken, () =>
        {
            var path = ResolveCheckedPath(key);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        });
    }

    /// <summary>
    /// Walks the tree below the provider scope (and <paramref name="prefix"/>) iteratively. Below the start directory a link is never followed
    /// or returned; an unreadable subdirectory and a vanished file are skipped so one odd entry cannot abort a sweep, while a problem with the
    /// start directory itself surfaces as a <see cref="StorageException"/>. Files younger than <paramref name="modifiedBefore"/> are not probed.
    /// </summary>
    public async IAsyncEnumerable<StorageEntry> EnumerateAsync(
        StorageObjectKey? prefix,
        DateTimeOffset modifiedBefore,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = ResolveStartDirectory(prefix);
        if (start is null)
        {
            yield break;
        }

        var pending = new Stack<(string Path, string Key, bool IsStart)>();
        pending.Push((start, prefix?.Value ?? string.Empty, true));
        while (pending.Count > 0)
        {
            var (directory, relative, isStart) = pending.Pop();
            var children = ListDirectory(directory, isStart);
            if (children is null)
            {
                continue;
            }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var childKey = relative.Length == 0 ? child.Name : relative + "/" + child.Name;
                if (child is DirectoryInfo)
                {
                    if (StorageObjectKey.TryCreate(childKey, out _))
                    {
                        pending.Push((child.FullName, childKey, false));
                    }

                    continue;
                }

                if (child is FileInfo file && Describe(file, childKey, modifiedBefore) is { } entry)
                {
                    yield return entry;
                }
            }
        }
    }

    private static string ValidatePrefixSegment(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Contains('/'))
        {
            throw new ArgumentException("A key prefix segment must not contain a separator.", nameof(segment));
        }

        return new StorageObjectKey(segment).Value;
    }

    /// <summary>Runs a short synchronous file system operation and reports every outcome through the returned task, as the async contract expects.</summary>
    private static Task<T> Execute<T>(CancellationToken cancellationToken, Func<T> operation)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        try
        {
            return Task.FromResult(operation());
        }
        catch (Exception exception) when (exception is StorageException or IOException or UnauthorizedAccessException)
        {
            return Task.FromException<T>(exception as StorageException ?? StorageIoErrors.Map(exception));
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleanup must never mask the failure that made it necessary; a leftover keeps the .partial suffix for a sweeper.
        }
    }

    /// <summary>The temporary name an upload of <paramref name="finalName"/> is written under; <see cref="IsTemporaryName"/> is its inverse and must stay in sync.</summary>
    private static string TemporaryName(string finalName) => $".{finalName}.{Guid.NewGuid():N}{TemporarySuffix}";

    /// <summary>True for <c>.&lt;finalName&gt;.&lt;32 lowercase hex&gt;.partial</c> with a nonempty final name, i.e. a name <see cref="TemporaryName"/> can produce.</summary>
    private static bool IsTemporaryName(string name)
    {
        const int tailLength = 1 + TemporaryTokenLength + 8;
        if (name.Length < 1 + 1 + tailLength || name[0] != '.' || !name.EndsWith(TemporarySuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var tokenStart = name.Length - TemporarySuffix.Length - TemporaryTokenLength;
        if (name[tokenStart - 1] != '.')
        {
            return false;
        }

        foreach (var c in name.AsSpan(tokenStart, TemporaryTokenLength))
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    private static List<FileSystemInfo>? ListDirectory(string path, bool isStart)
    {
        try
        {
            return [.. new DirectoryInfo(path).EnumerateFileSystemInfos("*", ListOptions)];
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return isStart ? throw StorageIoErrors.Map(exception) : null;
        }
    }

    /// <summary>Null when the file is not to be reported (too young, not ours, vanished or unreadable metadata).</summary>
    private static StorageEntry? Describe(FileInfo file, string key, DateTimeOffset modifiedBefore)
    {
        try
        {
            var modified = file.LastWriteTimeUtc;
            if (modified >= modifiedBefore.UtcDateTime || !StorageObjectKey.TryCreate(key, out var objectKey))
            {
                return null;
            }

            var length = file.Length;
            var partial = IsTemporaryName(file.Name);
            return new StorageEntry(
                objectKey!,
                partial ? StorageEntryKind.UnpublishedUpload : StorageEntryKind.Object,
                length,
                new DateTimeOffset(modified, TimeSpan.Zero),
                partial ? ProbeInUse(file.FullName) : null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The writer holds its temporary file with <see cref="FileShare.None"/>, so failing to open it exclusively means it is still in use.</summary>
    private static bool? ProbeInUse(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException exception) when (exception is not (FileNotFoundException or DirectoryNotFoundException))
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private StorageObjectMetadata? ReadMetadata(StorageObjectKey key, string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new StorageObjectMetadata(new StorageLocation(Id, key), info.Length, null, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero))
                : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Maps a key to its absolute path after checking the root exists, that no existing component is a link and that the result lies strictly inside the root.</summary>
    private string ResolveCheckedPath(StorageObjectKey key) => ResolveCheckedPath([.. _keyPrefix, .. key.Segments]);

    /// <summary>The start directory of an enumeration, or null when it does not exist. Links on the way to it are refused exactly as for a key.</summary>
    private string? ResolveStartDirectory(StorageObjectKey? prefix)
    {
        try
        {
            var path = ResolveCheckedPath(prefix is null ? _keyPrefix : [.. _keyPrefix, .. prefix.Segments]);
            return Directory.Exists(path) ? path : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw StorageIoErrors.Map(exception);
        }
    }

    private string ResolveCheckedPath(string[] segments)
    {
        if (!Directory.Exists(_root))
        {
            throw new StorageException(StorageFailureKind.InvalidConfiguration, "The storage root is not available.");
        }

        var current = _root;
        var inspect = true;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if (!inspect)
            {
                continue;
            }

            if (!TryGetAttributes(current, out var attributes))
            {
                inspect = false;
                continue;
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new StorageException(StorageFailureKind.AccessDenied, "The storage location contains a link, which is never followed.");
            }

            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                inspect = false;
            }
        }

        if (segments.Length == 0)
        {
            return _root;
        }

        var full = Path.GetFullPath(current);
        if (!full.StartsWith(_rootWithSeparator, PathComparison))
        {
            throw new StorageException(StorageFailureKind.AccessDenied, "The storage key resolves outside of the storage root.");
        }

        return full;
    }

    private static void EnsureKeyIsFree(string finalPath, StorageOverwritePolicy overwrite)
    {
        // A directory under the key means the key is a prefix of other objects; replacing it is never intended.
        if (Directory.Exists(finalPath) || (overwrite == StorageOverwritePolicy.Fail && File.Exists(finalPath)))
        {
            throw new StorageException(StorageFailureKind.AlreadyExists, "An object already exists under this key.");
        }
    }

    private static async Task<(long Length, ContentChecksum Checksum)> WriteTemporaryAsync(
        string temporaryPath,
        Stream content,
        StoragePutOptions options,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        await using (var destination = new FileStream(temporaryPath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0
        }))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read;
                    try
                    {
                        read = await content.ReadAsync(buffer.AsMemory(0, BlockSize), cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException exception)
                    {
                        throw new StorageException(StorageFailureKind.Io, "The content to store could not be read.", exception);
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (options.ExpectedLength is { } expectedLength && total > expectedLength)
                    {
                        throw LengthMismatch();
                    }

                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(total);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (options.ExpectedLength is { } expected && total != expected)
            {
                throw LengthMismatch();
            }

            destination.Flush(flushToDisk: true);
        }

        var checksum = ContentChecksum.Sha256(hash.GetHashAndReset());
        if (options.ExpectedChecksum is { } expectedChecksum && expectedChecksum != checksum)
        {
            throw new StorageException(StorageFailureKind.IntegrityMismatch, "The content checksum differs from the expected checksum.");
        }

        return (total, checksum);
    }

    private static StorageException LengthMismatch() =>
        new(StorageFailureKind.IntegrityMismatch, "The content length differs from the expected length.");

    private static void Publish(string temporaryPath, string finalPath, StorageOverwritePolicy overwrite)
    {
        try
        {
            File.Move(temporaryPath, finalPath, overwrite == StorageOverwritePolicy.Replace);
        }
        catch (IOException) when (overwrite == StorageOverwritePolicy.Fail && (File.Exists(finalPath) || Directory.Exists(finalPath)))
        {
            // Another writer published the same key between the early check and the rename.
            throw new StorageException(StorageFailureKind.AlreadyExists, "An object already exists under this key.");
        }
    }
}
