using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Connectivity;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using BackupPlatform.BackupProviders.MSSQL.PreChecks;
using BackupPlatform.BackupProviders.MSSQL.Restore;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.BackupProviders.MSSQL;

/// <summary>
/// Restores a verified full backup into SQL Server over the normal SQL network protocol: target-only pre-check, inspection of the staged backup file,
/// the plan (backup against target, and the file mapping) and the execution. The backup file must be where the SERVER can read it (a path as the server
/// sees it); nothing here depends on how the instance is hosted. The pieces are <see cref="SqlServerRestorePreCheck"/> and
/// <see cref="SqlServerRestorePlanner"/> (pure decisions), <see cref="SqlServerRestoreInspector"/> and <see cref="SqlServerRestoreExecutor"/> (statements),
/// and <see cref="SqlServerRestoreCommandBuilder"/> (the only T-SQL text); this type wires them to the contract and gathers the facts the pure parts decide on.
/// </summary>
public sealed class SqlServerRestoreProvider : IDatabaseRestoreProvider
{
    private readonly ILogger<SqlServerRestoreProvider> _logger;
    private readonly SqlServerRestoreInspector _inspector;
    private readonly SqlServerRestoreExecutor _executor;

    public SqlServerRestoreProvider(ILogger<SqlServerRestoreProvider> logger, TimeProvider? timeProvider = null)
        : this(logger, timeProvider, SqlServerRestoreSettings.Default)
    {
    }

    internal SqlServerRestoreProvider(ILogger<SqlServerRestoreProvider> logger, TimeProvider? timeProvider, SqlServerRestoreSettings settings)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(settings);
        _logger = logger;
        var time = timeProvider ?? TimeProvider.System;
        _inspector = new SqlServerRestoreInspector(logger, time);
        _executor = new SqlServerRestoreExecutor(logger, time, settings);
    }

    public DatabaseEngineId Engine => DatabaseEngineId.MsSql;

    public async Task<BackupPreCheckReport> PreCheckRestoreAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        RestorePreCheckRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(request);

        var replace = request.Mode == DatabaseRestoreMode.ReplaceExistingDatabase;
        var (session, connectFailure) = await OpenAsync(connection, credential, cancellationToken).ConfigureAwait(false);

        var target = new SqlServerRestoreTargetFacts(connectFailure, null, null, null, null, null, null, null);
        var permission = SqlServerBackupPermission.Unknown;
        int? sessions = null;

        if (session is not null)
        {
            await using (session.ConfigureAwait(false))
            {
                target = await GatherTargetAsync(session, request.DatabaseName, request.Mode, volumes: request.BackupSizeBytes is not null, cancellationToken).ConfigureAwait(false);
                if (target is { MetadataFailure: null, Instance: not null } && SqlServerRestoreChecks.CheckDatabaseName(request.DatabaseName).Status != BackupPreCheckStatus.Failed)
                {
                    if (!replace)
                    {
                        // CREATE ANY DATABASE (or dbcreator / sysadmin, which include it) is what restoring into a new database needs.
                        permission = await SqlServerRestoreTargetReader.OptionalAsync(
                            () => SqlServerDiscovery.ProbeVerifyPermissionAsync(session, cancellationToken), SqlServerBackupPermission.Unknown).ConfigureAwait(false);
                    }
                    else if (target.Database is not null)
                    {
                        permission = await SqlServerRestoreTargetReader.OptionalAsync(
                            () => SqlServerRestoreTargetReader.ProbeExistingRestorePermissionAsync(session, request.DatabaseName, cancellationToken),
                            SqlServerBackupPermission.Unknown).ConfigureAwait(false);
                        sessions = await SqlServerRestoreTargetReader.OptionalAsync(
                            async () => (int?)await SqlServerRestoreTargetReader.CountSessionsAsync(session, request.DatabaseName, cancellationToken).ConfigureAwait(false),
                            null).ConfigureAwait(false);
                    }
                }
            }
        }

        return new BackupPreCheckReport(SqlServerRestorePreCheck.Evaluate(new SqlServerRestorePreCheckFacts(target, permission, sessions), request));
    }

    public Task<RestoreInspectionResult> InspectBackupAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        RestoreInspectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(request);
        return _inspector.InspectAsync(connection, credential, request, cancellationToken);
    }

    public async Task<RestorePlanResult> PlanRestoreAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        RestorePlanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(request);

        var replace = request.Mode == DatabaseRestoreMode.ReplaceExistingDatabase;
        var (session, connectFailure) = await OpenAsync(connection, credential, cancellationToken).ConfigureAwait(false);

        var target = new SqlServerRestoreTargetFacts(connectFailure, null, null, null, null, null, null, null);
        Guid? family = null;
        IReadOnlyList<SqlServerMasterFile>? masterFiles = null;

        if (session is not null)
        {
            await using (session.ConfigureAwait(false))
            {
                target = await GatherTargetAsync(session, request.DatabaseName, request.Mode, volumes: true, cancellationToken).ConfigureAwait(false);
                if (target is { MetadataFailure: null, Instance: not null })
                {
                    masterFiles = await SqlServerRestoreTargetReader.OptionalAsync(
                        async () => (IReadOnlyList<SqlServerMasterFile>?)await SqlServerRestoreTargetReader.ReadMasterFilesAsync(session, cancellationToken).ConfigureAwait(false),
                        null).ConfigureAwait(false);
                    if (replace && target.Database is not null)
                    {
                        family = await SqlServerRestoreTargetReader.OptionalAsync(
                            () => SqlServerRestoreTargetReader.ReadFamilyGuidAsync(session, request.DatabaseName, cancellationToken), null).ConfigureAwait(false);
                    }
                }
            }
        }

        var evaluation = SqlServerRestorePlanner.Evaluate(new SqlServerRestorePlanFacts(target, family, masterFiles), request);
        return new RestorePlanResult(new BackupPreCheckReport(evaluation.Items), evaluation.Plan);
    }

    public Task<DatabaseRestoreResult> ExecuteRestoreAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        DatabaseRestoreRequest request,
        IProgress<RestoreProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(request);
        return _executor.ExecuteAsync(connection, credential, request, progress, cancellationToken);
    }

    private async Task<(SqlServerSession? Session, DatabaseProviderException? Failure)> OpenAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await SqlServerSession.OpenAsync(connection, credential, _logger, cancellationToken).ConfigureAwait(false), null);
        }
        catch (DatabaseProviderException exception)
        {
            return (null, exception);
        }
    }

    /// <summary>
    /// Reads what the target tells about itself: version and platform, the named database, the default directories, and (best effort, each one
    /// independently) availability-group membership, mirroring and volume space. The server metadata and the database lookup are essential: if they fail
    /// the facts carry the failure. Everything else that cannot be read is null and becomes an "unknown" Warning in the decision.
    /// </summary>
    private static async Task<SqlServerRestoreTargetFacts> GatherTargetAsync(
        SqlServerSession session,
        string databaseName,
        DatabaseRestoreMode mode,
        bool volumes,
        CancellationToken cancellationToken)
    {
        SqlServerInstanceInfo instance;
        SqlServerDatabaseInfo? database;
        try
        {
            instance = await SqlServerDiscovery.ReadInstanceAsync(session, cancellationToken).ConfigureAwait(false);
            database = await SqlServerDiscovery.FindDatabaseAsync(session, databaseName, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseProviderException exception)
        {
            return new SqlServerRestoreTargetFacts(null, exception, null, null, null, null, null, null);
        }

        var directories = await SqlServerRestoreTargetReader.OptionalAsync(
            async () => (SqlServerRawDirectories?)await SqlServerRestoreTargetReader.ReadDirectoriesAsync(session, cancellationToken).ConfigureAwait(false), null).ConfigureAwait(false);

        bool? inAvailabilityGroup = null;
        bool? mirrored = null;
        if (mode == DatabaseRestoreMode.ReplaceExistingDatabase && database is not null)
        {
            inAvailabilityGroup = await SqlServerRestoreTargetReader.OptionalAsync(
                async () => (bool?)await SqlServerRestoreTargetReader.IsInAvailabilityGroupAsync(session, databaseName, cancellationToken).ConfigureAwait(false), null).ConfigureAwait(false);
            mirrored = await SqlServerRestoreTargetReader.OptionalAsync(
                async () => (bool?)await SqlServerRestoreTargetReader.IsMirroredAsync(session, databaseName, cancellationToken).ConfigureAwait(false), null).ConfigureAwait(false);
        }

        IReadOnlyList<SqlServerVolume>? volumeFacts = null;
        if (volumes)
        {
            volumeFacts = await SqlServerRestoreTargetReader.OptionalAsync(
                async () => (IReadOnlyList<SqlServerVolume>?)await SqlServerRestoreTargetReader.ReadVolumesAsync(session, cancellationToken).ConfigureAwait(false), null).ConfigureAwait(false);
        }

        return new SqlServerRestoreTargetFacts(null, null, instance, database, directories, inAvailabilityGroup, mirrored, volumeFacts);
    }
}
