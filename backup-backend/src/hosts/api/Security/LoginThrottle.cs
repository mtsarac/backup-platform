using System.Collections.Concurrent;

namespace BackupPlatform.Api.Security;

/// <summary>
/// Locks a login identifier after repeated failures so one account cannot be password-guessed. State is per API process
/// (shortcut: a multi-instance deployment multiplies the allowance by the instance count; move it to the database then).
/// A short lock keeps the cost of locking out someone else's account low.
/// </summary>
public sealed class LoginThrottle(TimeProvider time)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private const int MaxTracked = 10_000;

    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset Since)> entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The time left to wait, or null when the identifier may try.</summary>
    public TimeSpan? RetryAfter(string identifier)
    {
        if (!entries.TryGetValue(identifier, out var e)) return null;
        var left = e.Since + Window - time.GetUtcNow();
        if (left <= TimeSpan.Zero) { entries.TryRemove(identifier, out _); return null; }
        return e.Failures >= MaxFailures ? left : null;
    }

    public void Failed(string identifier)
    {
        var now = time.GetUtcNow();
        if (entries.Count >= MaxTracked) foreach (var (key, value) in entries) if (value.Since + Window <= now) entries.TryRemove(key, out _);
        if (entries.Count >= MaxTracked) return; // bounded memory under a flood of distinct identifiers
        entries.AddOrUpdate(identifier, (1, now), (_, e) => e.Since + Window <= now ? (1, now) : (e.Failures + 1, e.Since));
    }

    public void Succeeded(string identifier) => entries.TryRemove(identifier, out _);
}
