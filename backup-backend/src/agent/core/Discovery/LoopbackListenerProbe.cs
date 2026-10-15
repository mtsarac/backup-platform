using System.Net;
using System.Net.Sockets;
using BackupPlatform.Contracts.Discovery;

namespace BackupPlatform.Agent.Core.Discovery;

/// <param name="Connected">The TCP connection was accepted.</param>
/// <param name="Recognized">The peer answered the engine's credential-free handshake like that engine.</param>
/// <param name="Version">Server version from the handshake when the protocol carries one (SQL Server only).</param>
public sealed record ListenerProbeResult(bool Connected, bool Recognized, string? Version);

/// <summary>Asks whether a database engine answers on a loopback port. Never throws except for cancellation.</summary>
public interface IListenerProbe
{
    Task<ListenerProbeResult> ProbeAsync(string engine, int port, CancellationToken cancellationToken);
}

/// <summary>
/// The credential-free handshake of an engine on <c>127.0.0.1</c> only (never another address, never a range): SQL Server answers a
/// TDS PRELOGIN with its version, PostgreSQL answers an SSLRequest with <c>S</c> or <c>N</c>. No login is attempted, so the
/// server's audit trail shows nothing but a connection.
/// </summary>
public sealed class LoopbackListenerProbe : IListenerProbe
{
    private const int MaximumResponseBytes = 4096;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _timeout;

    public LoopbackListenerProbe(TimeSpan? timeout = null) => _timeout = timeout ?? DefaultTimeout;

    public async Task<ListenerProbeResult> ProbeAsync(string engine, int port, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_timeout);
        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, limit.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when ((exception is SocketException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            return new ListenerProbeResult(false, false, null);
        }

        try
        {
            var stream = client.GetStream();
            return engine switch
            {
                DiscoveryEngines.MsSql => await ProbeMsSqlAsync(stream, limit.Token).ConfigureAwait(false),
                DiscoveryEngines.PostgreSql => await ProbePostgreSqlAsync(stream, limit.Token).ConfigureAwait(false),
                _ => new ListenerProbeResult(true, false, null),
            };
        }
        catch (Exception exception) when ((exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            && !cancellationToken.IsCancellationRequested)
        {
            return new ListenerProbeResult(true, false, null);
        }
    }

    private static async Task<ListenerProbeResult> ProbeMsSqlAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(DiscoveryParsers.BuildMsSqlPreLogin(), cancellationToken).ConfigureAwait(false);
        var response = new byte[MaximumResponseBytes];
        var header = await stream.ReadAtLeastAsync(response.AsMemory(0, 8), 8, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (header < 8)
        {
            return new ListenerProbeResult(true, false, null);
        }
        var length = Math.Min((response[2] << 8) | response[3], MaximumResponseBytes);
        var total = header;
        if (length > 8)
        {
            total += await stream.ReadAtLeastAsync(response.AsMemory(8, length - 8), length - 8, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        }
        return DiscoveryParsers.TryParsePreLoginResponse(response.AsSpan(0, total), out var version)
            ? new ListenerProbeResult(true, true, version)
            : new ListenerProbeResult(true, false, null);
    }

    private static async Task<ListenerProbeResult> ProbePostgreSqlAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(DiscoveryParsers.PostgreSqlSslRequest.ToArray(), cancellationToken).ConfigureAwait(false);
        var answer = new byte[1];
        return await stream.ReadAtLeastAsync(answer, 1, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false) == 1
            && DiscoveryParsers.IsPostgreSqlSslAnswer(answer[0])
            ? new ListenerProbeResult(true, true, null)
            : new ListenerProbeResult(true, false, null);
    }
}
