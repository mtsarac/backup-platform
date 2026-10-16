using System.Net;
using System.Net.Sockets;
using BackupPlatform.Agent.Core.Discovery;
using BackupPlatform.Agent.Transport;
using BackupPlatform.Contracts.Discovery;

namespace BackupPlatform.AgentTests;

/// <summary>The probe against a throw-away listener on an ephemeral loopback port: no real database and no fixed port is involved.</summary>
public sealed class LoopbackListenerProbeTests
{
    private static readonly LoopbackListenerProbe Probe = new(TimeSpan.FromSeconds(5));

    private static async Task<ListenerProbeResult> ProbeAsync(string engine, Func<NetworkStream, Task> serve)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync();
            await serve(peer.GetStream());
        });
        var result = await Probe.ProbeAsync(engine, port, default);
        await server.WaitAsync(TimeSpan.FromSeconds(10));
        return result;
    }

    [Fact]
    public async Task A_closed_port_is_not_connected()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await Probe.ProbeAsync(DiscoveryEngines.PostgreSql, port, default);

        Assert.False(result.Connected);
        Assert.False(result.Recognized);
    }

    [Theory]
    [InlineData((byte)'S')]
    [InlineData((byte)'N')]
    public async Task A_postgresql_server_answers_the_ssl_request_with_one_byte(byte answer)
    {
        byte[]? received = null;

        var result = await ProbeAsync(DiscoveryEngines.PostgreSql, async stream =>
        {
            received = new byte[8];
            await stream.ReadExactlyAsync(received);
            await stream.WriteAsync(new[] { answer });
        });

        Assert.True(result.Recognized);
        byte[] expected = [0x00, 0x00, 0x00, 0x08, 0x04, 0xD2, 0x16, 0x2F];
        Assert.Equal(expected, received);
    }

    [Fact]
    public async Task Another_protocol_on_the_port_is_connected_but_not_recognised()
    {
        var result = await ProbeAsync(DiscoveryEngines.PostgreSql, async stream =>
        {
            await stream.ReadExactlyAsync(new byte[8]);
            await stream.WriteAsync("HTTP/1.1 400 Bad Request\r\n"u8.ToArray());
        });

        Assert.True(result.Connected);
        Assert.False(result.Recognized);
    }

    [Fact]
    public async Task A_sql_server_answers_the_prelogin_with_its_version()
    {
        byte[]? request = null;
        byte[] response =
        [
            0x04, 0x01, 0x00, 0x1A, 0x00, 0x00, 0x01, 0x00,
            0x00, 0x00, 0x0B, 0x00, 0x06,
            0x01, 0x00, 0x11, 0x00, 0x01,
            0xFF,
            0x10, 0x00, 0x10, 0x27, 0x00, 0x00,
            0x00,
        ];

        var result = await ProbeAsync(DiscoveryEngines.MsSql, async stream =>
        {
            request = new byte[47];
            await stream.ReadExactlyAsync(request);
            await stream.WriteAsync(response);
        });

        Assert.True(result.Recognized);
        Assert.Equal("16.0.4135", result.Version);
        Assert.Equal(0x12, request![0]);
    }

    [Fact]
    public async Task A_peer_that_stays_silent_is_connected_but_not_recognised_within_the_timeout()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptTcpClientAsync();

        var result = await new LoopbackListenerProbe(TimeSpan.FromMilliseconds(300)).ProbeAsync(DiscoveryEngines.MsSql, port, default);
        using var peer = await accepted;

        Assert.True(result.Connected);
        Assert.False(result.Recognized);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Probe.ProbeAsync(DiscoveryEngines.PostgreSql, port, cts.Token));
    }
}
