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
    /// <summary>Only the shape of the path is verified; whether the database service can write there is a separate, later check.</summary>
    public const string StagingPathFormat = "staging.path-format";

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
    public BackupPreCheckItem(string checkId, BackupPreCheckStatus status, string reasonCode, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown pre-check status.");
        }

        CheckId = checkId;
        Status = status;
        ReasonCode = reasonCode;
        Message = message;
    }

    public string CheckId { get; }
    public BackupPreCheckStatus Status { get; }

    /// <summary>Stable, machine-readable reason, for example <c>database-offline</c>.</summary>
    public string ReasonCode { get; }

    /// <summary>User-safe diagnostic. Never contains raw engine exception text, credentials or connection strings.</summary>
    public string Message { get; }
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
/// says nothing about the server's ability to write there.
/// </summary>
public sealed record DatabaseServerPath
{
    public DatabaseServerPath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>What a caller intends to do, expressed so a provider can check it without executing anything.</summary>
public sealed record FullBackupPreCheckRequest
{
    public FullBackupPreCheckRequest(
        string databaseName,
        IEnumerable<DatabaseCapabilityId>? requestedCapabilities = null,
        DatabaseServerPath? stagingPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        DatabaseName = databaseName;
        RequestedCapabilities = requestedCapabilities?.Distinct().ToArray() ?? [];
        StagingPath = stagingPath;
    }

    public string DatabaseName { get; }

    /// <summary>Optional features of the backup, for example compression or checksum, as provider capability identifiers.</summary>
    public IReadOnlyList<DatabaseCapabilityId> RequestedCapabilities { get; }

    /// <summary>Where the database server would write the backup file, when the plan stages backups on the server host.</summary>
    public DatabaseServerPath? StagingPath { get; }
}
