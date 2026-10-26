using System.Net.Security;
using System.Net.Sockets;
using BackupPlatform.BackupProviders.Abstractions.Database;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BackupPlatform.BackupProviders.PostgreSql.Connectivity;

/// <summary>
/// One short-lived, non-pooled PostgreSQL connection. Every engine exception leaves this type either as caller cancellation or as a
/// <see cref="DatabaseProviderException"/> with a fixed user-safe message; the original exception is logged (SQLSTATE only, never the server's text)
/// and kept as the inner exception for diagnostics.
/// </summary>
internal sealed partial class PostgreSqlSession : IAsyncDisposable
{
    // Backstop for hangs Npgsql's own connect timeout does not bound (name resolution); fires just after the real one.
    private static readonly TimeSpan ConnectGuardGrace = TimeSpan.FromSeconds(5);

    private readonly NpgsqlConnection _connection;
    private readonly ILogger _logger;

    private PostgreSqlSession(NpgsqlConnection connection, ILogger logger)
    {
        _connection = connection;
        _logger = logger;
    }

    public static async Task<PostgreSqlSession> OpenAsync(PostgreSqlConnectionTarget target, string database, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(logger);
        cancellationToken.ThrowIfCancellationRequested();

        var connection = new NpgsqlConnection(target.ToNpgsql(database).ConnectionString);
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        guard.CancelAfter(TimeSpan.FromSeconds(target.ConnectTimeoutSeconds) + ConnectGuardGrace);
        try
        {
            await connection.OpenAsync(guard.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw Translate(logger, "connect", exception, timedOut: exception is OperationCanceledException);
        }

        return new PostgreSqlSession(connection, logger);
    }

    /// <summary>True when the exception (or its cause) is the server's "database does not exist" answer to a connection attempt.</summary>
    public static bool IsDatabaseMissing(Exception exception) =>
        Find(exception) is { SqlState: PostgresErrorCodes.InvalidCatalogName };

    public async Task<IReadOnlyList<object?[]>> QueryAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        try
        {
            await using var command = Command(sql, parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var rows = new List<object?[]>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = new object?[reader.FieldCount];
                for (var index = 0; index < row.Length; index++)
                {
                    row[index] = await reader.IsDBNullAsync(index, cancellationToken).ConfigureAwait(false) ? null : reader.GetValue(index);
                }

                rows.Add(row);
            }

            return rows;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw Translate(_logger, "query", exception, timedOut: exception is OperationCanceledException);
        }
    }

    public async Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        var rows = await QueryAsync(sql, cancellationToken, parameters).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0][0];
    }

    /// <summary>Runs one statement that returns nothing. <paramref name="timeout"/> bounds it; a statement that overruns is cancelled on the server.</summary>
    public async Task ExecuteAsync(string sql, TimeSpan timeout, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        try
        {
            await using var command = Command(sql, parameters);
            command.CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw Translate(_logger, "execute", exception, timedOut: exception is OperationCanceledException);
        }
    }

    private NpgsqlCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, _connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private static PostgresException? Find(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }

    // Npgsql has no dedicated type for "server refused SSL"; certificate failures surface as AuthenticationException.
    private static bool IsTlsFailure(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
            if (e is System.Security.Authentication.AuthenticationException || e is NpgsqlException && e.Message.Contains("SSL/TLS", StringComparison.Ordinal)) return true;
        return false;
    }

    private static DatabaseProviderException Translate(ILogger logger, string phase, Exception exception, bool timedOut)
    {
        var sqlState = Find(exception)?.SqlState;
        LogFailure(logger, exception.GetType().Name, sqlState ?? "none", phase);
        if (timedOut || sqlState == PostgresErrorCodes.QueryCanceled || exception is TimeoutException || exception.InnerException is TimeoutException)
        {
            return new DatabaseProviderException(DatabaseFailureKind.Timeout, "The server did not answer within the time limit.", exception);
        }

        return sqlState switch
        {
            PostgresErrorCodes.InvalidPassword or PostgresErrorCodes.InvalidAuthorizationSpecification =>
                new DatabaseProviderException(DatabaseFailureKind.AuthenticationFailed, "The server rejected the login.", exception),
            PostgresErrorCodes.InsufficientPrivilege =>
                new DatabaseProviderException(DatabaseFailureKind.PermissionDenied, "The login lacks a permission the operation needs.", exception),
            PostgresErrorCodes.InvalidCatalogName =>
                new DatabaseProviderException(DatabaseFailureKind.InvalidConfiguration, "The database does not exist on this server.", exception),
            _ when sqlState is not null && !sqlState.StartsWith("08", StringComparison.Ordinal)
                && sqlState is not ("57P01" or "57P02" or "57P03" or "53300") =>
                new DatabaseProviderException(DatabaseFailureKind.Unexpected, "The server returned an unexpected error. Details are in the agent log.", exception),
            _ when IsTlsFailure(exception) =>
                new DatabaseProviderException(DatabaseFailureKind.InvalidConfiguration, "A TLS connection could not be established: the server has no TLS or its certificate is not trusted. Change the encryption settings or fix the server certificate.", exception),
            _ when exception is NpgsqlException or SocketException or IOException or System.Security.Authentication.AuthenticationException
                    || exception.InnerException is SocketException or IOException =>
                new DatabaseProviderException(DatabaseFailureKind.Unreachable, "The server could not be reached or the connection was lost.", exception),
            _ => new DatabaseProviderException(DatabaseFailureKind.Unexpected, "The server returned an unexpected error. Details are in the agent log.", exception)
        };
    }

    [LoggerMessage(EventId = 310, Level = LogLevel.Warning, Message = "PostgreSQL operation failed: Phase={Phase} Exception={ExceptionType} SqlState={SqlState}")]
    private static partial void LogFailure(ILogger logger, string exceptionType, string sqlState, string phase);
}
