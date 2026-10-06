using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Contracts.Agents;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Core.Connectivity;

/// <summary>
/// Keeps the agent reachable: a heartbeat loop that runs independently of the channel, a push channel that is
/// reconnected in the background, and polling that also finds durable tasks when push hints are lost.
/// </summary>
/// <remarks>
/// Three loops cooperate through small shared state, each deciding from the same facts: the heartbeat loop
/// (<c>Heartbeat</c>), the push connection loop (<c>Push</c>) and the poll scheduler (<c>Poll</c>). A shared
/// <em>restriction</em> (credential rejected, protocol window violated) pauses all three until a slow retry time, after
/// which each probes again, reloading the identity from the store first when the credential was rejected. The agent
/// never reports itself online and never uses its clock for anything the server decides; it only measures local delays.
/// </remarks>
public sealed partial class AgentConnectivityService : IAgentConnectivityStatus
{
    /// <summary>Upper bound for waiting on the channel to stop when shutting down.</summary>
    internal static readonly TimeSpan ShutdownGracePeriod = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromHours(1);

    private readonly IAgentCredentialStore _store;
    private readonly IAgentBackendClient _client;
    private readonly IAgentTaskSink? _taskSink;
    private readonly IAgentPushChannel _channel;
    private readonly AgentSoftwareInfo _software;
    private readonly ICapabilityRevisionSource _capabilities;
    private readonly AgentConnectivityOptions _options;
    private readonly AgentBackoff _backoff;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentConnectivityService> _logger;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _identityGate = new(1, 1);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private AgentConnectivitySnapshot _snapshot;
    private bool _running;
    private int _started;
    private AgentLocalIdentity? _identity;
    private string _backendHost;

    // Shared between the loops; guarded by _gate.
    private bool _pushUp;
    private long _pushDownSince;
    private bool _catchUpRequested;
    private bool _hintRequested;
    private Restriction? _restriction;
    private long _restrictionSequence;
    private TimeSpan _effectiveHeartbeatInterval;
    private int _capabilitySyncRunning;
    private Task? _capabilitySync;

    public AgentConnectivityService(
        IAgentCredentialStore store,
        IAgentBackendClient client,
        IAgentPushChannel channel,
        AgentSoftwareInfo software,
        ICapabilityRevisionSource capabilities,
        AgentConnectivityOptions options,
        AgentBackoff backoff,
        TimeProvider timeProvider,
        ILogger<AgentConnectivityService> logger,
        IAgentTaskSink? taskSink = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(software);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backoff);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        var problems = options.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException("Invalid connectivity options: " + string.Join(" ", problems), nameof(options));
        }
        _store = store;
        _client = client;
        _taskSink = taskSink;
        _channel = channel;
        _software = software;
        _capabilities = capabilities;
        _options = options;
        _backoff = backoff;
        _time = timeProvider;
        _logger = logger;
        _backendHost = client.BackendBaseUri.Host;
        _effectiveHeartbeatInterval = options.HeartbeatInterval;
        _snapshot = new AgentConnectivitySnapshot { EffectiveHeartbeatInterval = options.HeartbeatInterval };
    }

    public AgentConnectivitySnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public event Action<AgentConnectivitySnapshot>? Changed;

    /// <summary>
    /// Runs until <paramref name="stoppingToken"/> is cancelled, then stops every loop, stops the push channel and returns
    /// normally. Throws <see cref="AgentIdentityUnavailableException"/> at startup when no usable identity is stored.
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The connectivity service can only run once.");
        }
        try
        {
            _identity = await LoadInitialIdentityAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        lock (_gate)
        {
            _running = true;
            _pushUp = false;
            _pushDownSince = _time.GetTimestamp();
        }
        Update(static s => s);
        LogStarted(_identity.AgentId, _backendHost, AgentProtocol.Current.Version.ToString(),
            _options.HeartbeatInterval, _options.PollInterval, _options.PushFallbackDelay);

        using var loops = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var tasks = new[]
        {
            Task.Run(() => HeartbeatLoopAsync(loops.Token), CancellationToken.None),
            Task.Run(() => PushLoopAsync(loops.Token), CancellationToken.None),
            Task.Run(() => PollLoopAsync(loops.Token), CancellationToken.None),
        };
        try
        {
            await Task.WhenAny(tasks).ConfigureAwait(false);
            await loops.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(tasks).ContinueWith(static _ => { }, TaskScheduler.Default).ConfigureAwait(false);
        }
        finally
        {
            await ShutdownAsync().ConfigureAwait(false);
        }
        foreach (var task in tasks)
        {
            if (task.IsFaulted)
            {
                ExceptionDispatchInfo.Capture(task.Exception!.GetBaseException()).Throw();
            }
        }
    }

    private async Task<AgentLocalIdentity> LoadInitialIdentityAsync(CancellationToken cancellationToken)
    {
        AgentLocalIdentity? identity;
        try
        {
            identity = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AgentCredentialStoreException exception)
        {
            throw new AgentIdentityUnavailableException($"The stored agent identity cannot be used: {exception.Message}", exception);
        }
        return identity ?? throw new AgentIdentityUnavailableException(
            "No agent identity is stored on this machine. Enroll the agent with the backend before starting the service.");
    }

    private async Task ShutdownAsync()
    {
        using var grace = new CancellationTokenSource(ShutdownGracePeriod, _time);
        try
        {
            await _channel.StopAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Bounded: the channel is released by its disposal if it does not stop in time.
        }
        catch (Exception exception)
        {
            LogUnexpected("push channel shutdown", exception.GetType().Name);
        }
        if (_capabilitySync is { } capabilitySync)
        {
            try
            {
                await capabilitySync.WaitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Bounded: the sync observes the cancelled loop token and ends on its own.
            }
        }
        lock (_gate)
        {
            _running = false;
            _pushUp = false;
        }
        Update(static s => s with { PollingActive = false });
        LogStopped(_identity?.AgentId ?? Guid.Empty);
    }

    // ---- shared state ------------------------------------------------------------------------------------------

    private sealed record Restriction(AgentChannelState Kind, long RetryAt, long Sequence);

    /// <summary>Marks the start of a backend attempt; only a success of an attempt that began after a restriction was set may lift it.</summary>
    private long BeginAttempt()
    {
        lock (_gate)
        {
            return _restrictionSequence;
        }
    }

    private long Now => _time.GetTimestamp();

    private long After(long timestamp, TimeSpan delay) => timestamp + (long)(delay.TotalSeconds * _time.TimestampFrequency);

    private TimeSpan Until(long deadline) => _time.GetElapsedTime(Now, deadline);

    private static TimeSpan? ClampRetryAfter(TimeSpan? retryAfter) =>
        retryAfter is { } value ? TimeSpan.FromTicks(Math.Clamp(value.Ticks, 0, MaximumRetryAfter.Ticks)) : null;

    private void SignalWake() => _wake.Writer.TryWrite(true);

    private async Task WaitForWakeAsync(TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (timeout is { } limit && limit <= TimeSpan.Zero)
        {
            return;
        }
        using var timeoutSource = timeout is { } span ? new CancellationTokenSource(span, _time) : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource?.Token ?? CancellationToken.None);
        try
        {
            await _wake.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false);
            _wake.Reader.TryRead(out _);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The timeout elapsed.
        }
    }

    private void Update(Func<AgentConnectivitySnapshot, AgentConnectivitySnapshot> change)
    {
        AgentConnectivitySnapshot next;
        lock (_gate)
        {
            var candidate = change(_snapshot);
            var state = !_running ? AgentChannelState.Stopped
                : _restriction is { } restriction ? restriction.Kind
                : _pushUp ? AgentChannelState.Connected
                : AgentChannelState.Connecting;
            next = candidate with { State = state, EffectiveHeartbeatInterval = _effectiveHeartbeatInterval };
            if (next == _snapshot)
            {
                return;
            }
            _snapshot = next;
        }
        try
        {
            Changed?.Invoke(next);
        }
        catch (Exception exception)
        {
            LogUnexpected("status listener", exception.GetType().Name);
        }
    }

    /// <summary>Pauses heartbeat, push and poll attempts until the slow retry time; <paramref name="kind"/> is Unauthorized or ProtocolIncompatible.</summary>
    private void SetRestriction(AgentChannelState kind, string? action, string reason)
    {
        var delay = _backoff.AddJitter(_options.ReconnectMaxDelay);
        lock (_gate)
        {
            _restriction = new Restriction(kind, After(Now, delay), ++_restrictionSequence);
        }
        Update(static s => s);
        SignalWake();
        LogRestricted(_identity?.AgentId ?? Guid.Empty, kind, action ?? "none", reason, delay, _backendHost,
            AgentProtocol.Current.Version.ToString());
    }

    private void ClearRestriction(long attemptSequence)
    {
        Restriction? cleared = null;
        lock (_gate)
        {
            if (_restriction is { } current && current.Sequence <= attemptSequence)
            {
                cleared = current;
                _restriction = null;
            }
        }
        if (cleared is null)
        {
            return;
        }
        Update(static s => s);
        SignalWake();
        LogRecovered(_identity?.AgentId ?? Guid.Empty, cleared.Kind);
    }

    /// <summary>
    /// Waits until no restriction applies or the retry time of the restriction in force is reached, then refreshes the
    /// identity if the credential was rejected. The retry time observed on entry is honoured even when a sibling loop's
    /// failed probe replaces the restriction meanwhile, so every loop probes once per window and none is starved.
    /// </summary>
    private async Task WaitForRestrictionAsync(CancellationToken cancellationToken)
    {
        var restriction = Volatile.Read(ref _restriction);
        if (restriction is null)
        {
            return;
        }
        var remaining = Until(restriction.RetryAt);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, _time, cancellationToken).ConfigureAwait(false);
        }
        await RefreshIdentityIfRejectedAsync(restriction, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshIdentityIfRejectedAsync(Restriction restriction, CancellationToken cancellationToken)
    {
        if (restriction.Kind != AgentChannelState.Unauthorized)
        {
            return;
        }
        await _identityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = _identity;
            var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (loaded is null)
            {
                LogIdentityReloadFailed(previous?.AgentId ?? Guid.Empty, "no identity is stored");
                return;
            }
            _identity = loaded;
            LogIdentityReloaded(loaded.AgentId, loaded.Current.Generation);
        }
        catch (AgentCredentialStoreException exception)
        {
            LogIdentityReloadFailed(_identity?.AgentId ?? Guid.Empty, exception.Message);
        }
        finally
        {
            _identityGate.Release();
        }
    }

    /// <summary>Runs a backend call, turning anything but cancellation into a transient failure so a faulty transport cannot kill the loops.</summary>
    private async Task<AgentBackendResult<T>> GuardAsync<T>(string operation, Func<Task<AgentBackendResult<T>>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogUnexpected(operation, exception.GetType().Name);
            return AgentBackendResult<T>.Fail(new AgentBackendFailure(AgentBackendFailureKind.Transient, $"unexpected {exception.GetType().Name}"));
        }
    }

    private AgentLocalIdentity Identity => _identity ?? throw new InvalidOperationException("The identity is not loaded.");
}
