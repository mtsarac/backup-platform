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

    private static readonly TimeSpan CancelRepeatInterval = TimeSpan.FromMilliseconds(250);

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

    /// <summary>
    /// Runs one long statement (BACKUP, RESTORE VERIFYONLY, a probe) with its own finite <paramref name="timeout"/>. Unlike
    /// <see cref="QueryAsync{T}"/> the <see cref="SqlException"/> is NOT translated: the caller needs every error number of the
    /// failed statement to classify it, and logs the exception itself. <paramref name="onInfoMessage"/> receives the informational
    /// messages the server sends while the statement runs (for example <c>STATS</c> progress) on the executing thread; it is
    /// advisory and must not throw.
    /// <para>
    /// The statement runs synchronously on a dedicated thread, and cancelling <paramref name="cancellationToken"/> calls
    /// <see cref="SqlCommand.Cancel"/>, which makes SqlClient send an attention signal that aborts the statement on the server
    /// (it removes its partial backup file). The returned task completes only once the server has acknowledged, so no statement
    /// is left running behind a completed call. Why not <c>ExecuteNonQueryAsync(token)</c>: observed against SQL Server 2025 with
    /// SqlClient, an asynchronous BACKUP with <c>STATS</c> ignores <c>Cancel</c> (and a concurrent <c>Close</c>) once the server
    /// streams progress messages, so the statement ran to the end seconds to hours after the cancellation; the synchronous call
    /// stops within ~50 ms at any point of the same backup, with and without <c>STATS</c>.
    /// </para>
    /// </summary>
    public Task ExecuteAsync(
        string sql,
        TimeSpan timeout,
        Action<SqlInfoMessageEventArgs>? onInfoMessage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Factory.StartNew(
            () => ExecuteBlocking(sql, timeout, onInfoMessage, cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void ExecuteBlocking(string sql, TimeSpan timeout, Action<SqlInfoMessageEventArgs>? onInfoMessage, CancellationToken cancellationToken)
    {
        SqlInfoMessageEventHandler? handler = onInfoMessage is null ? null : (_, args) => onInfoMessage(args);
        if (handler is not null)
        {
            _connection.InfoMessage += handler;
        }

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = (int)Math.Max(1, Math.Ceiling(timeout.TotalSeconds));

        // Cancel() before the statement is in flight does nothing, so it is repeated until the statement has ended.
        using var finished = new ManualResetEventSlim();
        Task? canceller = null;
        using var registration = cancellationToken.Register(() =>
            canceller = Task.Run(async () =>
            {
                while (!finished.IsSet)
                {
                    command.Cancel();
                    await Task.Delay(CancelRepeatInterval).ConfigureAwait(false);
                }
            }));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.ExecuteNonQuery();
        }
        finally
        {
            finished.Set();
            registration.Dispose();
            canceller?.Wait();
            if (handler is not null)
            {
                _connection.InfoMessage -= handler;
            }
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
