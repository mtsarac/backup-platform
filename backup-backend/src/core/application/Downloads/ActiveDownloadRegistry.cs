using System.Collections.Concurrent;

namespace BackupPlatform.Application.Downloads;

/// <summary>Per-instance registry of active download streams keyed by session and share for immediate revocation cancellation.</summary>
public sealed class ActiveDownloadRegistry
{
    private sealed record Entry(Guid SessionId, Guid? ShareId, CancellationTokenSource RevocationCts);

    private readonly ConcurrentDictionary<Guid, Entry> _sessions = new();

    public int Count => _sessions.Count;

    public bool ContainsSession(Guid sessionId) => _sessions.ContainsKey(sessionId);

    public IDisposable Register(Guid sessionId, Guid? shareId, CancellationTokenSource revocationCts)
    {
        ArgumentNullException.ThrowIfNull(revocationCts);
        _sessions[sessionId] = new Entry(sessionId, shareId, revocationCts);
        return new Registration(this, sessionId);
    }

    public bool Unregister(Guid sessionId) => _sessions.TryRemove(sessionId, out _);

    public IReadOnlyCollection<Guid> GetActiveShareIds()
    {
        var shareIds = new HashSet<Guid>();
        foreach (var entry in _sessions.Values)
        {
            if (entry.ShareId is { } shareId)
            {
                shareIds.Add(shareId);
            }
        }
        return shareIds;
    }

    public int CancelShare(Guid shareId)
    {
        if (shareId == Guid.Empty) return 0;
        var cancelled = 0;
        foreach (var entry in _sessions.Values)
        {
            if (entry.ShareId != shareId) continue;
            try
            {
                entry.RevocationCts.Cancel();
                cancelled++;
            }
            catch (ObjectDisposedException)
            {
            }
        }
        return cancelled;
    }

    private sealed class Registration(ActiveDownloadRegistry registry, Guid sessionId) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                registry.Unregister(sessionId);
            }
        }
    }
}

public sealed class ShareRevocationOptions
{
    public const string SectionName = "Downloads:ShareRevocation";

    public bool EnableListenNotify { get; init; } = true;

    public TimeSpan SignalledBound { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan ReconciliationInterval { get; init; } = TimeSpan.FromSeconds(2);

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (SignalledBound < TimeSpan.FromMilliseconds(50) || SignalledBound > TimeSpan.FromMinutes(5))
            problems.Add("SignalledBound must be between 50 milliseconds and 5 minutes.");
        if (ReconciliationInterval < TimeSpan.FromMilliseconds(50) || ReconciliationInterval > TimeSpan.FromMinutes(5))
            problems.Add("ReconciliationInterval must be between 50 milliseconds and 5 minutes.");
        return problems;
    }

    public void ThrowIfInvalid()
    {
        var problems = Validate();
        if (problems.Count != 0)
            throw new InvalidOperationException("Invalid share revocation options: " + string.Join(" ", problems));
    }
}
