using System.Collections;

namespace BackupPlatform.StorageProviders.Abstractions;

/// <summary>
/// How bytes reach the storage. <see cref="AgentDirect"/> means the agent accesses the provider itself;
/// <see cref="BackendRelayed"/> means the backend streams data to and from the provider.
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

/// <summary>The storage capability identifiers. A provider lists exactly those it genuinely supports.</summary>
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
    /// <summary>A conflicting existing object fails with <see cref="StorageFailureKind.AlreadyExists"/> and is untouched. Providers may adopt an identical owned object idempotently.</summary>
    Fail = 0,

    /// <summary>The new content replaces the old atomically, only after it was received and validated completely.</summary>
    Replace
}

/// <summary>
/// Conditions of a write. When <see cref="ExpectedChecksum"/> or <see cref="ExpectedLength"/> is set, a successful
/// write must match the bytes actually received. A failed write may retain owned data requiring cleanup.
/// </summary>
/// <param name="TransferAllowance">
/// Extra time the provider's limit on the TOTAL duration of the transfer is widened by, because the sender is deliberately held to a
/// bandwidth limit and a slow-by-policy upload must not be mistaken for a stalled one. Idle, per-request and connection limits are not
/// widened. Zero (the default) changes nothing.
/// </param>
public sealed record StoragePutOptions(
    StorageOverwritePolicy Overwrite,
    ContentChecksum? ExpectedChecksum = null,
    long? ExpectedLength = null,
    TimeSpan TransferAllowance = default);

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
    NotSupported,
    HostKeyMismatch,
    AuthenticationFailed,
    Timeout,
    NetworkDenied,
    NetworkUnavailable
}

/// <summary>
/// A storage failure whose <see cref="Exception.Message"/> is safe to show to a user: it never contains a file system path or a
/// credential. The underlying exception, when there is one, is the inner exception, for logs only. Caller cancellation is never
/// wrapped: it surfaces as <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class StorageException : Exception
{
    public StorageException(StorageFailureKind kind, string userMessage, Exception? innerException = null,
        bool objectMayRemain = false, StorageObjectKey? remainingObjectKey = null)
        : base(userMessage, innerException)
    {
        Kind = kind;
        ObjectMayRemain = objectMayRemain;
        RemainingObjectKey = remainingObjectKey;
    }

    public StorageFailureKind Kind { get; }
    public bool ObjectMayRemain { get; }
    public StorageObjectKey? RemainingObjectKey { get; }
}

/// <summary>
/// Storage operations for backup, restore and maintenance. Reads and writes stream without whole-object buffering,
/// honour cancellation, and use <see cref="StorageException"/> for expected failures. Capabilities describe supported operations.
/// </summary>
public interface IStorageProvider
{
    StorageProviderId Id { get; }

    StorageTopology Topology { get; }

    StorageCapabilities Capabilities { get; }

    /// <summary>
    /// Stores <paramref name="content"/> under <paramref name="key"/> and validates the supplied length/checksum expectations before success.
    /// Failed or cancelled writes may retain owned data: <see cref="StorageException.ObjectMayRemain"/> and
    /// <see cref="StorageException.RemainingObjectKey"/> identify cleanup obligations. Caller cancellation remains
    /// <see cref="OperationCanceledException"/> and may carry that storage failure in <see cref="Exception.Data"/>.
    /// With <see cref="StorageOverwritePolicy.Fail"/>, an existing published object is never overwritten or deleted.
    /// <paramref name="progress"/> receives the cumulative bytes written.
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
    /// modified BEFORE <paramref name="modifiedBefore"/>. Younger entries are never returned; callers must still respect
    /// active-operation ownership and <see cref="StorageEntry.InUse"/> before deleting an entry. Only entries whose location
    /// is expressible as a <see cref="StorageObjectKey"/> are returned; anything else in the storage is silently left out.
    /// Links are never followed or returned. A missing prefix lists nothing. Providers without <see cref="StorageCapabilityIds.Enumerate"/> throw <see cref="StorageException"/> with
    /// <see cref="StorageFailureKind.NotSupported"/>. This exists for maintenance sweeps (orphan and unpublished-upload cleanup); it is not a browsing API.
    /// </summary>
    IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken);
}
