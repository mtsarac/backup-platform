using System.Globalization;
using System.Text.Json;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.PostgreSql.Connectivity;
using BackupPlatform.BackupProviders.PostgreSql.Discovery;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BackupPlatform.BackupProviders.PostgreSql.Restore;

/// <summary>
/// Proves a restored <c>bp_verify_*</c> database usable and removes platform-created databases. Nothing here needs more than the privileges of the login that
/// created the database: it only reads the database it owns, and drops it as its owner. Every failure is a fixed sentence plus a stable reason code.
/// </summary>
internal static class PostgreSqlRestoreVerifier
{
    /// <summary>The most tables whose rows are read; a database with more is still verified on this many.</summary>
    internal const int MaxTablesRead = 500;

    private const string PostgresErrorObjectInUse = "55006";

    public static async Task<RestoreVerificationReport> VerifyAsync(
        ILogger logger, DatabaseConnectionSettings connection, DatabaseCredential credential, RestoreVerificationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(request);

        var checks = new List<RestoreVerificationCheck>(3);
        var current = RestoreVerificationCheck.DatabaseOnline;
        int? userTables = null, readableTables = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Timeout);
        var token = deadline.Token;
        try
        {
            var target = PostgreSqlConnectionTarget.Create(connection, credential);
            if (await CheckOnlineAsync(logger, target, request.DatabaseName, token).ConfigureAwait(false) is { } offline)
            {
                checks.Add(offline);
                return new RestoreVerificationReport(checks);
            }

            await using var session = await PostgreSqlSession.OpenAsync(target, request.DatabaseName, logger, token).ConfigureAwait(false);
            checks.Add(new RestoreVerificationCheck(current, true, PostgreSqlVerificationReasons.Ok, "The verification database is online and accepts connections."));
            await session.ExecuteAsync(string.Create(CultureInfo.InvariantCulture, $"SET statement_timeout = {(long)request.Timeout.TotalMilliseconds}"), request.Timeout, token).ConfigureAwait(false);

            current = RestoreVerificationCheck.Integrity;
            var integrity = await CheckIntegrityAsync(session, token).ConfigureAwait(false);
            checks.Add(integrity);
            if (!integrity.Passed) return new RestoreVerificationReport(checks);

            current = RestoreVerificationCheck.DataReadable;
            var (readable, total, read) = await ReadTablesAsync(session, token).ConfigureAwait(false);
            checks.Add(readable);
            userTables = total;
            readableTables = read;
        }
        catch (DatabaseProviderException exception)
        {
            var fallback = current switch
            {
                RestoreVerificationCheck.Integrity => PostgreSqlVerificationReasons.IntegrityCheckFailed,
                RestoreVerificationCheck.DataReadable => PostgreSqlVerificationReasons.TableUnreadable,
                _ => PostgreSqlReasons.UnexpectedError
            };
            checks.Add(Failed(current, ReasonFor(exception, fallback), exception.Message));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            checks.Add(Failed(current, PostgreSqlVerificationReasons.Timeout, "The verification ran out of time."));
        }

        return new RestoreVerificationReport(checks, userTables, readableTables);
    }

    public static async Task<DatabaseDropResult> DropAsync(
        ILogger logger, DatabaseConnectionSettings connection, DatabaseCredential credential, DatabaseDropRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(request);

        // The request already checked this; the pattern is the ownership proof of a database the platform may delete, so it is repeated where the DROP is issued.
        var owned = request.Kind == DatabaseDropKind.VerificationDatabase
            ? RestoreDatabaseNames.IsVerificationName(request.DatabaseName)
            : RestoreDatabaseNames.IsRollbackName(request.DatabaseName);
        if (!owned) return new DatabaseDropResult(DatabaseDropOutcome.Failed, PostgreSqlReasons.DatabaseNameInvalid);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(request.Timeout);
        var token = deadline.Token;
        try
        {
            var target = PostgreSqlConnectionTarget.Create(connection, credential);
            await using var session = await PostgreSqlSession.OpenAsync(target, "postgres", logger, token).ConfigureAwait(false);
            var rows = await session.QueryAsync("SELECT shobj_description(oid, 'pg_database') FROM pg_database WHERE datname = @name", token, ("name", request.DatabaseName)).ConfigureAwait(false);
            if (rows.Count == 0) return new DatabaseDropResult(DatabaseDropOutcome.NotFound);
            // A bp_old_ database holds the previous contents of a replaced database: only the marker the restore itself stamped may release it.
            if (request.Kind == DatabaseDropKind.RollbackCopy && !IsPreviousMarker(rows[0][0] as string))
                return new DatabaseDropResult(DatabaseDropOutcome.Failed, PostgreSqlVerificationReasons.RollbackMarkerMissing);

            // WITH (FORCE) ends the sessions the caller may terminate (the same rule as pg_terminate_backend) and fails with "in use" for the rest.
            await session.ExecuteAsync($"DROP DATABASE {PostgreSqlIdentifiers.Quote(request.DatabaseName)} WITH (FORCE)", request.Timeout, token).ConfigureAwait(false);
            return new DatabaseDropResult(DatabaseDropOutcome.Dropped);
        }
        catch (DatabaseProviderException exception)
        {
            return exception.InnerException switch
            {
                PostgresException { SqlState: PostgresErrorCodes.InvalidCatalogName } => new DatabaseDropResult(DatabaseDropOutcome.NotFound),
                PostgresException { SqlState: PostgresErrorObjectInUse } => new DatabaseDropResult(DatabaseDropOutcome.Failed, PostgreSqlVerificationReasons.DatabaseInUse),
                _ => new DatabaseDropResult(DatabaseDropOutcome.Failed, ReasonFor(exception, PostgreSqlVerificationReasons.DropFailed))
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DatabaseDropResult(DatabaseDropOutcome.Failed, PostgreSqlVerificationReasons.Timeout);
        }
    }

    /// <summary>True only for the comment a replacing restore stamps on the database it retires: provider <c>backup-platform</c>, phase <c>previous</c>.</summary>
    internal static bool IsPreviousMarker(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return false;
        try
        {
            using var document = JsonDocument.Parse(comment);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("provider", out var provider) && provider.ValueKind == JsonValueKind.String && provider.GetString() == "backup-platform"
                && root.TryGetProperty("phase", out var phase) && phase.ValueKind == JsonValueKind.String && phase.GetString() == "previous";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<RestoreVerificationCheck?> CheckOnlineAsync(ILogger logger, PostgreSqlConnectionTarget target, string name, CancellationToken token)
    {
        await using var maintenance = await PostgreSqlSession.OpenAsync(target, "postgres", logger, token).ConfigureAwait(false);
        var database = await PostgreSqlDiscovery.FindDatabaseAsync(maintenance, name, token).ConfigureAwait(false);
        if (database is null)
            return Failed(RestoreVerificationCheck.DatabaseOnline, PostgreSqlReasons.DatabaseNotFound, "The verification database does not exist.");
        return database.AllowsConnections && database.CanConnect
            ? null
            : Failed(RestoreVerificationCheck.DatabaseOnline, PostgreSqlReasons.DatabaseNotConnectable, "The verification database does not accept connections from this login.");
    }

    /// <summary>
    /// Catalog sanity only: no invalid or not-ready index and no relation without a schema. The restore already rebuilt every index and constraint, and the
    /// page-level <c>amcheck</c> extension is deliberately not used because creating it needs a superuser, which a verification login does not have.
    /// </summary>
    private static async Task<RestoreVerificationCheck> CheckIntegrityAsync(PostgreSqlSession session, CancellationToken token)
    {
        var rows = await session.QueryAsync(
            """
            SELECT (SELECT count(*) FROM pg_index WHERE NOT indisvalid OR NOT indisready),
                   (SELECT count(*) FROM pg_class c WHERE NOT EXISTS (SELECT 1 FROM pg_namespace n WHERE n.oid = c.relnamespace))
            """, token).ConfigureAwait(false);
        var clean = Convert.ToInt64(rows[0][0], CultureInfo.InvariantCulture) == 0 && Convert.ToInt64(rows[0][1], CultureInfo.InvariantCulture) == 0;
        return clean
            ? new RestoreVerificationCheck(RestoreVerificationCheck.Integrity, true, PostgreSqlVerificationReasons.Ok,
                "The catalog is consistent: every index is valid and every relation has a schema. The restore rebuilt all indexes and constraints; the amcheck extension was not used because it needs a superuser.")
            : Failed(RestoreVerificationCheck.Integrity, PostgreSqlVerificationReasons.IntegrityCheckFailed, "The catalog shows an invalid index or an orphaned relation.");
    }

    /// <summary>
    /// Reads every row of every user table (at most <see cref="MaxTablesRead"/>) through its text form, which makes the server detoast and decode every value, and
    /// counts the large objects. A restore alone cannot prove the stored data can be read back.
    /// </summary>
    private static async Task<(RestoreVerificationCheck Check, int? Total, int? Read)> ReadTablesAsync(PostgreSqlSession session, CancellationToken token)
    {
        var tables = await session.QueryAsync(
            $"""
            SELECT n.nspname, c.relname, count(*) OVER ()
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'r' AND n.nspname <> 'information_schema' AND n.nspname NOT LIKE 'pg\_%'
            ORDER BY n.nspname, c.relname LIMIT {MaxTablesRead}
            """, token).ConfigureAwait(false);
        var total = tables.Count == 0 ? 0 : Convert.ToInt32(tables[0][2], CultureInfo.InvariantCulture);
        var read = 0;
        foreach (var table in tables)
        {
            var qualified = $"{PostgreSqlIdentifiers.Quote((string)table[0]!)}.{PostgreSqlIdentifiers.Quote((string)table[1]!)}";
            try
            {
                await session.QueryAsync($"SELECT count(*), COALESCE(sum(octet_length(t::text)), 0) FROM {qualified} t", token).ConfigureAwait(false);
            }
            catch (DatabaseProviderException exception)
            {
                return (Failed(RestoreVerificationCheck.DataReadable, ReasonFor(exception, PostgreSqlVerificationReasons.TableUnreadable),
                    "A user table of the verification database could not be read."), total, read);
            }
            read++;
        }

        var largeObjects = Convert.ToInt64(await session.ScalarAsync("SELECT count(*) FROM pg_largeobject_metadata", token).ConfigureAwait(false), CultureInfo.InvariantCulture);
        var message = (total > read ? $"Read {read} of {total} user tables (the first {MaxTablesRead})" : $"Read all {total} user tables")
            + $"; {largeObjects.ToString(CultureInfo.InvariantCulture)} large object(s) present.";
        return (new RestoreVerificationCheck(RestoreVerificationCheck.DataReadable, true, PostgreSqlVerificationReasons.Ok, message), total, read);
    }

    private static RestoreVerificationCheck Failed(string checkId, string reasonCode, string message) => new(checkId, false, reasonCode, message);

    /// <summary>The stable reason of a failed engine call; <paramref name="fallback"/> is the one for a server error that has no better name.</summary>
    private static string ReasonFor(DatabaseProviderException exception, string fallback) => exception.Kind switch
    {
        DatabaseFailureKind.AuthenticationFailed => PostgreSqlReasons.AuthenticationFailed,
        DatabaseFailureKind.PermissionDenied => PostgreSqlReasons.PermissionDenied,
        DatabaseFailureKind.Timeout => PostgreSqlVerificationReasons.Timeout,
        DatabaseFailureKind.Unreachable => PostgreSqlVerificationReasons.ConnectionFailed,
        DatabaseFailureKind.InvalidConfiguration => PostgreSqlSession.IsDatabaseMissing(exception) ? PostgreSqlReasons.DatabaseNotFound : PostgreSqlReasons.ConnectionSettingsInvalid,
        _ => fallback
    };
}
