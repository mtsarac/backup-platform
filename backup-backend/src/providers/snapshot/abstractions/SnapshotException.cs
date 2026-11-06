namespace BackupPlatform.SnapshotProviders.Abstractions;

/// <summary>Why a snapshot could not be created or released; each value is reported to the operator as its own cause.</summary>
public enum SnapshotFailureKind
{
    /// <summary>The agent's account lacks the right to take snapshots.</summary>
    AccessDenied,

    /// <summary>The snapshot system refused or failed the operation (no storage, writers not responding, tool error).</summary>
    Failed,

    /// <summary>The configured source or snapshot context cannot be snapshotted.</summary>
    InvalidConfiguration
}

/// <summary>A snapshot failure with a classified cause. The message is fixed and user-safe; it never carries tool output or paths.</summary>
public sealed class SnapshotException : IOException
{
    public SnapshotException(SnapshotFailureKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;

    /// <summary>Keeps the native result in <see cref="Exception.HResult"/> for diagnostics; it is never sent to the backend.</summary>
    public SnapshotException(SnapshotFailureKind kind, string message, int hresult) : base(message) { Kind = kind; HResult = hresult; }

    public SnapshotFailureKind Kind { get; }
}

/// <summary>Classifies Windows VSS HRESULTs. Anything not listed is a plain snapshot failure.</summary>
public static class VssResult
{
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int InvalidArgument = unchecked((int)0x80070057);
    private const int VolumeNotSupported = unchecked((int)0x8004230C);
    private const int VolumeNotSupportedByProvider = unchecked((int)0x8004230E);
    private const int UnsupportedContext = unchecked((int)0x8004231B);

    public static SnapshotFailureKind Classify(int hresult) => hresult switch
    {
        AccessDenied => SnapshotFailureKind.AccessDenied,
        InvalidArgument or VolumeNotSupported or VolumeNotSupportedByProvider or UnsupportedContext => SnapshotFailureKind.InvalidConfiguration,
        _ => SnapshotFailureKind.Failed
    };

    public static SnapshotException ToException(int hresult, string operation) => new(Classify(hresult), Message(hresult, operation), hresult);

    private static string Message(int hresult, string operation) => (Classify(hresult)) switch
    {
        SnapshotFailureKind.AccessDenied => "The agent account is not allowed to create VSS snapshots; it must be a member of Backup Operators.",
        SnapshotFailureKind.InvalidConfiguration => "The configured volume cannot be snapshotted by VSS.",
        _ => "VSS could not complete the snapshot (" + operation + ")."
    };
}
