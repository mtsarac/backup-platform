using BackupPlatform.Agent.Core.Connectivity;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.AgentTests.Support;

/// <summary>Advances to timer deadlines only after every registered consumer reaches an actual suspension point.</summary>
internal sealed class DeterministicTimeProvider : FakeTimeProvider, IAgentLoopObserver
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Task?> _operations = [];
    private readonly HashSet<TrackedTimer> _timers = [];
    private TaskCompletionSource _changed = CreateSignal();
    private readonly AsyncLocal<string?> _executing = new();

    public DeterministicTimeProvider() : base(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) { }

    public Func<string, CancellationToken, Task>? OnAdmission { get; set; }

    Task IAgentLoopObserver.AdmittedAsync(string operation, CancellationToken cancellationToken) =>
        OnAdmission?.Invoke(operation, cancellationToken) ?? Task.CompletedTask;

    void IAgentLoopObserver.Started(string operation) => UpdateOperation(operation, null);
    void IAgentLoopObserver.Waiting(string operation, Task checkpoint) => UpdateOperation(operation, checkpoint);
    void IAgentLoopObserver.Resumed(string operation) => UpdateOperation(operation, null);

    void IAgentLoopObserver.Executing(string operation) => _executing.Value = operation;

    bool IAgentLoopObserver.Signal(string operation, Func<bool> signal)
    {
        lock (_gate)
        {
            _operations.TryGetValue(operation, out var previous);
            _operations[operation] = null;
            var accepted = signal();
            if (!accepted)
            {
                _operations[operation] = previous;
            }
            SignalChanged();
            return accepted;
        }
    }

    void IAgentLoopObserver.Completed(string operation)
    {
        lock (_gate)
        {
            if (operation is "heartbeat" or "push" or "poll")
            {
                // Their terminal handoff still resumes RunAsync (WhenAny, cancellation and shutdown).
                _operations[operation] = null;
            }
            else
            {
                _operations.Remove(operation);
                if (operation == "connectivity")
                {
                    _operations.Remove("heartbeat");
                    _operations.Remove("push");
                    _operations.Remove("poll");
                }
            }
            SignalChanged();
        }
    }

    private void UpdateOperation(string operation, Task? checkpoint)
    {
        lock (_gate)
        {
            _operations[operation] = checkpoint;
            SignalChanged();
        }
    }

    private static TaskCompletionSource CreateSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void SignalChanged()
    {
        var previous = _changed;
        _changed = CreateSignal();
        previous.TrySetResult();
    }

    public Task WaitForCheckpointAsync(string operation) =>
        WaitForCheckpointCoreAsync(operation).WaitAsync(TimeSpan.FromSeconds(10));

    private async Task WaitForCheckpointCoreAsync(string operation)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_operations.TryGetValue(operation, out var checkpoint) && checkpoint is { IsCompleted: false })
                {
                    return;
                }
                changed = _changed.Task;
            }
            await changed;
        }
    }

    public async Task DrainAsync()
    {
        try
        {
            await DrainCoreAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException exception)
        {
            string active;
            lock (_gate)
            {
                active = string.Join(", ", _operations.Where(pair => pair.Value is null || pair.Value.IsCompleted).Select(pair => pair.Key));
            }
            throw new TimeoutException($"Consumers did not reach a checkpoint: {active}.", exception);
        }
    }

    private async Task DrainCoreAsync()
    {
        while (true)
        {
            Task changed;
            bool timerDue;
            lock (_gate)
            {
                timerDue = _timers.Any(timer => timer.DueAt is { } due && due <= GetUtcNow());
                if (!timerDue && _operations.Values.All(checkpoint => checkpoint is { IsCompleted: false }))
                {
                    return;
                }
                changed = _changed.Task;
            }
            if (timerDue)
            {
                base.Advance(TimeSpan.Zero);
                continue;
            }
            await changed;
        }
    }

    public async Task AdvanceAsync(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        await DrainAsync();
        var target = GetUtcNow() + duration;
        while (GetUtcNow() < target)
        {
            DateTimeOffset next;
            lock (_gate)
            {
                next = _timers.Where(timer => timer.DueAt.HasValue).Select(timer => timer.DueAt!.Value).DefaultIfEmpty(target).Min();
            }
            if (next > target)
            {
                next = target;
            }
            base.Advance(next - GetUtcNow());
            await DrainAsync();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new TrackedTimer(this, callback, state, _executing.Value);
        timer.Inner = base.CreateTimer(timer.Fire, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class TrackedTimer(DeterministicTimeProvider owner, TimerCallback callback, object? state, string? operation) : ITimer
    {
        private TimeSpan _period;
        private bool _disposed;
        public ITimer Inner { get; set; } = null!;
        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (_disposed)
                {
                    return false;
                }
                _period = period;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;
                owner._timers.Add(this);
                var changed = Inner.Change(dueTime, period);
                owner.SignalChanged();
                return changed;
            }
        }

        public void Fire(object? unused)
        {
            lock (owner._gate)
            {
                DueAt = _period > TimeSpan.Zero ? owner.GetUtcNow() + _period : null;
                if (!_disposed && operation is not null && owner._operations.ContainsKey(operation))
                {
                    // Channel cancellation/wake continuations may still be queued while their Task reports pending.
                    // Keep the timer's exact consumer active until it acknowledges the callback by resuming/rearming.
                    owner._operations[operation] = null;
                }
                owner.SignalChanged();
            }
            callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                _disposed = true;
                DueAt = null;
                owner._timers.Remove(this);
                Inner.Dispose();
                owner.SignalChanged();
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
