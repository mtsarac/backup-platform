using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.AgentTests.Support;

/// <summary>The connectivity service wired to fakes and a fake clock. Tests advance virtual time in small steps.</summary>
internal sealed class ConnectivityHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _run;
    private ILoggerFactory? _factory;

    public ConnectivityHarness(AgentConnectivityOptions? options = null, double random = 0.5)
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Options = options ?? new AgentConnectivityOptions();
        Random = random;
        Store = new FakeCredentialStore(TestIdentity.Create());
        Client = new FakeBackendClient(Time);
        Channel = new FakePushChannel(Time);
        Logs = new CaptureLoggerProvider();
        Capabilities = new TestCapabilitySource();
    }

    public FakeTimeProvider Time { get; }

    public AgentConnectivityOptions Options { get; }

    public double Random { get; set; }

    public FakeCredentialStore Store { get; }

    public FakeBackendClient Client { get; }

    public FakePushChannel Channel { get; }

    public CaptureLoggerProvider Logs { get; }

    public TestCapabilitySource Capabilities { get; }

    public AgentConnectivityService Service { get; private set; } = null!;

    public Task Run => _run ?? throw new InvalidOperationException("Not started.");

    public AgentConnectivitySnapshot Snapshot => Service.Current;

    public void Start()
    {
        _factory = Logs.CreateFactory();
        Service = new AgentConnectivityService(
            Store, Client, Channel,
            new AgentSoftwareInfo("1.2.3", "linux", "Test OS", "10.0.0"),
            Capabilities, Options,
            new AgentBackoff(Options.ReconnectBaseDelay, Options.ReconnectMaxDelay, () => Random),
            Time, _factory.CreateLogger<AgentConnectivityService>());
        _run = Service.RunAsync(_cts.Token);
    }

    public async Task StartAndSettleAsync()
    {
        Start();
        await SettleAsync();
    }

    /// <summary>Lets queued continuations run; the service has no real-time dependencies.</summary>
    public static async Task SettleAsync() => await Task.Delay(6);

    public async Task AdvanceAsync(TimeSpan duration, TimeSpan? step = null)
    {
        var size = step ?? TimeSpan.FromMilliseconds(500);
        var remaining = duration;
        while (remaining > TimeSpan.Zero)
        {
            var chunk = remaining < size ? remaining : size;
            Time.Advance(chunk);
            remaining -= chunk;
            await SettleAsync();
        }
        await SettleAsync();
    }

    public async Task AdvanceSecondsAsync(double seconds) => await AdvanceAsync(TimeSpan.FromSeconds(seconds));

    public async Task StopAsync()
    {
        await _cts.CancelAsync();
        await Run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_run is not null)
        {
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception)
            {
                // Assertions on failures live in the tests themselves.
            }
        }
        _cts.Dispose();
        _factory?.Dispose();
    }
}

internal sealed class TestCapabilitySource : ICapabilityRevisionSource
{
    private int _syncs;

    public string? Revision { get; set; }

    public Func<AgentLocalIdentity, CancellationToken, Task<bool>> OnSync { get; set; } = static (_, _) => Task.FromResult(true);

    public int SyncCount => Volatile.Read(ref _syncs);

    public string? GetRevision() => Revision;

    public Task<bool> SyncAsync(AgentLocalIdentity identity, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _syncs);
        return OnSync(identity, cancellationToken);
    }
}
