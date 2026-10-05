using BackupPlatform.BackupProviders.Abstractions.Database;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.BackupProviders.MSSQL.Connectivity;

/// <summary>
/// One short-lived SQL Server connection. Every network operation honours the caller's token and a finite timeout, and every
/// engine exception leaves this type either as caller cancellation or as a <see cref="DatabaseProviderException"/> carrying a
/// user-safe message; the original exception is logged here and kept only as the inner exception.
/// </summary>
internal sealed partial class SqlServerSession : IAsyncDisposable
{
    // Backstop for hangs SqlClient's own connect timeout does not bound (for example name resolution); it fires just after the real one.
    private static readonly TimeSpan ConnectGuardGrace = TimeSpan.FromSeconds(5);

    private readonly SqlConnection _connection;
    private readonly int _commandTimeoutSeconds;
    private readonly ILogger _logger;

    private SqlServerSession(SqlConnection connection, int commandTimeoutSeconds, ILogger logger)
    {
        _connection = connection;
        _commandTimeoutSeconds = commandTimeoutSeconds;
        _logger = logger;
    }

    public static async Task<SqlServerSession> OpenAsync(
        DatabaseConnectionSettings settings,
        DatabaseCredential credential,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);
        cancellationToken.ThrowIfCancellationRequested();

        SqlConnection connection;
        try
        {
            var builder = SqlServerConnectionStringFactory.Create(settings, credential);
            connection = new SqlConnection(builder.ConnectionString);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or NotSupportedException)
        {
            throw Translate(logger, "connection-configuration", exception, SqlServerOperationPhase.Connect, timedOut: false);
        }

        using var guard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        guard.CancelAfter(settings.Options.ConnectTimeout + ConnectGuardGrace);
        try
        {
            await connection.OpenAsync(guard.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw Translate(logger, "connect", exception, SqlServerOperationPhase.Connect, timedOut: exception is OperationCanceledException);
        }

        return new SqlServerSession(connection, (int)settings.Options.CommandTimeout.TotalSeconds, logger);
    }

    /// <summary>Runs a parameterized query and maps every row. <paramref name="sql"/> must be a constant: values go through <paramref name="bind"/>.</summary>
    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        string operation,
        string sql,
        Action<SqlParameterCollection>? bind,
        Func<SqlDataReader, T> map,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = _commandTimeoutSeconds;
            bind?.Invoke(command.Parameters);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var rows = new List<T>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(map(reader));
            }

            return rows;
        }
        catch (Exception exception) when (exception is not DatabaseProviderException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Translate(_logger, operation, exception, SqlServerOperationPhase.Command, timedOut: false);
        }
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private static DatabaseProviderException Translate(
        ILogger logger,
        string operation,
        Exception exception,
        SqlServerOperationPhase phase,
        bool timedOut)
    {
        var (kind, message, number) = timedOut
            ? (DatabaseFailureKind.Timeout, SqlServerFailureClassifier.MessageFor(DatabaseFailureKind.Timeout), (int?)null)
            : SqlServerFailureClassifier.Classify(exception, phase);

        // The exception is an internal diagnostic: it is logged, and it stays reachable as InnerException for in-process callers,
        // but the message that travels onward is the fixed user-safe sentence.
        LogFailure(logger, exception, operation, kind, number);
        return new DatabaseProviderException(kind, message, exception);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "SQL Server operation failed: Operation={Operation} Kind={Kind} SqlErrorNumber={SqlErrorNumber}")]
    private static partial void LogFailure(ILogger logger, Exception exception, string operation, DatabaseFailureKind kind, int? sqlErrorNumber);
}
