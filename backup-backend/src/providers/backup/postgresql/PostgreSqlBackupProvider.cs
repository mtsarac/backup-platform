using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.PostgreSql.Backup;
using BackupPlatform.BackupProviders.PostgreSql.Connectivity;
using BackupPlatform.BackupProviders.PostgreSql.Discovery;
using BackupPlatform.BackupProviders.PostgreSql.Tools;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.BackupProviders.PostgreSql;

/// <summary>Consistent database-scoped logical Full Backup over TCP, using native PostgreSQL client tools on the agent.</summary>
public sealed class PostgreSqlBackupProvider : IDatabaseBackupProvider
{
    private readonly ILogger<PostgreSqlBackupProvider> _logger;
    private readonly PostgreSqlClientTools _tools;
    private readonly TimeProvider _time;
    private readonly PostgreSqlBackupMode _mode;

    public PostgreSqlBackupProvider(ILogger<PostgreSqlBackupProvider> logger, PostgreSqlProviderOptions? options = null, TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _tools = new PostgreSqlClientTools(options ?? new PostgreSqlProviderOptions(), logger);
        _mode = (options ?? new PostgreSqlProviderOptions()).BackupMode;
        if (!Enum.IsDefined(_mode)) throw new ArgumentOutOfRangeException(nameof(options));
        _time = timeProvider ?? TimeProvider.System;
    }

    public DatabaseEngineId Engine => DatabaseEngineId.PostgreSql;

    public async Task<DatabaseInstanceDiscovery> DiscoverAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, CancellationToken cancellationToken)
    {
        var target = PostgreSqlConnectionTarget.Create(connection, credential);
        await using var session = await PostgreSqlSession.OpenAsync(target, "postgres", _logger, cancellationToken).ConfigureAwait(false);
        var server = await PostgreSqlDiscovery.ReadServerAsync(session, cancellationToken).ConfigureAwait(false);
        var databases = await PostgreSqlDiscovery.ListDatabasesAsync(session, cancellationToken).ConfigureAwait(false);
        var capabilities = PostgreSqlCapabilityEvaluator.EvaluateInstance(server);
        return new DatabaseInstanceDiscovery(Instance(connection, server), capabilities,
            databases.Select(database => new DatabaseInfo(database.Name, database.IsSystem,
                database.AllowsConnections && database.CanConnect ? DatabaseAvailability.Online : DatabaseAvailability.Unavailable,
                PostgreSqlCapabilityEvaluator.EvaluateDatabase(capabilities, database))).ToArray());
    }

    public async Task<BackupPreCheckReport> PreCheckFullBackupAsync(DatabaseConnectionSettings connection, DatabaseCredential credential,
        FullBackupPreCheckRequest request, CancellationToken cancellationToken)
    {
        if (_mode != PostgreSqlBackupMode.Logical)
            return await new PostgreSqlPhysicalBackupExecutor(this, _tools, _logger, _time, _mode).PreCheckAsync(connection, credential, request, cancellationToken).ConfigureAwait(false);
        var items = new List<BackupPreCheckItem>();
        if (!PostgreSqlIdentifiers.IsValidDatabaseName(request.DatabaseName))
        {
            items.Add(PostgreSqlFailures.Failed(BackupPreCheckIds.DatabaseExists, PostgreSqlReasons.DatabaseNameInvalid,
                "PostgreSQL database names must contain 1–63 UTF-8 bytes and no control characters.", BackupFailureCause.InvalidConfiguration));
            return new BackupPreCheckReport(items);
        }
        PostgreSqlBackupFacts? facts = null;
        try
        {
            var target = PostgreSqlConnectionTarget.Create(connection, credential);
            await using var session = await PostgreSqlSession.OpenAsync(target, request.DatabaseName, _logger, cancellationToken).ConfigureAwait(false);
            items.Add(PostgreSqlFailures.Passed(BackupPreCheckIds.ServerReachable, "The PostgreSQL server is reachable."));
            items.Add(PostgreSqlFailures.Passed(BackupPreCheckIds.Authentication, "The login authenticated to the requested database."));
            facts = await PostgreSqlBackupFacts.ReadAsync(session, request.DatabaseName, cancellationToken).ConfigureAwait(false);
            items.Add(PostgreSqlFailures.Passed(BackupPreCheckIds.DatabaseExists, "The requested database exists and accepts this login."));
            PostgreSqlBackupChecks.CheckFacts(items, facts, request);
        }
        catch (DatabaseProviderException exception)
        {
            var failure = PostgreSqlFailures.FromConnection(exception);
            items.Add(PostgreSqlFailures.Failed(failure.Cause == BackupFailureCause.DatabaseNotFound ? BackupPreCheckIds.DatabaseExists
                : failure.Cause == BackupFailureCause.AuthenticationFailed ? BackupPreCheckIds.Authentication : BackupPreCheckIds.ServerReachable,
                failure.ReasonCode, failure.Message, failure.Cause));
        }
        var dump = await _tools.ResolveAsync(PostgreSqlClientTools.PgDump, cancellationToken).ConfigureAwait(false);
        var restore = await _tools.ResolveAsync(PostgreSqlClientTools.PgRestore, cancellationToken).ConfigureAwait(false);
        items.Add(PostgreSqlBackupChecks.CheckTool(dump, PostgreSqlClientTools.PgDump, facts?.Server.Major));
        items.Add(PostgreSqlBackupChecks.CheckTool(restore, PostgreSqlClientTools.PgRestore, dump.Tool?.Major));
        return new BackupPreCheckReport(items);
    }

    public Task<FullBackupResult> ExecuteFullBackupAsync(DatabaseConnectionSettings connection, DatabaseCredential credential,
        FullBackupRequest request, IProgress<BackupProgress>? progress, CancellationToken cancellationToken) =>
        _mode == PostgreSqlBackupMode.Logical
            ? new PostgreSqlFullBackupExecutor(this, _tools, _logger, _time).ExecuteAsync(connection, credential, request, progress, cancellationToken)
            : new PostgreSqlPhysicalBackupExecutor(this, _tools, _logger, _time, _mode).ExecuteAsync(connection, credential, request, progress, cancellationToken);

    internal static DatabaseInstanceInfo Instance(DatabaseConnectionSettings connection, PostgreSqlServerInfo server) =>
        new(DatabaseEngineId.PostgreSql, connection.Endpoint.Host, null, server.Version, "PostgreSQL", server.HostPlatform);
}
