using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.PostgreSql.Discovery;
using BackupPlatform.BackupProviders.PostgreSql.Tools;

namespace BackupPlatform.BackupProviders.PostgreSql.Backup;

internal static class PostgreSqlBackupChecks
{
    public static void CheckFacts(List<BackupPreCheckItem> items, PostgreSqlBackupFacts facts, FullBackupPreCheckRequest request)
    {
        var capabilities = PostgreSqlCapabilityEvaluator.EvaluateDatabase(PostgreSqlCapabilityEvaluator.EvaluateInstance(facts.Server), facts.Database);
        var full = capabilities.Single(capability => capability.Id == PostgreSqlCapabilityIds.FullBackup);
        items.Add(full.Support == DatabaseCapabilitySupport.Supported
            ? PostgreSqlFailures.Passed(BackupPreCheckIds.BackupCapability, "A consistent logical full backup is supported.")
            : PostgreSqlFailures.Failed(BackupPreCheckIds.BackupCapability, full.ReasonCode, full.Explanation, BackupFailureCause.Unsupported));
        if (facts.HasForeignTables || facts.HasSubscriptions || facts.IsStandby)
            items.Add(PostgreSqlFailures.Failed("postgres.logical-source", "logical-source-unsupported",
                "Full logical backup requires a primary database without foreign tables or replication subscriptions; external or unavailable data must not be silently omitted.",
                BackupFailureCause.Unsupported));
        foreach (var requested in request.RequestedCapabilities)
        {
            var capability = capabilities.TryGet(requested, out var known) ? known : null;
            items.Add(capability?.Support == DatabaseCapabilitySupport.Supported
                ? PostgreSqlFailures.Passed(BackupPreCheckIds.RequestedCapability(requested), capability.Explanation)
                : PostgreSqlFailures.Failed(BackupPreCheckIds.RequestedCapability(requested), PostgreSqlReasons.RequiredOptionUnsupported,
                    capability?.Explanation ?? "The requested capability is not supported.", BackupFailureCause.Unsupported));
        }
        items.Add(facts.UnreadableTables == 0 && facts.RowSecurityBlockedTables == 0 && facts.LargeObjectsReadable != false
            ? PostgreSqlFailures.Passed(BackupPreCheckIds.BackupPermission, "The database relations are readable without row-security filtering.")
            : PostgreSqlFailures.Failed(BackupPreCheckIds.BackupPermission,
                facts.RowSecurityBlockedTables > 0 ? PostgreSqlReasons.RowSecurityNotBypassed : PostgreSqlReasons.TablesNotReadable,
                "The login cannot read all tables, sequences or large objects, or cannot bypass row-security policies.", BackupFailureCause.PermissionDenied));
        if (facts.LargeObjectsReadable is null)
            items.Add(new BackupPreCheckItem("permission.large-objects", BackupPreCheckStatus.Warning, PostgreSqlWarningCodes.LargeObjectPrivilegeUnverified,
                "Large-object privileges could not be checked in advance; pg_dump must read every object successfully before any artifact is published."));
        if (request.Staging is { } staging) CheckStaging(items, staging, facts.Database.SizeBytes);
    }

    public static void CheckStaging(List<BackupPreCheckItem> items, StagingArea staging, long? estimatedBytes)
    {
        var access = new AgentStagingDirectory(staging.AgentDirectory).Inspect();
        items.Add(access.DirectoryExists && access.CanWrite && access.CanList
            ? PostgreSqlFailures.Passed(BackupPreCheckIds.StagingAgentAccess, "The agent can access its staging directory; PostgreSQL needs no shared server directory.")
            : PostgreSqlFailures.Failed(BackupPreCheckIds.StagingAgentAccess, PostgreSqlReasons.StagingAgentDirectoryMissing,
                "The agent staging directory must exist and be readable and writable.", BackupFailureCause.StagingInvalid));
        if (access.FreeBytes is { } free && estimatedBytes is { } estimate && free < estimate)
            items.Add(new BackupPreCheckItem(BackupPreCheckIds.StagingDiskSpace, BackupPreCheckStatus.Warning, PostgreSqlReasons.StagingInsufficientSpace,
                "Available staging space is smaller than the uncompressed database; the dump may run out of space."));
    }

    public static BackupPreCheckItem CheckTool(PostgreSqlToolResolution resolution, string tool, int? serverMajor = null)
    {
        if (resolution.Tool is not { } found)
            return PostgreSqlFailures.Failed("postgres.tools." + tool,
                resolution.Status == PostgreSqlToolStatus.Missing ? PostgreSqlReasons.ToolsMissing : PostgreSqlReasons.ToolsUnusable,
                "A required PostgreSQL client tool is missing or cannot run. Configure ClientToolsDirectory or the agent PATH.", BackupFailureCause.Unsupported);
        if (serverMajor is { } major && found.Major < major)
            return PostgreSqlFailures.Failed("postgres.tools." + tool, PostgreSqlReasons.ToolVersionTooOld,
                "The PostgreSQL client tool must be at least as new as the source server.", BackupFailureCause.Unsupported);
        return PostgreSqlFailures.Passed("postgres.tools." + tool, "The required PostgreSQL client tool is available and compatible.");
    }
}
