using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Api.Security;

/// <summary>
/// Locks a login identifier after repeated failures so one account cannot be password-guessed. The counters live in PostgreSQL so every API instance
/// enforces the same limit. A short lock keeps the cost of locking out someone else's account low.
/// </summary>
public sealed class LoginThrottle(DbContextOptions<PlatformDbContext> options, TimeProvider time)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private const int MaxTracked = 10_000;

    /// <summary>The time left to wait, or null when the identifier may try.</summary>
    public async Task<TimeSpan?> RetryAfterAsync(string identifier, CancellationToken cancellationToken)
    {
        if (Key(identifier) is not { } key) return null;
        var now = time.GetUtcNow();
        await using var db = PlatformDbContext.CreateForPlatformOperations(options);
        var since = await db.LoginThrottleEntries.AsNoTracking().Where(entry => entry.Identifier == key && entry.Failures >= MaxFailures && entry.WindowStart > now - Window)
            .Select(entry => (DateTimeOffset?)entry.WindowStart).SingleOrDefaultAsync(cancellationToken);
        return since is { } start ? start + Window - now : null;
    }

    public async Task FailedAsync(string identifier, CancellationToken cancellationToken)
    {
        if (Key(identifier) is not { } key) return;
        var now = time.GetUtcNow();
        var expired = now - Window;
        await using var db = PlatformDbContext.CreateForPlatformOperations(options);
        await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM login_throttle_entries WHERE "WindowStart" <= {expired}""", cancellationToken);
        // An identifier already tracked always counts; a new one is dropped once the table is full, so a flood of distinct identifiers cannot grow it without bound.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO login_throttle_entries ("Identifier", "Failures", "WindowStart")
            SELECT {key}, 1, {now}
            WHERE EXISTS (SELECT 1 FROM login_throttle_entries WHERE "Identifier" = {key})
               OR (SELECT count(*) FROM (SELECT 1 FROM login_throttle_entries LIMIT {MaxTracked}) tracked) < {MaxTracked}
            ON CONFLICT ("Identifier") DO UPDATE SET
                "Failures" = CASE WHEN login_throttle_entries."WindowStart" <= {expired} THEN 1 ELSE login_throttle_entries."Failures" + 1 END,
                "WindowStart" = CASE WHEN login_throttle_entries."WindowStart" <= {expired} THEN {now} ELSE login_throttle_entries."WindowStart" END
            """, cancellationToken);
    }

    public async Task SucceededAsync(string identifier, CancellationToken cancellationToken)
    {
        if (Key(identifier) is not { } key) return;
        await using var db = PlatformDbContext.CreateForPlatformOperations(options);
        await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM login_throttle_entries WHERE "Identifier" = {key}""", cancellationToken);
    }

    // The login's own normal form, so padding or case cannot dodge the counter. An identifier no account can have is never counted: it cannot be guessed against, and PostgreSQL text cannot store a NUL.
    private static string? Key(string? identifier) => LoginIdentifierNormalizer.TryNormalize(identifier, out var key) ? key : null;
}
