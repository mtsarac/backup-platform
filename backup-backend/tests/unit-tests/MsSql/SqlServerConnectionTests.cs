using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL;
using BackupPlatform.BackupProviders.MSSQL.Connectivity;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.UnitTests.MsSql;

public sealed class SqlServerConnectionTests
{
    private const string Password = "Sup3r;Secret=Pa55!word";

    private static readonly PasswordDatabaseCredential SqlLogin = new("backup_operator", new DatabaseSecret(Password));

    [Fact]
    public void SqlAuthenticationUsesTcpAndSafeDefaults()
    {
        var builder = SqlServerConnectionStringFactory.Create(
            new DatabaseConnectionSettings(new DatabaseEndpoint("db.example", 14330)), SqlLogin);

        Assert.Equal("tcp:db.example,14330", builder.DataSource);
        Assert.Equal("backup_operator", builder.UserID);
        Assert.Equal(Password, builder.Password);
        Assert.False(builder.IntegratedSecurity);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, builder.Encrypt);
        Assert.False(builder.TrustServerCertificate);
        Assert.False(builder.Pooling);
        Assert.Equal(0, builder.ConnectRetryCount);
        Assert.Equal(15, builder.ConnectTimeout);
        Assert.Equal("master", builder.InitialCatalog);
    }

    [Fact]
    public void NamedInstanceAndDefaultInstanceAddressing()
    {
        Assert.Equal("tcp:host\\SQL2022", SqlServerConnectionStringFactory.Create(
            new DatabaseConnectionSettings(new DatabaseEndpoint("host", instanceName: "SQL2022")), SqlLogin).DataSource);
        Assert.Equal("tcp:host", SqlServerConnectionStringFactory.Create(
            new DatabaseConnectionSettings(new DatabaseEndpoint("host")), SqlLogin).DataSource);
    }

    [Fact]
    public void IntegratedCredentialCarriesNoUserOrPassword()
    {
        var builder = SqlServerConnectionStringFactory.Create(
            new DatabaseConnectionSettings(new DatabaseEndpoint("host")), IntegratedDatabaseCredential.Instance);

        Assert.True(builder.IntegratedSecurity);
        Assert.Equal(string.Empty, builder.UserID);
        Assert.Equal(string.Empty, builder.Password);
    }

    [Fact]
    public void ConfiguredOptionsReachTheConnection()
    {
        var options = new DatabaseConnectionOptions(
            connectTimeout: TimeSpan.FromSeconds(3), commandTimeout: TimeSpan.FromSeconds(7), requireEncryption: false, trustServerCertificate: true);
        var builder = SqlServerConnectionStringFactory.Create(new DatabaseConnectionSettings(new DatabaseEndpoint("host"), options), SqlLogin);

        Assert.Equal(3, builder.ConnectTimeout);
        Assert.Equal(SqlConnectionEncryptOption.Optional, builder.Encrypt);
        Assert.True(builder.TrustServerCertificate);
    }

    [Fact]
    public void ConnectionStringKeywordsInCredentialsAreDataNotSyntax()
    {
        var hostile = new PasswordDatabaseCredential("sa;Server=evil", new DatabaseSecret("x';Encrypt=False;TrustServerCertificate=True;Data Source=evil"));
        var builder = SqlServerConnectionStringFactory.Create(new DatabaseConnectionSettings(new DatabaseEndpoint("host")), hostile);

        var roundTripped = new SqlConnectionStringBuilder(builder.ConnectionString);

        Assert.Equal("tcp:host", roundTripped.DataSource);
        Assert.Equal(SqlConnectionEncryptOption.Mandatory, roundTripped.Encrypt);
        Assert.False(roundTripped.TrustServerCertificate);
        Assert.Equal("sa;Server=evil", roundTripped.UserID);
    }

    [Fact]
    public void SecretsNeverRenderThemselves()
    {
        var secret = new DatabaseSecret(Password);
        var settings = new DatabaseConnectionSettings(new DatabaseEndpoint("host", 1433));

        Assert.DoesNotContain(Password, secret.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, SqlLogin.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, settings.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, $"{secret} {SqlLogin} {settings}", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Connect", 18456, DatabaseFailureKind.AuthenticationFailed)]
    [InlineData("Connect", 4060, DatabaseFailureKind.AuthenticationFailed)]
    [InlineData("Connect", 18487, DatabaseFailureKind.AuthenticationFailed)]
    [InlineData("Connect", -2, DatabaseFailureKind.Timeout)]
    [InlineData("Connect", 229, DatabaseFailureKind.PermissionDenied)]
    [InlineData("Connect", 300, DatabaseFailureKind.PermissionDenied)]
    [InlineData("Connect", 53, DatabaseFailureKind.Unreachable)]
    [InlineData("Connect", 40, DatabaseFailureKind.Unreachable)]
    [InlineData("Command", 10054, DatabaseFailureKind.Unreachable)]
    [InlineData("Command", -1, DatabaseFailureKind.Unreachable)]
    [InlineData("Command", -2, DatabaseFailureKind.Timeout)]
    [InlineData("Command", 229, DatabaseFailureKind.PermissionDenied)]
    [InlineData("Command", 208, DatabaseFailureKind.Unexpected)] // query errors are not connectivity errors
    [InlineData("Command", 102, DatabaseFailureKind.Unexpected)]
    public void ErrorNumbersAreClassifiedPerPhase(string phaseName, int number, DatabaseFailureKind expected) =>
        Assert.Equal(expected, SqlServerFailureClassifier.ClassifyNumber(number, Enum.Parse<SqlServerOperationPhase>(phaseName)).Kind);

    [Fact]
    public void ClassifiedMessagesAreFixedAndNeverEchoExceptionText()
    {
        var (_, message, _) = SqlServerFailureClassifier.Classify(
            new IOException($"leaked {Password} and Server=secret-host"), SqlServerOperationPhase.Command);

        Assert.DoesNotContain(Password, message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-host", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreachableServerSurfacesAUserSafeFailureAndLeaksNothing()
    {
        var logs = new CapturingLogger<SqlServerBackupProvider>();
        var provider = new SqlServerBackupProvider(logs);
        var settings = new DatabaseConnectionSettings(new DatabaseEndpoint("127.0.0.1", ClosedPort()), new DatabaseConnectionOptions(TimeSpan.FromSeconds(3)));

        var failure = await Assert.ThrowsAsync<DatabaseProviderException>(
            () => provider.DiscoverAsync(settings, SqlLogin, CancellationToken.None));

        Assert.Equal(DatabaseFailureKind.Unreachable, failure.Kind);
        AssertNoLeak(failure.Message, logs.AllText, Password, "backup_operator");
        Assert.NotNull(failure.InnerException);
        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task PreCheckReportsAnUnreachableServerAsDataNotAnException()
    {
        var logs = new CapturingLogger<SqlServerBackupProvider>();
        var provider = new SqlServerBackupProvider(logs);
        var settings = new DatabaseConnectionSettings(new DatabaseEndpoint("127.0.0.1", ClosedPort()), new DatabaseConnectionOptions(TimeSpan.FromSeconds(3)));

        var report = await provider.PreCheckFullBackupAsync(settings, SqlLogin, new FullBackupPreCheckRequest("Sales"), CancellationToken.None);

        Assert.False(report.CanProceed);
        var reachable = Assert.Single(report.Items, item => item.CheckId == BackupPreCheckIds.ServerReachable);
        Assert.Equal(BackupPreCheckStatus.Failed, reachable.Status);
        Assert.Equal("server-unreachable", reachable.ReasonCode);
        Assert.DoesNotContain(report.Items, item => item.Status == BackupPreCheckStatus.Passed);
        AssertNoLeak(string.Join('\n', report.Items.Select(item => $"{item.CheckId}|{item.ReasonCode}|{item.Message}")), logs.AllText, Password, "backup_operator");
    }

    [Fact]
    public async Task AnAlreadyCancelledTokenNeverTouchesTheNetwork()
    {
        var provider = new SqlServerBackupProvider(new CapturingLogger<SqlServerBackupProvider>());
        var settings = new DatabaseConnectionSettings(new DatabaseEndpoint("127.0.0.1", ClosedPort()));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.DiscoverAsync(settings, SqlLogin, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.PreCheckFullBackupAsync(settings, SqlLogin, new FullBackupPreCheckRequest("Sales"), cancelled.Token));
    }

    [Fact]
    public async Task ASilentServerEndsWithATimeoutAtTheConfiguredConnectTimeout()
    {
        using var silent = new SilentServer();
        var provider = new SqlServerBackupProvider(new CapturingLogger<SqlServerBackupProvider>());
        var settings = new DatabaseConnectionSettings(new DatabaseEndpoint("127.0.0.1", silent.Port), new DatabaseConnectionOptions(TimeSpan.FromSeconds(1)));

        var stopwatch = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<DatabaseProviderException>(() => provider.DiscoverAsync(settings, SqlLogin, CancellationToken.None));
        stopwatch.Stop();

        Assert.Equal(DatabaseFailureKind.Timeout, failure.Kind);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(9));
    }

    [Fact]
    public async Task CancellingWhileTheServerIsSilentReturnsPromptly()
    {
        using var silent = new SilentServer();
        var provider = new SqlServerBackupProvider(new CapturingLogger<SqlServerBackupProvider>());
        var settings = new DatabaseConnectionSettings(new DatabaseEndpoint("127.0.0.1", silent.Port), new DatabaseConnectionOptions(TimeSpan.FromSeconds(60)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.DiscoverAsync(settings, SqlLogin, cancellation.Token));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Cancellation took {stopwatch.Elapsed}.");
    }

    private static void AssertNoLeak(string visible, string logs, params string[] secrets)
    {
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, visible, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
        }
    }

    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Accepts TCP connections and never answers, like a server that is up but wedged.</summary>
    private sealed class SilentServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentBag<TcpClient> _clients = [];
        private readonly CancellationTokenSource _stop = new();

        public SilentServer()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    _clients.Add(await _listener.AcceptTcpClientAsync(_stop.Token));
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            foreach (var client in _clients)
            {
                client.Dispose();
            }

            _stop.Dispose();
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Text)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Text)> Entries => _entries.ToArray();

        public string AllText => string.Join('\n', _entries.Select(entry => entry.Text));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, $"{formatter(state, exception)}\n{exception}"));
    }
}
