namespace BackupPlatform.Agent.Core.Backup;

/// <summary>
/// How the agent sweeps its own staging directory for files a crashed process left behind. This is not storage retention: it
/// only ever concerns the agent-local directory SQL Server writes into, and it only removes files the platform itself produced.
/// </summary>
public sealed record StagingCleanupOptions
{
    public static readonly TimeSpan DefaultMinimumAge = TimeSpan.FromHours(12);
    public static readonly TimeSpan DefaultScanInterval = TimeSpan.FromHours(1);

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// A managed file whose last write is more recent than this is never removed. It has to outlast the longest backup the agent can
    /// run: a backup file is written to for the whole backup, so a file that was touched recently may still be in use by a process
    /// this agent cannot see (another agent process, the database service itself).
    /// </summary>
    public TimeSpan MinimumAge { get; init; } = DefaultMinimumAge;

    /// <summary>How often the hosted sweep runs.</summary>
    public TimeSpan ScanInterval { get; init; } = DefaultScanInterval;
}

/// <summary>Stable machine-readable codes for what went wrong during a staging sweep. They carry no path or file name.</summary>
public static class StagingCleanupReasons
{
    public const string ListAccessDenied = "list-access-denied";
    public const string ListFailed = "list-failed";
    public const string DeleteAccessDenied = "delete-access-denied";
    public const string DeleteFailed = "delete-failed";
}

/// <summary>How many times a failure of one kind happened during a sweep.</summary>
public sealed record StagingCleanupFailure(string Reason, int Count);

/// <summary>
/// What one sweep did. Only counts and stable codes, except <see cref="DeletedNames"/>: those are names of the managed grammar,
/// which contain the database name and nothing else, and are what an operator wants to see in a log line.
/// <see cref="Scanned"/> counts the regular files found; a file that vanished before it could be examined again is counted there only.
/// </summary>
/// <param name="Unrecognized">Files whose name is not of the managed grammar. They are somebody else's and are never touched.</param>
/// <param name="Failed">The sum of the counts of <paramref name="Failures"/>.</param>
public sealed record StagingCleanupReport(
    int Scanned,
    int Deleted,
    int KeptYoung,
    int KeptInUse,
    int Unrecognized,
    int Failed,
    IReadOnlyList<StagingCleanupFailure> Failures,
    IReadOnlyList<string> DeletedNames)
{
    /// <summary>The report of a sweep that did nothing (cleanup disabled).</summary>
    public static StagingCleanupReport Empty { get; } = new(0, 0, 0, 0, 0, 0, [], []);
}
