using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using BackupPlatform.Api.Security;
using BackupPlatform.Notifications;

namespace BackupPlatform.Api.Notifications;

/// <summary>
/// Limits for the anonymous recipient endpoints, shaped so that a genuine mail provider's one-click unsubscribe can never be refused:
/// <list type="bullet">
/// <item>valid tokens are limited per TOKEN (a provider sends one request per token; a replayer is held to a few per window) and never by client address, so
/// nobody else's traffic, including a flood from behind the same reverse proxy address, can starve them;</item>
/// <item>requests with a missing, forged or expired token are limited per network client, counting only those invalid requests.</item>
/// </list>
/// Keys are never the token itself: a limiter key could otherwise end up in a diagnostic.
/// </summary>
public sealed class RecipientLinkThrottle : IDisposable
{
    private readonly PartitionedRateLimiter<string> _invalid;
    private readonly PartitionedRateLimiter<string> _perToken;

    public RecipientLinkThrottle(NotificationOptions options)
    {
        RetryAfter = options.RecipientLinkWindow;
        _invalid = Create(options.RecipientLinkInvalidPerClient, options.RecipientLinkWindow);
        _perToken = Create(options.RecipientLinkAttemptsPerToken, options.RecipientLinkWindow);
    }

    public TimeSpan RetryAfter { get; }

    public static string ClientKey(HttpContext context) => AgentClientKey.From(context.Connection.RemoteIpAddress);

    /// <summary>The client already spent its budget of invalid requests.</summary>
    public bool IsBlocked(string clientKey) => _invalid.GetStatistics(clientKey)?.CurrentAvailablePermits <= 0;

    public void RecordInvalid(string clientKey) => _invalid.AttemptAcquire(clientKey).Dispose();

    /// <summary>One more request with this valid token; false once the token used its share of the window.</summary>
    public bool TryAcquire(string token)
    {
        using var lease = _perToken.AttemptAcquire(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
        return lease.IsAcquired;
    }

    private static PartitionedRateLimiter<string> Create(int permits, TimeSpan window) =>
        PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            SegmentsPerWindow = 4,
            QueueLimit = 0,
            AutoReplenishment = true
        }));

    public void Dispose()
    {
        _invalid.Dispose();
        _perToken.Dispose();
    }
}
