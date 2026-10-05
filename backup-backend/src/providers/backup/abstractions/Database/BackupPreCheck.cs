namespace BackupPlatform.BackupProviders.Abstractions.Database;

/// <summary>Outcome of a single pre-check.</summary>
public enum BackupPreCheckStatus
{
    /// <summary>The condition was verified.</summary>
    Passed = 0,

    /// <summary>The backup may proceed, but the condition could not be fully verified or deserves attention.</summary>
    Warning,

    /// <summary>The backup must not be started.</summary>
    Failed,

    /// <summary>Not evaluated because a prerequisite check failed. Never the cause of an overall failure.</summary>
    Skipped
}

/// <summary>
/// Stable identifiers for engine-independent pre-checks. Engine-specific checks use their own identifiers; consumers key on
/// <see cref="BackupPreCheckItem.CheckId"/> and <see cref="BackupPreCheckItem.ReasonCode"/>, never on message text.
/// </summary>
public static class BackupPreCheckIds
{
    public const string ServerReachable = "server.reachable";
    public const string Authentication = "server.authentication";
    public const string DatabaseExists = "database.exists";
    public const string DatabaseState = "database.state";
    public const string BackupCapability = "database.backup-capability";
    public const string BackupPermission = "permission.backup";
    /// <summary>Only the shape of the server-side path is verified here; whether the database service can write there is the separate <see cref="StagingServerWritable"/> check.</summary>
    public const string StagingPathFormat = "staging.path-format";

    /// <summary>The agent process can list, write and delete in its side of the staging directory. Says nothing about the database service account.</summary>
    public const string StagingAgentAccess = "staging.agent-access";

    /// <summary>The database service account can create entries in the staging directory (probed from the server's side, never inferred from agent access).</summary>
    public const string StagingServerWritable = "staging.server-writable";

    /// <summary>The server-side and agent-side staging directories are the same storage (an entry made by one side is seen by the other).</summary>
    public const string StagingMapping = "staging.mapping";

    /// <summary>Free space on the staging volume compared with the estimated backup size.</summary>
    public const string StagingDiskSpace = "staging.disk-space";

    /// <summary>The check id for a capability the caller asked to use.</summary>
    public static string RequestedCapability(DatabaseCapabilityId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return $"requested.{id.Value}";
    }
}

/// <summary>One pre-check result: which check, how it went, why (machine-readable) and what to tell a person.</summary>
public sealed record BackupPreCheckItem
{
    public BackupPreCheckItem(string checkId, BackupPreCheckStatus status, string reasonCode, string message, BackupFailureCause cause = BackupFailureCause.Unclassified)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown pre-check status.");
        }

        if (!Enum.IsDefined(cause))
        {
            throw new ArgumentOutOfRangeException(nameof(cause), cause, "Unknown failure cause.");
        }

        CheckId = checkId;
        Status = status;
        ReasonCode = reasonCode;
        Message = message;
        Cause = cause;
    }

    public string CheckId { get; }
    public BackupPreCheckStatus Status { get; }

    /// <summary>Stable, machine-readable reason, for example <c>database-offline</c>.</summary>
    public string ReasonCode { get; }

    /// <summary>User-safe diagnostic. Never contains raw engine exception text, credentials or connection strings.</summary>
    public string Message { get; }

    /// <summary>Engine-independent classification of a failed check; <see cref="BackupFailureCause.Unclassified"/> when unknown or not a failure.</summary>
    public BackupFailureCause Cause { get; }
}

/// <summary>The ordered results of a pre-check run together with their aggregate.</summary>
public sealed class BackupPreCheckReport
{
    public BackupPreCheckReport(IEnumerable<BackupPreCheckItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items.ToArray();
        if (Items.Any(item => item is null))
        {
            throw new ArgumentException("A pre-check report must not contain null items.", nameof(items));
        }

        Status = Items.Any(item => item.Status == BackupPreCheckStatus.Failed) ? BackupPreCheckStatus.Failed
            : Items.Any(item => item.Status == BackupPreCheckStatus.Warning) ? BackupPreCheckStatus.Warning
            : BackupPreCheckStatus.Passed;
    }

    public IReadOnlyList<BackupPreCheckItem> Items { get; }

    /// <summary>Failed if any item failed, otherwise Warning if any item warned, otherwise Passed. Skipped items never contribute.</summary>
    public BackupPreCheckStatus Status { get; }

    /// <summary>True unless an item failed.</summary>
    public bool CanProceed => Status != BackupPreCheckStatus.Failed;
}

/// <summary>
/// A path as seen by the database server's own file system. A server-side backup file is written by the database service
/// account, not by the agent process, so this type exists to keep the two apart: write access of the agent to a directory
/// says nothing about the server's ability to write there. The agent's view of a path is <see cref="AgentPath"/>.
/// </summary>
public sealed record DatabaseServerPath
{
    public DatabaseServerPath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    /// <summary>True for drive-letter and UNC paths, which use <c>\</c> as separator; everything else is treated as POSIX style.</summary>
    public bool IsWindowsStyle =>
        (Value.Length >= 3 && char.IsAsciiLetter(Value[0]) && Value[1] == ':' && Value[2] is '\\' or '/')
        || Value.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>
    /// The path of a file directly inside this directory, using the separator of the path's own style. <paramref name="fileName"/>
    /// must be a simple name: it is the one value that is joined into a server path, so separators are refused.
    /// </summary>
    public DatabaseServerPath Combine(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName is "." or ".." || fileName.Any(char.IsControl) || fileName.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            throw new ArgumentException("A server file name is a simple name without directory components.", nameof(fileName));
        }

        var separator = IsWindowsStyle ? '\\' : '/';
        return new DatabaseServerPath(Value.TrimEnd('/', '\\') + separator + fileName);
    }

    public override string ToString() => Value;
}

/// <summary>What a caller intends to do, expressed so a provider can check it without executing anything.</summary>
public sealed record FullBackupPreCheckRequest
{
    public FullBackupPreCheckRequest(
        string databaseName,
        IEnumerable<DatabaseCapabilityId>? requestedCapabilities = null,
        StagingArea? staging = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        DatabaseName = databaseName;
        RequestedCapabilities = requestedCapabilities?.Distinct().ToArray() ?? [];
        Staging = staging;
    }

    public string DatabaseName { get; }

    /// <summary>Optional features of the backup that must be available, as provider capability identifiers; an unsupported one fails the pre-check.</summary>
    public IReadOnlyList<DatabaseCapabilityId> RequestedCapabilities { get; }

    /// <summary>The staging directory seen from the database server and from the agent, when the plan stages backups on disk.</summary>
    public StagingArea? Staging { get; }
}
