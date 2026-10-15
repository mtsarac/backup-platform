using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Backup;
using BackupPlatform.BackupProviders.MSSQL.Connectivity;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.BackupProviders.MSSQL.Restore;

/// <summary>Stable reason codes of an isolated restore verification on SQL Server; the sentences are fixed and never carry engine text.</summary>
internal static class SqlServerVerificationReasons
{
    public const string Ok = "ok";
    public const string DatabaseNotOnline = "database-not-online";
    public const string IntegrityCheckFailed = "integrity-check-failed";
    public const string IntegrityCheckTimeout = "integrity-check-timeout";
    public const string TableUnreadable = "table-unreadable";
    public const string Timeout = "timeout";
    public const string PermissionDenied = "permission-denied";
    public const string AuthenticationFailed = "authentication-failed";
    public const string ConnectionFailed = "connection-failed";
    public const string UnexpectedError = "unexpected-error";
    public const string UnsupportedKind = "unsupported-kind";
    public const string DatabaseInUse = "database-in-use";
    public const string DropFailed = "drop-failed";

    public static string ForKind(DatabaseFailureKind kind) => kind switch
    {
        DatabaseFailureKind.Timeout => Timeout,
        DatabaseFailureKind.PermissionDenied => PermissionDenied,
        DatabaseFailureKind.AuthenticationFailed => AuthenticationFailed,
        DatabaseFailureKind.Unreachable => ConnectionFailed,
        _ => UnexpectedError
    };
}

/// <summary>
/// Proves a restored <c>bp_verify_*</c> database usable and removes platform-created databases. Both run on one short-lived session against
/// master: the database is addressed by its three-part names, so no connection ever holds it open and the drop never has to evict its own session.
/// </summary>
internal sealed partial class SqlServerRestoreVerifier(ILogger logger, TimeProvider time, SqlServerRestoreSettings settings)
{
    /// <summary>The most tables whose rows are read; a database with more is still verified on this many.</summary>
    internal const int MaxTablesRead = 500;

    private const int DatabaseDoesNotExist = 3701;
    private const int DatabaseInUse = 3702;

    public async Task<RestoreVerificationReport> VerifyAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        RestoreVerificationRequest request,
        CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        TimeSpan Remaining() => request.Timeout - time.GetElapsedTime(started);

        SqlServerSession session;
        try
        {
            session = await SqlServerSession.OpenAsync(connection, credential, logger, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseProviderException exception)
        {
            return Failed(RestoreVerificationCheck.DatabaseOnline, exception.Kind);
        }

        await using (session.ConfigureAwait(false))
        {
            var checks = new List<RestoreVerificationCheck>(3);

            SqlServerObservedDatabase? observed;
            try
            {
                observed = await SqlServerRestoreTargetReader.ObserveAsync(session, request.DatabaseName, cancellationToken).ConfigureAwait(false);
            }
            catch (DatabaseProviderException exception)
            {
                return Failed(RestoreVerificationCheck.DatabaseOnline, exception.Kind);
            }

            if (observed is not { State: SqlServerDatabaseState.Online })
            {
                checks.Add(new RestoreVerificationCheck(RestoreVerificationCheck.DatabaseOnline, false, SqlServerVerificationReasons.DatabaseNotOnline,
                    "The verification database does not exist or is not online."));
                return new RestoreVerificationReport(checks);
            }

            checks.Add(new RestoreVerificationCheck(RestoreVerificationCheck.DatabaseOnline, true, SqlServerVerificationReasons.Ok, "The verification database is online."));

            var integrity = await CheckIntegrityAsync(session, request.DatabaseName, Remaining(), cancellationToken).ConfigureAwait(false);
            checks.Add(integrity);
            if (!integrity.Passed)
            {
                return new RestoreVerificationReport(checks);
            }

            var (readable, total, read) = await ReadTablesAsync(session, request.DatabaseName, Remaining, cancellationToken).ConfigureAwait(false);
            checks.Add(readable);
            return new RestoreVerificationReport(checks, total, read);
        }
    }

    public async Task<DatabaseDropResult> DropAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        DatabaseDropRequest request,
        CancellationToken cancellationToken)
    {
        // SQL Server never keeps a rollback copy, so there is nothing of that kind to drop; the name check is repeated here rather than trusted from the request.
        if (request.Kind != DatabaseDropKind.VerificationDatabase || !RestoreDatabaseNames.IsVerificationName(request.DatabaseName))
        {
            return new DatabaseDropResult(DatabaseDropOutcome.Failed, SqlServerVerificationReasons.UnsupportedKind);
        }

        var started = time.GetTimestamp();
        TimeSpan Remaining(TimeSpan cap) => Max1s(Min(request.Timeout - time.GetElapsedTime(started), cap));

        SqlServerSession session;
        try
        {
            session = await SqlServerSession.OpenAsync(connection, credential, logger, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseProviderException exception)
        {
            return new DatabaseDropResult(DatabaseDropOutcome.Failed, SqlServerVerificationReasons.ForKind(exception.Kind));
        }

        await using (session.ConfigureAwait(false))
        {
            try
            {
                var observed = await SqlServerRestoreTargetReader.ObserveAsync(session, request.DatabaseName, cancellationToken).ConfigureAwait(false);
                if (observed is null)
                {
                    return new DatabaseDropResult(DatabaseDropOutcome.NotFound);
                }

                if (observed.State == SqlServerDatabaseState.Online)
                {
                    // Best effort: it only evicts stray sessions. A database that cannot be switched is still dropped, and DROP reports what is wrong.
                    await TryAsync(session, SqlServerRestoreCommandBuilder.BuildSetSingleUser(request.DatabaseName), Remaining(settings.AccessChangeTimeout), cancellationToken).ConfigureAwait(false);
                }

                await session.ExecuteAsync(
                    $"DROP DATABASE {SqlServerBackupCommandBuilder.QuoteIdentifier(request.DatabaseName)};",
                    Remaining(request.Timeout), onInfoMessage: null, cancellationToken).ConfigureAwait(false);
                return new DatabaseDropResult(DatabaseDropOutcome.Dropped);
            }
            catch (DatabaseProviderException exception)
            {
                return new DatabaseDropResult(DatabaseDropOutcome.Failed, SqlServerVerificationReasons.ForKind(exception.Kind));
            }
            catch (SqlException exception) when (!cancellationToken.IsCancellationRequested)
            {
                LogDropFailed(logger, exception, exception.Number);
                return exception.Number switch
                {
                    DatabaseDoesNotExist => new DatabaseDropResult(DatabaseDropOutcome.NotFound),
                    DatabaseInUse => new DatabaseDropResult(DatabaseDropOutcome.Failed, SqlServerVerificationReasons.DatabaseInUse),
                    _ => new DatabaseDropResult(DatabaseDropOutcome.Failed, DropReason(exception.Number))
                };
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max1s(TimeSpan value) => value < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : value;

    private static string DropReason(int number) => SqlServerFailureClassifier.ClassifyNumber(number, SqlServerOperationPhase.Command).Kind switch
    {
        DatabaseFailureKind.Unexpected => SqlServerVerificationReasons.DropFailed,
        var kind => SqlServerVerificationReasons.ForKind(kind)
    };

    private async Task TryAsync(SqlServerSession session, string sql, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await session.ExecuteAsync(sql, timeout, onInfoMessage: null, cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogDropFailed(logger, exception, exception.Number);
        }
    }

    /// <summary>
    /// A full <c>DBCC CHECKDB</c> (never PHYSICAL_ONLY: the point is to catch logical damage too). Any error it raises, or a connection it
    /// takes down, fails the check; the engine's own text stays in the log.
    /// </summary>
    private async Task<RestoreVerificationCheck> CheckIntegrityAsync(SqlServerSession session, string database, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var sql = $"DBCC CHECKDB({SqlServerBackupCommandBuilder.QuoteStringLiteral(database)}) WITH NO_INFOMSGS, ALL_ERRORMSGS;";
        if (timeout < TimeSpan.FromSeconds(1))
        {
            return new RestoreVerificationCheck(RestoreVerificationCheck.Integrity, false, SqlServerVerificationReasons.IntegrityCheckTimeout, "The integrity check ran out of time.");
        }

        try
        {
            await session.ExecuteAsync(sql, timeout, onInfoMessage: null, cancellationToken).ConfigureAwait(false);
            return new RestoreVerificationCheck(RestoreVerificationCheck.Integrity, true, SqlServerVerificationReasons.Ok, "DBCC CHECKDB reported no errors.");
        }
        catch (SqlException exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogCheckFailed(logger, exception, RestoreVerificationCheck.Integrity, exception.Number);
            return SqlServerFailureClassifier.ClassifyNumber(exception.Number, SqlServerOperationPhase.Command).Kind switch
            {
                DatabaseFailureKind.Timeout => new RestoreVerificationCheck(RestoreVerificationCheck.Integrity, false, SqlServerVerificationReasons.IntegrityCheckTimeout, "The integrity check ran out of time."),
                DatabaseFailureKind.PermissionDenied => new RestoreVerificationCheck(RestoreVerificationCheck.Integrity, false, SqlServerVerificationReasons.PermissionDenied, "The login may not run DBCC CHECKDB on the verification database."),
                _ => new RestoreVerificationCheck(RestoreVerificationCheck.Integrity, false, SqlServerVerificationReasons.IntegrityCheckFailed, "DBCC CHECKDB reported errors or could not complete.")
            };
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>
    /// <c>COUNT_BIG(*)</c> of every user table (at most <see cref="MaxTablesRead"/>). Together with CHECKDB this proves the data pages can be scanned by an
    /// ordinary query, which a restore alone does not.
    /// </summary>
    private async Task<(RestoreVerificationCheck Check, int? Total, int? Read)> ReadTablesAsync(
        SqlServerSession session, string database, Func<TimeSpan> remaining, CancellationToken cancellationToken)
    {
        var quoted = SqlServerBackupCommandBuilder.QuoteIdentifier(database);
        IReadOnlyList<(string Schema, string Table, int Total)> tables;
        try
        {
            // Identifiers are bracket-quoted by QuoteIdentifier; there is no parameter form for a database or table name.
            tables = await session.QueryAsync(
                "verify-list-tables",
                $"SELECT TOP ({MaxTablesRead}) s.name, t.name, CONVERT(int, COUNT(*) OVER ()) FROM {quoted}.sys.tables AS t " +
                $"JOIN {quoted}.sys.schemas AS s ON s.schema_id = t.schema_id WHERE t.is_ms_shipped = 0 AND t.is_external = 0 ORDER BY s.name, t.name;",
                bind: null,
                reader => (reader.GetString(0), reader.GetString(1), reader.GetInt32(2)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseProviderException exception)
        {
            return (new RestoreVerificationCheck(RestoreVerificationCheck.DataReadable, false, SqlServerVerificationReasons.ForKind(exception.Kind),
                "The user tables of the verification database could not be listed."), null, null);
        }

        var total = tables.Count == 0 ? 0 : tables[0].Total;
        var read = 0;
        foreach (var (schema, table, _) in tables)
        {
            var budget = remaining();
            if (budget < TimeSpan.FromSeconds(1))
            {
                return (new RestoreVerificationCheck(RestoreVerificationCheck.DataReadable, false, SqlServerVerificationReasons.Timeout,
                    "Reading the user tables ran out of time."), total, read);
            }

            try
            {
                await session.ExecuteAsync(
                    $"SELECT COUNT_BIG(*) FROM {quoted}.{SqlServerBackupCommandBuilder.QuoteIdentifier(schema)}.{SqlServerBackupCommandBuilder.QuoteIdentifier(table)};",
                    budget, onInfoMessage: null, cancellationToken).ConfigureAwait(false);
                read++;
            }
            catch (SqlException exception) when (!cancellationToken.IsCancellationRequested)
            {
                LogCheckFailed(logger, exception, RestoreVerificationCheck.DataReadable, exception.Number);
                var kind = SqlServerFailureClassifier.ClassifyNumber(exception.Number, SqlServerOperationPhase.Command).Kind;
                var reason = kind is DatabaseFailureKind.Timeout or DatabaseFailureKind.PermissionDenied ? SqlServerVerificationReasons.ForKind(kind) : SqlServerVerificationReasons.TableUnreadable;
                return (new RestoreVerificationCheck(RestoreVerificationCheck.DataReadable, false, reason, "A user table of the verification database could not be read."), total, read);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        var message = total > read
            ? $"Read {read} of {total} user tables (the first {MaxTablesRead})."
            : $"Read all {total} user tables.";
        return (new RestoreVerificationCheck(RestoreVerificationCheck.DataReadable, true, SqlServerVerificationReasons.Ok, message), total, read);
    }

    private static RestoreVerificationReport Failed(string checkId, DatabaseFailureKind kind) => new([
        new RestoreVerificationCheck(checkId, false, SqlServerVerificationReasons.ForKind(kind), SqlServerFailureClassifier.MessageFor(kind))]);

    [LoggerMessage(EventId = 45, Level = LogLevel.Warning, Message = "SQL Server verification check failed: Check={Check} SqlErrorNumber={SqlErrorNumber}")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception, string check, int sqlErrorNumber);

    [LoggerMessage(EventId = 46, Level = LogLevel.Warning, Message = "SQL Server verification database drop step failed: SqlErrorNumber={SqlErrorNumber}")]
    private static partial void LogDropFailed(ILogger logger, Exception exception, int sqlErrorNumber);
}
