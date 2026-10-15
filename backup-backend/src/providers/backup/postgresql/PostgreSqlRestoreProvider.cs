using System.Globalization;
using System.Text.Json;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.PostgreSql.Connectivity;
using BackupPlatform.BackupProviders.PostgreSql.Discovery;
using BackupPlatform.BackupProviders.PostgreSql.Restore;
using BackupPlatform.BackupProviders.PostgreSql.Tools;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.BackupProviders.PostgreSql;

/// <summary>Logical full restore, preserving ownership and ACLs. A superuser is required; cluster roles are never created from an archive.</summary>
public sealed class PostgreSqlRestoreProvider : IDatabaseRestoreProvider
{
    private readonly ILogger<PostgreSqlRestoreProvider> _logger;
    private readonly PostgreSqlClientTools _tools;
    private readonly TimeProvider _clock;
    public PostgreSqlRestoreProvider(ILogger<PostgreSqlRestoreProvider> logger, PostgreSqlProviderOptions? options = null, TimeProvider? timeProvider = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tools = new PostgreSqlClientTools(options ?? new(), logger);
        _clock = timeProvider ?? TimeProvider.System;
    }
    public DatabaseEngineId Engine => DatabaseEngineId.PostgreSql;

    public async Task<BackupPreCheckReport> PreCheckRestoreAsync(DatabaseConnectionSettings connection, DatabaseCredential credential,
        RestorePreCheckRequest request, CancellationToken cancellationToken)
    {
        var items = new List<BackupPreCheckItem>();
        try
        {
            ValidateName(request.DatabaseName);
            var tool = await RestoreToolAsync(cancellationToken).ConfigureAwait(false);
            items.Add(Pass("postgres.tools", "pg_restore is available."));
            var target = PostgreSqlConnectionTarget.Create(connection, credential);
            await using var session = await PostgreSqlSession.OpenAsync(target, "postgres", _logger, cancellationToken).ConfigureAwait(false);
            await CheckTargetAsync(session, request.DatabaseName, request.Mode, request.TerminateActiveConnections, items, cancellationToken).ConfigureAwait(false);
            _ = tool;
        }
        catch (Exception exception) when (IsExpected(exception)) { items.Add(FailedItem("postgres.restore", exception)); }
        return new BackupPreCheckReport(items);
    }

    public async Task<RestoreInspectionResult> InspectBackupAsync(DatabaseConnectionSettings connection, DatabaseCredential credential,
        RestoreInspectionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var tool = await RestoreToolAsync(cancellationToken).ConfigureAwait(false);
            var path = AgentFile(request.AgentFile);
            var inspection = await PostgreSqlArchive.ReadAsync(path, tool, request.RunVerifyOnly, request.VerifyTimeout, _clock, cancellationToken).ConfigureAwait(false);
            return new RestoreInspectionResult(inspection, null);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return new RestoreInspectionResult(null, Failure(exception, RestoreFailureKind.BackupInvalid, RestoreExecutionStage.InspectingBackup));
        }
    }

    public async Task<RestorePlanResult> PlanRestoreAsync(DatabaseConnectionSettings connection, DatabaseCredential credential,
        RestorePlanRequest request, CancellationToken cancellationToken)
    {
        var items = new List<BackupPreCheckItem>();
        RestoreTargetInfo? targetInfo = null;
        try
        {
            ValidateName(request.DatabaseName);
            var tool = await RestoreToolAsync(cancellationToken).ConfigureAwait(false);
            var target = PostgreSqlConnectionTarget.Create(connection, credential);
            await using var session = await PostgreSqlSession.OpenAsync(target, "postgres", _logger, cancellationToken).ConfigureAwait(false);
            targetInfo = await CheckTargetAsync(session, request.DatabaseName, request.Mode, request.TerminateActiveConnections, items, cancellationToken).ConfigureAwait(false);
            await CheckArchiveAsync(session, request.Inspection, tool, items, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpected(exception)) { items.Add(FailedItem("postgres.restore-plan", exception)); }
        var report = new BackupPreCheckReport(items);
        return new RestorePlanResult(report, report.CanProceed && targetInfo is not null ? new RestorePlan([], targetInfo,
            request.Mode == DatabaseRestoreMode.ReplaceExistingDatabase
                ? [new BackupWarning(PostgreSqlWarningCodes.RestoredDataRetained, "Replacement retains the previous database under an identifiable recovery name. Remove it manually only after validating the restored database.")]
                : []) : null);
    }

    public Task<DatabaseRestoreResult> ExecuteRestoreAsync(DatabaseConnectionSettings connection, DatabaseCredential credential,
        DatabaseRestoreRequest request, IProgress<RestoreProgress>? progress, CancellationToken cancellationToken) =>
        PostgreSqlRestoreExecutor.ExecuteAsync(this, connection, credential, request, progress, cancellationToken);

    internal ILogger Logger => _logger;
    internal TimeProvider Clock => _clock;
    internal async Task<PostgreSqlToolInfo> RestoreToolAsync(CancellationToken token)
    {
        var resolved = await _tools.ResolveAsync(PostgreSqlClientTools.PgRestore, token).ConfigureAwait(false);
        return resolved.Tool ?? throw PostgreSqlArchive.Failure(resolved.Status == PostgreSqlToolStatus.Missing ? PostgreSqlReasons.ToolsMissing : PostgreSqlReasons.ToolsUnusable,
            "A working pg_restore client must be installed on the agent.", BackupFailureCause.Unsupported);
    }

    internal static string AgentFile(AgentPath? file)
    {
        if (file is null || !Path.IsPathFullyQualified(file.Value))
            throw PostgreSqlArchive.Failure(PostgreSqlReasons.AgentFileMissing, "PostgreSQL restore requires an absolute agent-local archive path.", BackupFailureCause.StagingInvalid);
        if (!File.Exists(file.Value)) throw PostgreSqlArchive.Failure(PostgreSqlReasons.StagedFileMissing, "The agent-local archive does not exist.", BackupFailureCause.StagingInvalid);
        return file.Value;
    }

    internal static void ValidateName(string name)
    {
        if (!PostgreSqlIdentifiers.IsValidDatabaseName(name))
            throw PostgreSqlArchive.Failure(PostgreSqlReasons.DatabaseNameInvalid, "The target database name is invalid.");
        if (PostgreSqlIdentifiers.IsProtectedDatabase(name) || name.StartsWith("bp_restore_", StringComparison.Ordinal) || name.StartsWith("bp_old_", StringComparison.Ordinal))
            throw PostgreSqlArchive.Failure(RestoreReasonCodes.TargetProtected, "System and restore recovery databases cannot be restore targets.");
    }

    internal static async Task<RestoreTargetInfo> CheckTargetAsync(PostgreSqlSession session, string name, DatabaseRestoreMode mode, bool terminate,
        List<BackupPreCheckItem> items, CancellationToken token)
    {
        var server = await PostgreSqlDiscovery.ReadServerAsync(session, token).ConfigureAwait(false);
        var role = await PostgreSqlDiscovery.ReadRoleAsync(session, token).ConfigureAwait(false);
        if (server.Major < PostgreSqlDiscovery.MinimumServerMajor)
            throw PostgreSqlArchive.Failure(PostgreSqlReasons.ServerVersionUnsupported, "This PostgreSQL server version is not supported.", BackupFailureCause.Unsupported);
        if (!role.IsSuperuser)
            throw PostgreSqlArchive.Failure(PostgreSqlReasons.RestoreRolePrivilege, "PostgreSQL restore requires a superuser to preserve object owners, ACLs, large objects and extensions.", BackupFailureCause.PermissionDenied);
        items.Add(Pass("postgres.restore-permission", "The login can preserve ownership, ACLs and extension privileges."));
        var database = await PostgreSqlDiscovery.FindDatabaseAsync(session, name, token).ConfigureAwait(false);
        if (database?.IsSystem == true) throw PostgreSqlArchive.Failure(RestoreReasonCodes.TargetProtected, "The target is a protected database.");
        if (mode == DatabaseRestoreMode.NewDatabase && database is not null)
            throw PostgreSqlArchive.Failure(RestoreReasonCodes.TargetDatabaseExists, "A new restore never overwrites an existing database.");
        if (mode == DatabaseRestoreMode.ReplaceExistingDatabase && database is null)
            throw PostgreSqlArchive.Failure(RestoreReasonCodes.TargetDatabaseMissing, "Replacement requires an existing database and explicit upstream overwrite confirmation.");
        if (database is not null && !terminate && await session.ScalarAsync("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = @name)", token, ("name", name)).ConfigureAwait(false) is true)
            throw PostgreSqlArchive.Failure(RestoreReasonCodes.TargetInUse, "The target has active connections and session termination was not permitted.");
        items.Add(Pass("postgres.target", "The target database matches the selected restore mode."));
        return new RestoreTargetInfo(new DatabaseInstanceInfo(DatabaseEngineId.PostgreSql, "PostgreSQL", null, server.Version, null, server.HostPlatform),
            null, null, database is not null, database is null ? null : database.AllowsConnections ? "ONLINE" : "UNAVAILABLE");
    }

    internal static async Task CheckArchiveAsync(PostgreSqlSession session, RestoreBackupInspection inspection, PostgreSqlToolInfo tool,
        List<BackupPreCheckItem> items, CancellationToken token)
    {
        if (inspection.Header.Kind != RestoreBackupKind.Database || inspection.EngineDetails is not { } details || !details.TryGetValue(PostgreSqlArchive.DatabaseSqlKey, out _)
            || !details.TryGetValue(PostgreSqlArchive.DumpMajorKey, out var majorText) || !int.TryParse(majorText, NumberStyles.None, CultureInfo.InvariantCulture, out var dumpMajor))
            throw PostgreSqlArchive.Failure(RestoreReasonCodes.BackupTypeUnsupported, "Restore requires an inspected PostgreSQL custom-format full archive.", BackupFailureCause.Unsupported);
        var server = await PostgreSqlDiscovery.ReadServerAsync(session, token).ConfigureAwait(false);
        if (tool.Major < dumpMajor) throw PostgreSqlArchive.Failure(PostgreSqlReasons.ArchiveVersionUnsupported, "The installed pg_restore is older than the archive writer.", BackupFailureCause.Unsupported);
        if (inspection.Header.SourceServerVersion is null || server.Major < inspection.Header.SourceServerVersion.Major || server.Major < dumpMajor)
            throw PostgreSqlArchive.Failure(RestoreReasonCodes.TargetVersionTooOld, "The target must be at least as new as both the source server and the archive's pg_dump major version.", BackupFailureCause.Unsupported);
        foreach (var extension in ReadNames(details, PostgreSqlArchive.ExtensionsKey))
            if (await session.ScalarAsync("SELECT EXISTS (SELECT 1 FROM pg_available_extensions WHERE name = @name)", token, ("name", extension)).ConfigureAwait(false) is not true)
                throw PostgreSqlArchive.Failure(PostgreSqlReasons.ExtensionUnavailable, "An extension required by the archive is unavailable on the target.", BackupFailureCause.Unsupported);
        items.Add(Pass("postgres.archive-compatibility", "Source, archive writer, restore client, target version and extension availability are compatible."));
        items.Add(new BackupPreCheckItem("postgres.roles", BackupPreCheckStatus.Warning, "role-definitions-not-in-archive",
            "Cluster role definitions are not included. All owner and ACL roles must already exist; missing roles fail safely on scratch before publication. Ownership and ACLs are never silently stripped."));
        items.Add(new BackupPreCheckItem("postgres.archive-trust", BackupPreCheckStatus.Warning, "trusted-archive-required",
            "Restore only trusted platform artifacts: archive schema SQL, functions and extensions execute with the restore login's superuser privileges."));
    }

    private static string[] ReadNames(IReadOnlyDictionary<string, string> details, string key) => details.TryGetValue(key, out var json)
        ? JsonSerializer.Deserialize<string[]>(json) ?? [] : [];
    private static BackupPreCheckItem Pass(string id, string message) => new(id, BackupPreCheckStatus.Passed, "ok", message);
    private static BackupPreCheckItem FailedItem(string id, Exception exception)
    {
        var failure = Failure(exception, RestoreFailureKind.PreCheckFailed, RestoreExecutionStage.PreparingTarget);
        return new(id, BackupPreCheckStatus.Failed, failure.ReasonCode, failure.Message, failure.Cause);
    }
    internal static bool IsExpected(Exception exception) => exception is PostgreSqlRestoreException or DatabaseProviderException or IOException or UnauthorizedAccessException
        or System.ComponentModel.Win32Exception or JsonException or System.Text.RegularExpressions.RegexMatchTimeoutException;
    internal static RestoreFailure Failure(Exception exception, RestoreFailureKind kind, RestoreExecutionStage stage)
    {
        if (exception is PostgreSqlRestoreException postgres) return new(kind, stage, postgres.ReasonCode, postgres.Message, postgres.Cause);
        if (exception is DatabaseProviderException provider)
        {
            var (code, cause) = provider.Kind switch
            {
                DatabaseFailureKind.AuthenticationFailed => (PostgreSqlReasons.AuthenticationFailed, BackupFailureCause.AuthenticationFailed),
                DatabaseFailureKind.PermissionDenied => (PostgreSqlReasons.PermissionDenied, BackupFailureCause.PermissionDenied),
                DatabaseFailureKind.Timeout => (PostgreSqlReasons.ConnectionTimeout, BackupFailureCause.Timeout),
                DatabaseFailureKind.InvalidConfiguration => (PostgreSqlReasons.ConnectionSettingsInvalid, BackupFailureCause.Unclassified),
                _ => (PostgreSqlReasons.ServerUnreachable, BackupFailureCause.DatabaseUnavailable)
            };
            return new(kind, stage, code, provider.Message, cause);
        }
        return new(kind, stage, PostgreSqlReasons.ArchiveInvalid, "The archive or client tool could not be read safely. Details are available to the agent administrator.");
    }
}
