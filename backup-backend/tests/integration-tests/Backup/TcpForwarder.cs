using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace BackupPlatform.IntegrationTests.Backup;

/// <summary>
/// Loopback TCP forwarder with a controllable outage: while stopped its port is closed (connection refused), once started it pipes bytes
/// to the real target. Lets a smoke test make the real agent hit a genuine, transient connectivity failure without faking the agent.
/// </summary>
internal sealed class TcpForwarder : IAsyncDisposable
{
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly ConcurrentDictionary<TcpClient, byte> _connections = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task _acceptLoop = Task.CompletedTask;

    public TcpForwarder(string targetHost, int targetPort)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        // Reserve a free ephemeral port, then release it: it stays closed until Start() re-binds exactly this port.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
    }

    public string Host => IPAddress.Loopback.ToString();
    public int Port { get; }
    public bool Running => _listener is not null;

    public void Start()
    {
        if (_listener is not null) throw new InvalidOperationException("Forwarder is already running.");
        TcpListener? listener = null;
        for (var attempt = 0; listener is null; attempt++)
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start();
            }
            catch (SocketException) when (attempt < 20)
            {
                listener?.Stop();
                listener = null;
                Thread.Sleep(100); // The OS may briefly keep the just-released port; nothing else can be awaited here.
            }
        }
        _listener = listener;
        _cancellation = new CancellationTokenSource();
        _acceptLoop = AcceptAsync(listener, _cancellation.Token);
    }

    public async Task StopAsync()
    {
        if (_listener is not { } listener) return;
        _listener = null;
        await _cancellation!.CancelAsync();
        listener.Stop();
        foreach (var connection in _connections.Keys) connection.Dispose();
        try { await _acceptLoop; }
        catch (OperationCanceledException) { }
        _cancellation.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task AcceptAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        var pipes = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                pipes.Add(PipeAsync(client, cancellationToken));
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException) { }
        finally
        {
            foreach (var connection in _connections.Keys) connection.Dispose();
            await Task.WhenAll(pipes);
        }
    }

    private async Task PipeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var upstream = new TcpClient();
        _connections[client] = 0;
        _connections[upstream] = 0;
        Task[] copies = [];
        try
        {
            await upstream.ConnectAsync(_targetHost, _targetPort, cancellationToken);
            var left = client.GetStream();
            var right = upstream.GetStream();
            copies = [left.CopyToAsync(right, cancellationToken), right.CopyToAsync(left, cancellationToken)];
            // First direction to finish (EOF or reset) tears down both sockets, so neither side lingers after Stop().
            await Task.WhenAny(copies);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        finally
        {
            _connections.TryRemove(client, out _);
            _connections.TryRemove(upstream, out _);
            upstream.Dispose();
            client.Dispose();
            try { await Task.WhenAll(copies); }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        }
    }
}
