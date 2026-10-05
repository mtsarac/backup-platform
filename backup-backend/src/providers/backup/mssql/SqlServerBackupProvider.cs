using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Backup;
using BackupPlatform.BackupProviders.MSSQL.Capabilities;
using BackupPlatform.BackupProviders.MSSQL.Connectivity;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using BackupPlatform.BackupProviders.MSSQL.PreChecks;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.BackupProviders.MSSQL;

/// <summary>
/// SQL Server discovery, capability detection, full-backup pre-check and full-backup execution over the normal SQL network
/// protocol. Nothing here depends on how the instance is hosted: a native Windows service, a native Linux service and a
/// container that publishes its port are the same engine at a different address. The execution (backup, verification, cleanup)
/// is <see cref="SqlServerFullBackupExecutor"/>; this type only wires it to the contract.
/// </summary>
public sealed partial class SqlServerBackupProvider : IDatabaseBackupProvider
{
    private readonly ILogger<SqlServerBackupProvider> _logger;
    private readonly IReadOnlyList<ISqlServerPreCheckExtension> _extensions;
    private readonly SqlServerFullBackupExecutor _executor;

    public SqlServerBackupProvider(
        ILogger<SqlServerBackupProvider> logger,
        IEnumerable<ISqlServerPreCheckExtension>? extensions = null,
        TimeProvider? timeProvider = null)
        : this(logger, extensions, timeProvider, SqlServerExecutionSettings.Default)
    {
    }

    internal SqlServerBackupProvider(
        ILogger<SqlServerBackupProvider> logger,
        IEnumerable<ISqlServerPreCheckExtension>? extensions,
        TimeProvider? timeProvider,
        SqlServerExecutionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(settings);
        _logger = logger;
        _extensions = extensions?.ToArray() ?? [];
        _executor = new SqlServerFullBackupExecutor(logger, timeProvider ?? TimeProvider.System, settings);
    }

    public DatabaseEngineId Engine => DatabaseEngineId.MsSql;

    /// <summary>The typed discovery result: server metadata plus every database with state, recovery model, compatibility level and system flag.</summary>
    public async Task<SqlServerInstanceDiscovery> DiscoverSqlServerAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(credential);

        await using var session = await SqlServerSession.OpenAsync(connection, credential, _logger, cancellationToken).ConfigureAwait(false);
        var instance = await SqlServerDiscovery.ReadInstanceAsync(session, cancellationToken).ConfigureAwait(false);
        var databases = await SqlServerDiscovery.ListDatabasesAsync(session, cancellationToken).ConfigureAwait(false);
        return new SqlServerInstanceDiscovery(instance, databases);
    }

    public async Task<DatabaseInstanceDiscovery> DiscoverAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        CancellationToken cancellationToken)
    {
        var discovery = await DiscoverSqlServerAsync(connection, credential, cancellationToken).ConfigureAwait(false);
        var instanceCapabilities = SqlServerCapabilityEvaluator.EvaluateInstance(discovery.Instance);

        var instance = discovery.Instance;
        return new DatabaseInstanceDiscovery(
            SqlServerInstanceInfoMapping.ToInstanceInfo(instance),
            instanceCapabilities,
            discovery.Databases
                .Select(database => new DatabaseInfo(
                    database.Name,
                    database.IsSystem,
                    database.State switch
                    {
                        SqlServerDatabaseState.Online => DatabaseAvailability.Online,
                        SqlServerDatabaseState.Unknown => DatabaseAvailability.Unknown,
                        _ => DatabaseAvailability.Unavailable
                    },
                    SqlServerCapabilityEvaluator.EvaluateDatabase(instanceCapabilities, database)))
                .ToArray());
    }

    public async Task<BackupPreCheckReport> PreCheckFullBackupAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        FullBackupPreCheckRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(request);

        SqlServerSession? session = null;
        DatabaseProviderException? connectFailure = null;
        try
        {
            session = await SqlServerSession.OpenAsync(connection, credential, _logger, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseProviderException exception)
        {
            connectFailure = exception;
        }

        SqlServerInstanceInfo? instance = null;
        SqlServerDatabaseInfo? database = null;
        var permission = SqlServerBackupPermission.Unknown;
        var verifyPermission = SqlServerBackupPermission.Unknown;
        DatabaseProviderException? metadataFailure = null;
        SqlServerStagingFacts? staging = null;

        if (session is not null)
        {
            await using (session.ConfigureAwait(false))
            {
                try
                {
                    instance = await SqlServerDiscovery.ReadInstanceAsync(session, cancellationToken).ConfigureAwait(false);
                    database = await SqlServerDiscovery.FindDatabaseAsync(session, request.DatabaseName, cancellationToken).ConfigureAwait(false);
                    // HAS_PERMS_BY_NAME answers 0 (not NULL) for a database it cannot evaluate, even for sysadmin (observed for OFFLINE
                    // databases on SQL Server 2025), so the answer is only trusted while the database is online.
                    if (database is { State: SqlServerDatabaseState.Online })
                    {
                        permission = await SqlServerDiscovery.ProbeBackupPermissionAsync(session, request.DatabaseName, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (database is not null)
                    {
                        verifyPermission = await SqlServerDiscovery.ProbeVerifyPermissionAsync(session, cancellationToken).ConfigureAwait(false);
                    }

                    if (instance is not null && database is not null && request.Staging is { } stagingArea)
                    {
                        var pathFormatUsable = SqlServerFullBackupPreCheck.CheckStagingPathFormat(stagingArea.ServerDirectory, instance.HostPlatform).Status
                            != BackupPreCheckStatus.Failed;
                        staging = await SqlServerStagingProbe.GatherAsync(session, request, database, pathFormatUsable, _logger, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                catch (DatabaseProviderException exception)
                {
                    metadataFailure = exception;
                }
            }
        }

        var items = new List<BackupPreCheckItem>(SqlServerFullBackupPreCheck.Evaluate(
            new SqlServerPreCheckFacts(connectFailure, metadataFailure, instance, database, permission, staging, verifyPermission), request));

        if (instance is not null && database is not null && metadataFailure is null)
        {
            var context = new SqlServerPreCheckContext(request, instance, database);
            foreach (var extension in _extensions)
            {
                items.AddRange(await RunExtensionAsync(extension, context, cancellationToken).ConfigureAwait(false));
            }
        }

        return new BackupPreCheckReport(items);
    }

    /// <summary>
    /// Takes one full backup into the staging directory and verifies it with <c>RESTORE VERIFYONLY</c>; see
    /// <see cref="SqlServerFullBackupExecutor"/> for the flow, the staging watchdog and the cleanup rules. Stages are reported as
    /// Preparing, BackingUp and Verifying only: Completed, Failed and Cancelled are terminal stages owned by the pipeline around
    /// the provider, which also owns the staged file once the result says Succeeded.
    /// </summary>
    public Task<FullBackupResult> ExecuteFullBackupAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        FullBackupRequest request,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(request);
        return _executor.ExecuteAsync(connection, credential, request, progress, cancellationToken);
    }

    /// <summary>Runs <c>RESTORE VERIFYONLY</c> against a file the server can see. Internal: the execution itself is the supported path; this exists so verification can be exercised on a prepared (for example damaged) file.</summary>
    internal Task<SqlServerVerifyOutcome> VerifyBackupFileAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        DatabaseServerPath serverFile,
        bool checksumApplied,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        _executor.VerifyFileAsync(connection, credential, serverFile, checksumApplied, timeout, cancellationToken);

    private async Task<IReadOnlyList<BackupPreCheckItem>> RunExtensionAsync(
        ISqlServerPreCheckExtension extension,
        SqlServerPreCheckContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await extension.CheckAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogExtensionFailed(exception, extension.GetType().Name);
            return
            [
                new BackupPreCheckItem(
                    $"extension.{extension.GetType().Name.ToLowerInvariant()}",
                    BackupPreCheckStatus.Failed,
                    SqlServerPreCheckReasons.ExtensionFailed,
                    "A pre-check extension failed unexpectedly. Details are in the agent log.")
            ];
        }
    }

    [LoggerMessage(EventId = 10, Level = LogLevel.Error, Message = "SQL Server pre-check extension failed: Extension={Extension}")]
    private partial void LogExtensionFailed(Exception exception, string extension);
}
