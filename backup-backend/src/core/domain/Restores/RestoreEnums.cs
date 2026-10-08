namespace BackupPlatform.Domain.Restores;

/// <summary>Read-only preparation is distinct from the operation that changes a database.</summary>
public enum RestorePurpose
{
    Execution = 1,
    Precheck = 2,
    Discovery = 3
}

/// <summary>
/// What the restore does to the target. <see cref="NewDatabase"/> is the safe default and can never touch an existing database;
/// <see cref="ReplaceExistingDatabase"/> overwrites one and therefore needs an explicit, confirmed request.
/// </summary>
public enum RestoreMode
{
    NewDatabase = 1,
    ReplaceExistingDatabase = 2
}

/// <summary>Derived from <see cref="RestoreMode"/> and stored with the operation so the decision to overwrite is a fact of its own, not an inference.</summary>
public enum RestoreOverwritePolicy
{
    /// <summary>The target database must not exist; an existing one fails the pre-check and the statement never carries REPLACE.</summary>
    Forbidden = 1,
    /// <summary>The user explicitly confirmed overwriting the named, existing database.</summary>
    ReplaceExisting = 2
}

/// <summary>How the agent reaches the bytes of the selected replica. A replica the target agent cannot reach has no access and is never eligible.</summary>
public enum RestoreSourceAccess
{
    /// <summary>The replica lives on the target agent's own disk (agent-local storage).</summary>
    AgentLocal = 1,
    /// <summary>The replica lives in central storage, which the agent reaches directly as a mounted directory.</summary>
    CentralStorage = 2
}

/// <summary>Attempts use Pending..AgentLost; the operation mirrors its current attempt.</summary>
public enum RestoreStatus
{
    Pending = 1, Leased = 2, Running = 3, Succeeded = 4, Failed = 5, Cancelled = 6, AgentLost = 7
}

/// <summary>
/// What is known about the target database after the attempt ended. Never inferred from the status: a Cancelled or Failed restore says
/// explicitly whether the target was touched and in which condition it was left.
/// </summary>
public enum RestoreTargetState
{
    /// <summary>Nothing on the target changed (failed or cancelled before the target was prepared).</summary>
    NotModified = 1,
    /// <summary>The restored database is ONLINE and usable.</summary>
    Online = 2,
    /// <summary>The target was changed and the restore did not complete: the database may be RESTORING, damaged or missing, and a replaced database's old contents are gone.</summary>
    PartiallyRestored = 3,
    /// <summary>The agent could not establish the condition of the target (connection lost, agent lost mid-change).</summary>
    Unknown = 4
}

/// <summary>Restore stages, in order. Terminal stages close the timeline.</summary>
public static class RestoreStages
{
    public const string Preparing = "Preparing";
    public const string DownloadingArtifact = "DownloadingArtifact";
    public const string VerifyingChecksum = "VerifyingChecksum";
    public const string InspectingBackup = "InspectingBackup";
    public const string PreparingTarget = "PreparingTarget";
    public const string Restoring = "Restoring";
    public const string Finalizing = "Finalizing";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";

    public static IReadOnlyList<string> All { get; } =
        [Preparing, DownloadingArtifact, VerifyingChecksum, InspectingBackup, PreparingTarget, Restoring, Finalizing, Completed, Failed, Cancelled];

    public static bool IsKnown(string? stage) => stage is not null && All.Contains(stage, StringComparer.Ordinal);

}
