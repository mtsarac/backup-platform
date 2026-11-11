using System.Net;
using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Api.Security;

public sealed class LoginThrottle(DbContextOptions<PlatformDbContext> options, TimeProvider time)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public async Task<TimeSpan?> RetryAfterAsync(string kind, string? account, string? ipAddress, CancellationToken cancellationToken)
    {
        ValidateKind(kind);
        var accountKey = AccountKey(account);
        var ipKey = IpKey(ipAddress);
        var now = time.GetUtcNow();
        await using var db = PlatformDbContext.CreateForPlatformOperations(options);
        var until = await db.LoginThrottleEntries.AsNoTracking()
            .Where(entry => entry.Kind == kind && entry.LockedUntil > now &&
                ((accountKey != null && entry.Scope == "account" && entry.Identifier == accountKey) ||
                 (ipKey != null && entry.Scope == "ip" && entry.Identifier == ipKey)))
            .MaxAsync(entry => (DateTimeOffset?)entry.LockedUntil, cancellationToken);
        return until is { } lockedUntil ? lockedUntil - now : null;
    }

    public async Task FailedAsync(string kind, string? account, string? ipAddress, CancellationToken cancellationToken)
    {
        ValidateKind(kind);
        var accountKey = AccountKey(account);
        var ipKey = IpKey(ipAddress);
        var now = time.GetUtcNow();
        var expired = now - Retention;
        await using var db = PlatformDbContext.CreateForPlatformOperations(options);
        await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM login_throttle_entries WHERE "LastFailureAt" <= {expired}""", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO login_throttle_entries ("Kind", "Scope", "Identifier", "Failures", "LastFailureAt", "LockedUntil")
            SELECT {kind}, keys.scope, keys.identifier, 1, {now}, {now}
            FROM (VALUES ('account', CAST({accountKey} AS text)), ('ip', CAST({ipKey} AS text))) AS keys(scope, identifier)
            WHERE keys.identifier IS NOT NULL
            ON CONFLICT ("Kind", "Scope", "Identifier") DO UPDATE SET
                "Failures" = CASE WHEN login_throttle_entries."LastFailureAt" <= {expired} THEN 1 ELSE LEAST(login_throttle_entries."Failures" + 1, 13) END,
                "LastFailureAt" = GREATEST(login_throttle_entries."LastFailureAt", {now}),
                "LockedUntil" = GREATEST(login_throttle_entries."LockedUntil", CASE
                    WHEN login_throttle_entries."LastFailureAt" <= {expired} OR login_throttle_entries."Failures" + 1 < {MaxFailures} THEN {now}
                    ELSE {now} + make_interval(secs => LEAST(900, 5 * power(2, LEAST(login_throttle_entries."Failures" + 1 - {MaxFailures}, 8))))
                END)
            """, cancellationToken);
    }

    public async Task SucceededAsync(string kind, string? account, string? ipAddress, CancellationToken cancellationToken)
    {
        ValidateKind(kind);
        if (AccountKey(account) is not { } key) return;
        await using var db = PlatformDbContext.CreateForPlatformOperations(options);
        await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM login_throttle_entries WHERE "Kind" = {kind} AND "Scope" = 'account' AND "Identifier" = {key}""", cancellationToken);
    }

    private static string? AccountKey(string? account)
    {
        try
        {
            return account?.Contains('@') == true
                ? LoginIdentifierNormalizer.NormalizeEmail(account)
                : LoginIdentifierNormalizer.TryNormalize(account, out var key) ? key : null;
        }
        catch (ArgumentException) { return null; }
    }

    private static string? IpKey(string? ipAddress) => IPAddress.TryParse(ipAddress, out var ip) ? ip.MapToIPv6().ToString() : null;

    private static void ValidateKind(string kind)
    {
        if (string.IsNullOrEmpty(kind) || kind.Length > 64 || kind.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
            throw new ArgumentException("Attempt kind must contain 1 to 64 lowercase ASCII letters, digits, '-' or '_'.", nameof(kind));
    }
}
