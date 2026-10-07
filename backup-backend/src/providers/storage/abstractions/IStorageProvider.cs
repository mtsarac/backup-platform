using System.Collections;

namespace BackupPlatform.StorageProviders.Abstractions;

/// <summary>
/// How bytes reach the storage. <see cref="AgentDirect"/>: the agent process itself writes to the storage (a disk or a share the
/// agent can open). <see cref="BackendRelayed"/> is reserved for providers that stream through the backend; it is part of the
/// contract so such a provider can be added without changing callers, and no provider implements it yet.
/// </summary>
public enum StorageTopology
{
    AgentDirect = 1,
    BackendRelayed
}

/// <summary>
/// A canonical storage capability name, using the same grammar as the platform capability identifiers (at least two nonempty
/// dot-separated segments of ASCII lowercase letters, digits and hyphens).
/// </summary>
public sealed record StorageCapabilityId
{
    public StorageCapabilityId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var segments = value.Split('.');
        if (segments.Length < 2
            || segments.Any(segment => segment.Length == 0
                || !segment.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
        {
            throw new ArgumentException(
                "Capability names need at least two nonempty dot-separated segments of ASCII lowercase letters, digits and hyphens.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>The storage capability identifiers (plan §5). A provider lists exactly those it genuinely supports.</summary>
public static class StorageCapabilityIds
{
    public static StorageCapabilityId Delete { get; } = new("storage.delete");
    public static StorageCapabilityId AtomicPublish { get; } = new("storage.atomic-publish");
    public static StorageCapabilityId ServerSideChecksum { get; } = new("storage.server-side-checksum");
    public static StorageCapabilityId CapacityQuery { get; } = new("storage.capacity-query");
    public static StorageCapabilityId Resume { get; } = new("storage.resume");
    public static StorageCapabilityId Enumerate { get; } = new("storage.enumerate");
    /// <summary>After <see cref="IStorageProvider.DeleteAsync"/> returns, <see cref="IStorageProvider.ExistsAsync"/> reports the truth immediately (no eventual consistency), so "deleted" can be proven by checking absence.</summary>
    public static StorageCapabilityId VerifyAbsence { get; } = new("storage.verify-absence");
    public static StorageCapabilityId Multipart { get; } = new("storage.multipart");
    public static StorageCapabilityId ImmutableStorage { get; } = new("storage.immutable");
    /// <summary>The provider can stream a stored object back to the caller (<see cref="IStorageProvider.OpenReadAsync"/>), which a restore needs.</summary>
    public static StorageCapabilityId Read { get; } = new("storage.read");
}

public enum StorageEntryKind
{
    /// <summary>A published object.</summary>
    Object = 1,

    /// <summary>The temporary file of an upload that has not been published (a leftover when the writer died).</summary>
    UnpublishedUpload
}

/// <summary>One entry found by <see cref="IStorageProvider.EnumerateAsync"/>.</summary>
/// <param name="InUse">For an <see cref="StorageEntryKind.UnpublishedUpload"/>: true when a writer still holds it open, false when the provider could prove nobody does, null when it cannot tell. Always null for objects.</param>
public sealed record StorageEntry(StorageObjectKey Key, StorageEntryKind Kind, long SizeBytes, DateTimeOffset LastModifiedUtc, bool? InUse);

/// <summary>The immutable set of capabilities a provider supports; anything not listed is not supported.</summary>
public sealed class StorageCapabilities : IReadOnlyCollection<StorageCapabilityId>
{
    private readonly HashSet<StorageCapabilityId> _supported;

    public StorageCapabilities(IEnumerable<StorageCapabilityId> supported)
    {
        ArgumentNullException.ThrowIfNull(supported);
        _supported = [.. supported];
    }

    public static StorageCapabilities None { get; } = new([]);

    public int Count => _supported.Count;

    public bool Supports(StorageCapabilityId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _supported.Contains(id);
    }

    public IEnumerator<StorageCapabilityId> GetEnumerator() => _supported.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>What <see cref="IStorageProvider.PutAsync"/> does when the key is already taken. There is no implicit default behaviour: the caller states it.</summary>
public enum StorageOverwritePolicy
{
    /// <summary>The write fails with <see cref="StorageFailureKind.AlreadyExists"/> and the existing object is untouched.</summary>
    Fail = 0,

    /// <summary>The new content replaces the old atomically, only after it was received and validated completely.</summary>
    Replace
}

/// <summary>
/// Conditions of a write. When <see cref="ExpectedChecksum"/> or <see cref="ExpectedLength"/> is set, the provider validates the
/// bytes it actually received before the object becomes visible under its key; a mismatch fails the write and leaves nothing behind.
/// </summary>
public sealed record StoragePutOptions(
    StorageOverwritePolicy Overwrite,
    ContentChecksum? ExpectedChecksum = null,
    long? ExpectedLength = null);

/// <summary>A stored object as the provider knows it. <see cref="Checksum"/> is null when the provider cannot state it without reading the content again.</summary>
public sealed record StorageObjectMetadata(
    StorageLocation Location,
    long SizeBytes,
    ContentChecksum? Checksum,
    DateTimeOffset LastModifiedUtc);

public enum StorageVerificationMethod
{
    /// <summary>The provider read the stored bytes back and hashed them.</summary>
    ReadBack = 1,

    /// <summary>The storage service computed or reported the checksum itself, without the content travelling (requires <see cref="StorageCapabilityIds.ServerSideChecksum"/>).</summary>
    ServerSideChecksum,

    /// <summary>Only the size was compared.</summary>
    SizeOnly
}

public enum StorageVerificationStatus
{
    /// <summary>Everything that was expected matched.</summary>
    Verified = 1,
    Missing,
    SizeMismatch,
    ChecksumMismatch,

    /// <summary>The provider could not perform the requested comparison (for example it cannot verify checksums); nothing is claimed.</summary>
    Inconclusive
}

public sealed record StorageVerificationExpectation(long SizeBytes, ContentChecksum Checksum);

public sealed record StorageVerificationResult(
    StorageLocation Location,
    StorageVerificationStatus Status,
    StorageVerificationMethod Method,
    long? ActualSizeBytes,
    ContentChecksum? ActualChecksum);

public enum StorageFailureKind
{
    Unexpected = 0,
    InvalidConfiguration,
    AlreadyExists,
    NotFound,

    /// <summary>The bytes received did not match <see cref="StoragePutOptions.ExpectedChecksum"/> or <see cref="StoragePutOptions.ExpectedLength"/>.</summary>
    IntegrityMismatch,
    InsufficientSpace,
    AccessDenied,

    /// <summary>The storage could not be read or written for another I/O reason, including a source stream that failed while being copied.</summary>
    Io,
    NotSupported
}

/// <summary>
/// A storage failure whose <see cref="Exception.Message"/> is safe to show to a user: it never contains a file system path or a
/// credential. The underlying exception, when there is one, is the inner exception, for logs only. Caller cancellation is never
/// wrapped: it surfaces as <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class StorageException : Exception
{
    public StorageException(StorageFailureKind kind, string userMessage, Exception? innerException = null)
        : base(userMessage, innerException)
    {
        Kind = kind;
    }

    public StorageFailureKind Kind { get; }
}

/// <summary>
/// The MVP storage operations: write, exists, metadata, verify and delete. The interface stays small on purpose; multipart,
/// resume and server-side copy are capabilities a future provider can advertise and callers can discover, not methods every
/// provider must implement. All operations are stream based (no whole-object buffering), honour the token, and use
/// <see cref="StorageException"/> for expected failures.
/// </summary>
public interface IStorageProvider
{
    StorageProviderId Id { get; }

    StorageTopology Topology { get; }

    StorageCapabilities Capabilities { get; }

    /// <summary>
    /// Stores <paramref name="content"/> under <paramref name="key"/>. The object either appears complete or not at all: an
    /// interrupted, cancelled, failed or integrity-mismatching write leaves nothing under the final key and no partial data behind.
    /// <paramref name="progress"/> receives the cumulative bytes written. The returned metadata includes the checksum of the bytes written.
    /// </summary>
    Task<StorageObjectMetadata> PutAsync(
        StorageObjectKey key,
        Stream content,
        StoragePutOptions options,
        IProgress<long>? progress,
        CancellationToken cancellationToken);

    Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken);

    /// <summary>Null when the object does not exist.</summary>
    Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Checks the stored object against <paramref name="expected"/>. It inspects what is stored, independently of how it was written:
    /// a provider without server-side checksums reads the object back.
    /// </summary>
    Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the stored object for one sequential read. The caller owns and disposes the stream; the provider never exposes a path. A missing
    /// object is a <see cref="StorageException"/> with <see cref="StorageFailureKind.NotFound"/>. Providers without <see cref="StorageCapabilityIds.Read"/>
    /// throw <see cref="StorageFailureKind.NotSupported"/> (the default, so a provider that only writes needs no change).
    /// </summary>
    Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken) =>
        Task.FromException<Stream>(new StorageException(StorageFailureKind.NotSupported, "This storage cannot be read."));

    /// <summary>Deletes the object; true if it existed. Providers without <see cref="StorageCapabilityIds.Delete"/> throw <see cref="StorageException"/> with <see cref="StorageFailureKind.NotSupported"/>.</summary>
    Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the entries stored below <paramref name="prefix"/> (the whole provider scope when null), recursively, that were last
    /// modified BEFORE <paramref name="modifiedBefore"/>. Younger entries are never returned, so a caller that only ever acts on old entries
    /// cannot touch something that is still being written. Only entries whose location is expressible as a <see cref="StorageObjectKey"/> are
    /// returned; anything else in the storage is not ours and is silently left out. Links are never followed or returned. A missing prefix
    /// lists nothing. Providers without <see cref="StorageCapabilityIds.Enumerate"/> throw <see cref="StorageException"/> with
    /// <see cref="StorageFailureKind.NotSupported"/>. This exists for maintenance sweeps (orphan and unpublished-upload cleanup); it is not a browsing API.
    /// </summary>
    IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken);
}
