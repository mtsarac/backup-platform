using System.Globalization;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using BackupPlatform.BackupProviders.MSSQL.PreChecks;

namespace BackupPlatform.BackupProviders.MSSQL.Restore;

/// <summary>What the plan needs from the target beyond the shared facts. <see cref="MasterFiles"/> is null when the server's file catalog could not be read.</summary>
internal sealed record SqlServerRestorePlanFacts(
    SqlServerRestoreTargetFacts Target,
    Guid? TargetFamilyGuid,
    IReadOnlyList<SqlServerMasterFile>? MasterFiles);

internal sealed record SqlServerRestorePlanEvaluation(IReadOnlyList<BackupPreCheckItem> Items, RestorePlan? Plan);

/// <summary>
/// The decision logic of the restore plan: the backup compared with the target (type, version direction, the target's existing database and its
/// relation to the backup, file types), then the file mapping and its disk-space check. A plan exists exactly when no item failed. Pure.
/// </summary>
internal static class SqlServerRestorePlanner
{
    public static SqlServerRestorePlanEvaluation Evaluate(SqlServerRestorePlanFacts facts, RestorePlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(request);

        var target = facts.Target;
        var replace = request.Mode == DatabaseRestoreMode.ReplaceExistingDatabase;
        var inspection = request.Inspection;

        if (SqlServerRestoreChecks.ConnectionFailureItems(target, AllChecks(replace)) is { } connectionFailure)
        {
            return new SqlServerRestorePlanEvaluation(connectionFailure, null);
        }

        var items = new List<BackupPreCheckItem>(SqlServerRestoreChecks.ConnectedItems());

        if (target.MetadataFailure is not null || target.Instance is not { } instance)
        {
            items.Add(SqlServerRestoreChecks.MetadataUnavailable(target, SqlServerRestoreCheckIds.TargetServer));
            items.AddRange(AllChecks(replace).Skip(1).Select(SqlServerRestoreChecks.Skipped));
            return new SqlServerRestorePlanEvaluation(items, null);
        }

        items.Add(SqlServerRestoreChecks.CheckServer(instance));
        items.Add(SqlServerRestoreCompatibility.CheckBackupType(inspection.Header));
        items.Add(SqlServerRestoreCompatibility.CheckVersion(inspection.Header.SourceServerVersion, instance.Version));
        items.Add(SqlServerRestoreCompatibility.CheckEdition(instance.Edition));

        var name = SqlServerRestoreChecks.CheckDatabaseName(request.DatabaseName);
        items.Add(name);
        var nameUsable = name.Status != BackupPreCheckStatus.Failed;

        var database = target.Database;
        BackupPreCheckItem exists;
        if (nameUsable)
        {
            exists = SqlServerRestoreChecks.CheckExists(request.Mode, database);
            items.Add(exists);
            if (SqlServerRestoreChecks.CheckProtected(request.DatabaseName, request.Mode, target) is { } protection)
            {
                items.Add(protection);
            }
        }
        else
        {
            exists = SqlServerRestoreChecks.Skipped(BackupPreCheckIds.DatabaseExists);
            items.Add(exists);
            items.Add(SqlServerRestoreChecks.Skipped(SqlServerRestoreCheckIds.TargetProtected));
        }

        if (replace)
        {
            items.Add(database is not null
                ? SqlServerRestoreCompatibility.CheckRelation(facts.TargetFamilyGuid, inspection.Header.FamilyGuid)
                : SqlServerRestoreChecks.Skipped(SqlServerRestoreCheckIds.TargetRelation));
        }

        var typeRejection = SqlServerRestoreFileMapper.RejectUnsupportedFiles(inspection.Files);
        items.Add(typeRejection is null
            ? SqlServerRestoreChecks.Passed(SqlServerRestoreCheckIds.FileTypes, SqlServerRestoreReasons.FileTypesSupported, "Every file in the backup is a data or log file.")
            : SqlServerRestoreChecks.Failed(SqlServerRestoreCheckIds.FileTypes, RestoreReasonCodes.FileMappingFailed, typeRejection));

        var (directoryItem, directories) = SqlServerRestoreChecks.CheckDirectories(target);
        if (directoryItem.Status == BackupPreCheckStatus.Warning && directories is null)
        {
            // The pre-check may live with unknown directories (the plan repeats the look); the plan cannot place a single file without them.
            directoryItem = SqlServerRestoreChecks.Failed(SqlServerRestoreCheckIds.Directories, RestoreReasonCodes.FileMappingFailed,
                "SQL Server did not report its default data and log directories, so there is no safe place for the restored files.");
        }

        items.Add(directoryItem);

        var mappable = typeRejection is null && directories is not null && nameUsable && exists.Status == BackupPreCheckStatus.Passed;
        if (!mappable)
        {
            items.Add(SqlServerRestoreChecks.Skipped(SqlServerRestoreCheckIds.FileMapping));
            items.Add(SqlServerRestoreChecks.Skipped(SqlServerRestoreCheckIds.DiskSpace));
            return new SqlServerRestorePlanEvaluation(items, null);
        }

        var targetFiles = replace && database is not null && facts.MasterFiles is { } known
            ? known.Where(file => string.Equals(file.DatabaseName, database.Name, StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];
        var mapping = SqlServerRestoreFileMapper.Map(new SqlServerRestoreMappingInput(
            request.DatabaseName, inspection.Files, directories!, instance.HostPlatform, replace, targetFiles, facts.MasterFiles ?? []));

        if (!mapping.Succeeded)
        {
            items.Add(SqlServerRestoreChecks.Failed(SqlServerRestoreCheckIds.FileMapping, RestoreReasonCodes.FileMappingFailed, mapping.FailureMessage!));
            items.Add(SqlServerRestoreChecks.Skipped(SqlServerRestoreCheckIds.DiskSpace));
            return new SqlServerRestorePlanEvaluation(items, null);
        }

        var mappings = mapping.Mappings!;
        items.Add(facts.MasterFiles is null
            ? SqlServerRestoreChecks.Warning(SqlServerRestoreCheckIds.FileMapping, SqlServerRestoreReasons.ExistingFilesUnknown,
                "The files that already exist on the server could not be listed; destinations are generated, and the engine refuses to overwrite a file that is taken.")
            : SqlServerRestoreChecks.Passed(SqlServerRestoreCheckIds.FileMapping, SqlServerRestoreReasons.FileMappingReady,
                $"{mappings.Count.ToString(CultureInfo.InvariantCulture)} file(s) are placed in the server's own directories."));
        items.Add(CheckDiskSpace(target.Volumes, mappings, targetFiles));

        var warnings = new List<BackupWarning>();
        if (mapping.ReusedFiles > 0)
        {
            warnings.Add(new BackupWarning(SqlServerRestoreWarningCodes.ExistingFilesReused,
                $"{mapping.ReusedFiles.ToString(CultureInfo.InvariantCulture)} file(s) of the existing database are overwritten in place."));
        }

        if (items.Any(item => item.Status == BackupPreCheckStatus.Failed))
        {
            return new SqlServerRestorePlanEvaluation(items, null);
        }

        var info = new RestoreTargetInfo(
            SqlServerInstanceInfoMapping.ToInstanceInfo(instance),
            directories!.Data,
            directories.Log,
            database is not null,
            database is null ? null : SqlServerRestoreOutcomeRules.StateText(database.State));
        return new SqlServerRestorePlanEvaluation(items, new RestorePlan(mappings, info, warnings));
    }

    /// <summary>
    /// The exact check: the bytes the restored files need on each volume against its free space. The files of one directory share a volume, and two
    /// directories whose volumes report the same size and free space are treated as one volume, so data and log are summed when they live together. A file
    /// restored in place over a file of the same database needs only the growth.
    /// </summary>
    internal static BackupPreCheckItem CheckDiskSpace(
        IReadOnlyList<SqlServerVolume>? volumes,
        IReadOnlyList<RestoreFileMapping> mappings,
        IReadOnlyList<SqlServerMasterFile> targetFiles)
    {
        const string id = SqlServerRestoreCheckIds.DiskSpace;
        if (volumes is null)
        {
            return SqlServerRestoreChecks.Warning(id, SqlServerRestoreReasons.DiskSpaceUnknown,
                "SQL Server did not report free disk space (VIEW SERVER STATE is needed); the restore fails if a volume is too small.");
        }

        var needByDirectory = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in mappings)
        {
            var directory = SqlServerTargetDirectoryResolver.DirectoryOf(mapping.TargetPath.Value);
            if (directory is null)
            {
                continue;
            }

            var existing = targetFiles.FirstOrDefault(file => string.Equals(file.PhysicalName, mapping.TargetPath.Value, StringComparison.OrdinalIgnoreCase));
            var need = Math.Max(0, mapping.SizeBytes - (existing?.SizeBytes ?? 0));
            needByDirectory[directory] = needByDirectory.GetValueOrDefault(directory) + need;
        }

        var perVolume = new Dictionary<(long Total, long Available), long>();
        var unresolved = false;
        foreach (var (directory, need) in needByDirectory)
        {
            if (SqlServerRestoreChecks.FindVolume(volumes, directory) is not { } volume)
            {
                unresolved = true;
                continue;
            }

            var key = (volume.TotalBytes, volume.AvailableBytes);
            perVolume[key] = perVolume.GetValueOrDefault(key) + need;
        }

        foreach (var ((_, available), need) in perVolume)
        {
            if (need > available)
            {
                return SqlServerRestoreChecks.Failed(id, RestoreReasonCodes.InsufficientDiskSpace,
                    $"The restored files need {FormatBytes(need)} on a volume that has {FormatBytes(available)} free.");
            }
        }

        return unresolved || perVolume.Count == 0
            ? SqlServerRestoreChecks.Warning(id, SqlServerRestoreReasons.DiskSpaceUnknown,
                "The free space of some target directories could not be determined; the restore fails if a volume is too small.")
            : SqlServerRestoreChecks.Passed(id, SqlServerRestoreReasons.DiskSpaceSufficient,
                $"The target volumes have enough free space for the {FormatBytes(perVolume.Values.Sum())} the restored files need.");
    }

    private static IReadOnlyList<string> AllChecks(bool replace)
    {
        var ids = new List<string>
        {
            SqlServerRestoreCheckIds.TargetServer,
            SqlServerRestoreCheckIds.BackupType,
            SqlServerRestoreCheckIds.VersionCompatibility,
            SqlServerRestoreCheckIds.EditionCompatibility,
            SqlServerRestoreCheckIds.DatabaseName,
            BackupPreCheckIds.DatabaseExists,
            SqlServerRestoreCheckIds.TargetProtected
        };

        if (replace)
        {
            ids.Add(SqlServerRestoreCheckIds.TargetRelation);
        }

        ids.Add(SqlServerRestoreCheckIds.FileTypes);
        ids.Add(SqlServerRestoreCheckIds.Directories);
        ids.Add(SqlServerRestoreCheckIds.FileMapping);
        ids.Add(SqlServerRestoreCheckIds.DiskSpace);
        return ids;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes.ToString(CultureInfo.InvariantCulture)} B"
            : $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}
