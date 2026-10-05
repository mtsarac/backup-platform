using System.Globalization;
using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.BackupProviders.MSSQL.PreChecks;

/// <summary>What the server-side probe of the staging directory found. See <see cref="SqlServerStagingProbe"/>.</summary>
internal enum SqlServerStagingProbeOutcome
{
    /// <summary>The probe directory was created by SQL Server and the agent saw it, then removed it.</summary>
    CreatedAndVisible = 0,

    /// <summary>As <see cref="CreatedAndVisible"/>, but the agent could not remove the probe directory afterwards.</summary>
    CreatedAndVisibleLeftover,

    /// <summary>SQL Server created the directory and the agent does not see it: the two sides do not name the same storage.</summary>
    CreatedNotVisible,

    /// <summary>The login may not run the probe (least-privilege logins lack the right to execute the extended procedure).</summary>
    PermissionDenied,

    /// <summary>The operating system refused the creation for the SQL Server service account (no such path, access denied, read-only).</summary>
    ServerOsFailure,

    /// <summary>
    /// The staging directory itself does not exist on the server. Reported before the probe runs: <c>xp_create_subdir</c> creates
    /// every missing parent, so probing a mistyped path would silently build it on the server and let the backup land there.
    /// </summary>
    ServerDirectoryMissing,

    /// <summary>The probe failed for a reason that proves neither writability nor its absence.</summary>
    Failed
}

/// <summary>An estimate of the backup's size. An upper bound (allocated file sizes) may overstate the real size considerably.</summary>
internal sealed record SqlServerBackupSizeEstimate(long Bytes, bool IsUpperBound);

/// <summary>Everything the staging checks learned, gathered separately so their decisions are pure.</summary>
internal sealed record SqlServerStagingFacts(
    StagingAgentAccess AgentAccess,
    SqlServerStagingProbeOutcome? Probe,
    SqlServerBackupSizeEstimate? Estimate,
    bool CompressionRequested);

/// <summary>
/// The decisions of the staging pre-checks (agent access, server-side writability, mapping, disk space). Pure. The agent-side
/// check says nothing about the SQL Server service account; the server-side probe is the only evidence for it, and where a login
/// cannot run the probe the answer is a Warning ("unverifiable"), never a silent Pass. Messages never contain paths.
/// </summary>
internal static class SqlServerStagingPreCheck
{
    public static IReadOnlyList<BackupPreCheckItem> Evaluate(SqlServerStagingFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var items = new List<BackupPreCheckItem> { CheckAgentAccess(facts.AgentAccess) };
        if (items[0].Status == BackupPreCheckStatus.Failed || facts.Probe is not { } probe)
        {
            items.AddRange(Skipped(BackupPreCheckIds.StagingServerWritable, BackupPreCheckIds.StagingMapping, BackupPreCheckIds.StagingDiskSpace));
            return items;
        }

        items.AddRange(CheckProbe(probe));
        items.Add(CheckDiskSpace(facts.AgentAccess, facts.Estimate, facts.CompressionRequested));
        return items;
    }

    /// <summary>The ids of the checks <see cref="Evaluate"/> produces, in order; used to report them as skipped.</summary>
    public static IReadOnlyList<string> CheckIds { get; } =
    [
        BackupPreCheckIds.StagingAgentAccess,
        BackupPreCheckIds.StagingServerWritable,
        BackupPreCheckIds.StagingMapping,
        BackupPreCheckIds.StagingDiskSpace
    ];

    public static BackupPreCheckItem CheckAgentAccess(StagingAgentAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);
        const string id = BackupPreCheckIds.StagingAgentAccess;
        const string scope = " This is the agent process' own access; it says nothing about the SQL Server service account.";

        if (!access.DirectoryExists)
        {
            return Item(id, BackupPreCheckStatus.Failed, SqlServerPreCheckReasons.StagingAgentDirectoryMissing,
                "The staging directory does not exist on the agent's side." + scope);
        }

        if (!access.CanList)
        {
            return Item(id, BackupPreCheckStatus.Failed, SqlServerPreCheckReasons.StagingAgentNotListable,
                "The agent cannot list the staging directory." + scope);
        }

        if (!access.CanWrite)
        {
            return Item(id, BackupPreCheckStatus.Failed, SqlServerPreCheckReasons.StagingAgentNotWritable,
                "The agent cannot create and delete files in the staging directory." + scope);
        }

        return Item(id, BackupPreCheckStatus.Passed, SqlServerPreCheckReasons.StagingAgentAccessible,
            "The agent can list, write and delete in the staging directory." + scope);
    }

    private static IEnumerable<BackupPreCheckItem> CheckProbe(SqlServerStagingProbeOutcome probe)
    {
        const string writable = BackupPreCheckIds.StagingServerWritable;
        const string mapping = BackupPreCheckIds.StagingMapping;

        switch (probe)
        {
            case SqlServerStagingProbeOutcome.CreatedAndVisible:
                yield return Item(writable, BackupPreCheckStatus.Passed, SqlServerPreCheckReasons.StagingServerWritable,
                    "SQL Server created a probe directory in the staging directory, so its service account can write there.");
                yield return Item(mapping, BackupPreCheckStatus.Passed, SqlServerPreCheckReasons.StagingMappingVerified,
                    "The agent saw the probe directory SQL Server created, so both sides name the same storage.");
                break;
            case SqlServerStagingProbeOutcome.CreatedAndVisibleLeftover:
                yield return Item(writable, BackupPreCheckStatus.Passed, SqlServerPreCheckReasons.StagingServerWritable,
                    "SQL Server created a probe directory in the staging directory, so its service account can write there.");
                yield return Item(mapping, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.ProbeLeftover,
                    "Both sides name the same storage, but the agent could not remove the probe directory (named bp_probe_*) afterwards; remove it manually.");
                break;
            case SqlServerStagingProbeOutcome.CreatedNotVisible:
                yield return Item(writable, BackupPreCheckStatus.Passed, SqlServerPreCheckReasons.StagingServerWritable,
                    "SQL Server created a probe directory in the staging directory, so its service account can write there.");
                yield return Item(mapping, BackupPreCheckStatus.Failed, SqlServerPreCheckReasons.StagingMappingMismatch,
                    "The agent cannot see the directory SQL Server just created: the server and agent staging directories are not the same storage. "
                    + "Check the mapping. A probe directory (named bp_probe_*) may remain on the server.");
                break;
            case SqlServerStagingProbeOutcome.PermissionDenied:
                yield return Item(writable, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.StagingServerWriteUnverifiable,
                    "This login cannot probe the staging directory from the server's side, so write access of the SQL Server service account is not verified. "
                    + "The backup itself checks at its start and fails fast.");
                yield return Item(mapping, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.StagingServerWriteUnverifiable,
                    "This login cannot probe the staging directory, so the server/agent mapping is not verified. The backup fails fast if the agent does not see the file SQL Server writes.");
                break;
            case SqlServerStagingProbeOutcome.ServerOsFailure:
                yield return Item(writable, BackupPreCheckStatus.Failed, SqlServerPreCheckReasons.StagingServerCannotWrite,
                    "The operating system refused SQL Server's service account to create a directory in the staging directory. "
                    + "Check that the directory exists on the server and that the account may write to it.");
                yield return Skipped(mapping);
                break;
            case SqlServerStagingProbeOutcome.ServerDirectoryMissing:
                yield return Item(writable, BackupPreCheckStatus.Failed, SqlServerPreCheckReasons.StagingServerDirectoryMissing,
                    "The staging directory does not exist on the database server. The pre-check never creates it; create it and let the SQL Server service account write there.");
                yield return Skipped(mapping);
                break;
            default:
                yield return Item(writable, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.StagingProbeFailed,
                    "The server-side probe of the staging directory failed unexpectedly, so write access is not verified. Details are in the agent log.");
                yield return Item(mapping, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.StagingProbeFailed,
                    "The server-side probe failed unexpectedly, so the server/agent mapping is not verified.");
                break;
        }
    }

    /// <summary>
    /// Compares the free space measured on the agent's side with the estimated backup size. A shortage is only a failure when the
    /// estimate is the used-pages figure and compression is not requested; an upper bound or a compressed backup may well fit.
    /// </summary>
    public static BackupPreCheckItem CheckDiskSpace(StagingAgentAccess access, SqlServerBackupSizeEstimate? estimate, bool compressionRequested)
    {
        ArgumentNullException.ThrowIfNull(access);
        const string id = BackupPreCheckIds.StagingDiskSpace;
        const string side = " (free space is measured on the agent's side of the staging directory)";

        if (access.FreeBytes is not { } free)
        {
            return Item(id, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.DiskSpaceUnknown,
                "The free space of the staging volume could not be read on the agent's side.");
        }

        if (estimate is null)
        {
            return Item(id, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.DiskSpaceUnknown,
                "The size of the backup could not be estimated, so the free space cannot be compared." + side);
        }

        var description = $"estimated size {FormatBytes(estimate.Bytes)}{(estimate.IsUpperBound ? " (an upper bound: allocated file sizes)" : string.Empty)}, free {FormatBytes(free)}{side}";
        if (free >= estimate.Bytes)
        {
            return Item(id, BackupPreCheckStatus.Passed, SqlServerPreCheckReasons.DiskSpaceSufficient, "The staging volume has enough free space: " + description + ".");
        }

        return estimate.IsUpperBound || compressionRequested
            ? Item(id, BackupPreCheckStatus.Warning, SqlServerPreCheckReasons.DiskSpaceEstimateUncertain,
                "The free space is below the estimate, which may overstate the backup (upper bound or compression): " + description + ".")
            : Item(id, BackupPreCheckStatus.Failed, SqlServerPreCheckReasons.DiskSpaceInsufficient,
                "The staging volume does not have enough free space: " + description + ".");
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }

    private static IEnumerable<BackupPreCheckItem> Skipped(params string[] ids) => ids.Select(Skipped);

    private static BackupPreCheckItem Skipped(string id) =>
        Item(id, BackupPreCheckStatus.Skipped, SqlServerPreCheckReasons.PrerequisiteFailed, "Not evaluated because an earlier check failed.");

    private static BackupPreCheckItem Item(string id, BackupPreCheckStatus status, string reason, string message) => new(id, status, reason, message);
}
