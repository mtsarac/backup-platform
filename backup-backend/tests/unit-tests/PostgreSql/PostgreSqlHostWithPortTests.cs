using System.Net;
using System.Net.Sockets;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.PostgreSql.Connectivity;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.UnitTests.PostgreSql;

/// <summary>A user may type the host as "localhost:5432". Every consumer of the target (Npgsql, libpq tools, the TLS probe) must then see the same host and port.</summary>
public sealed class PostgreSqlHostWithPortTests
{
    private static PostgreSqlConnectionTarget Target(string host, int? port = 5432, bool encrypt = true) =>
        PostgreSqlConnectionTarget.Create(new DatabaseConnectionSettings(new DatabaseEndpoint(host, port), new DatabaseConnectionOptions(connectTimeout: TimeSpan.FromSeconds(5), requireEncryption: encrypt)),
            new PasswordDatabaseCredential("operator", new DatabaseSecret("secret")));

    [Theory]
    [InlineData("localhost:5433", 5432, "localhost", 5433)]
    [InlineData("db.example.com:6543", null, "db.example.com", 6543)]
    [InlineData("localhost", 5432, "localhost", 5432)]
    [InlineData("localhost", null, "localhost", 5432)]
    [InlineData("::1", 5433, "::1", 5433)]
    public void A_port_written_after_the_host_is_split_off_once(string host, int? port, string expectedHost, int expectedPort)
    {
        var target = Target(host, port);

        Assert.Equal((expectedHost, expectedPort), (target.Host, target.Port));
        var npgsql = target.ToNpgsql("postgres");
        Assert.Equal((expectedHost, expectedPort), (npgsql.Host, npgsql.Port));
        Assert.Contains($"port={expectedPort} ", target.ToConninfo("postgres"));
        Assert.StartsWith($"host='{expectedHost}' ", target.ToConninfo("postgres"));
    }

    [Theory]
    [InlineData("localhost:")]
    [InlineData("localhost:abc")]
    [InlineData("localhost:0")]
    [InlineData("localhost:65536")]
    [InlineData(":5432")]
    public void A_host_that_is_not_a_name_with_an_optional_port_is_invalid_configuration(string host)
    {
        var error = Assert.Throws<DatabaseProviderException>(() => Target(host));
        Assert.Equal(DatabaseFailureKind.InvalidConfiguration, error.Kind);
    }

    /// <summary>A server that answers every encryption request with "no", like a stock PostgreSQL without a certificate.</summary>
    private static (TcpListener Listener, Task Serving) ServerWithoutTls()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        async Task Serve()
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch (Exception e) when (e is ObjectDisposedException or SocketException or InvalidOperationException) { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        var request = new byte[8];
                        try
                        {
                            while (await stream.ReadAtLeastAsync(request, request.Length, throwOnEndOfStream: false) == request.Length)
                                await stream.WriteAsync("N"u8.ToArray());
                        }
                        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
                    }
                });
            }
        }
        return (listener, Serve());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_server_without_tls_is_reported_as_such_however_the_port_is_written(bool portInHost)
    {
        var (listener, serving) = ServerWithoutTls();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var target = portInHost ? Target($"localhost:{port}", port: 1) : Target("localhost", port);

            var error = await Assert.ThrowsAsync<DatabaseProviderException>(() =>
                PostgreSqlSession.OpenAsync(target, "postgres", NullLogger.Instance, CancellationToken.None));

            Assert.Equal(DatabaseFailureKind.ServerTlsUnavailable, error.Kind);
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }
}
