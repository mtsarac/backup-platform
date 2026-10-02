using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using BackupPlatform.Contracts.Agents;
using Microsoft.AspNetCore.RateLimiting;

namespace BackupPlatform.Api.Security;

/// <summary>
/// Limits for the agent surface. Defaults leave wide headroom over normal behaviour (a 30 s heartbeat is 2 per window
/// against 12 permitted; a 30 s fallback poll is 2 against 20) so retries, reconnect bursts and restarts are not
/// throttled, while a runaway or hostile client is cut off within seconds. Rate limiting is one layer only: every
/// request is still authenticated and authorized independently.
/// </summary>
public sealed class AgentRateLimitOptions
{
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>Enrollment attempts per network client per window (a network prefix, so NAT-shared fleets need headroom).</summary>
    public int EnrollmentPerClient { get; init; } = 30;
    /// <summary>Enrollment attempts per token identifier (the public half of a token) per window, valid or not.</summary>
    public int EnrollmentPerToken { get; init; } = 5;
    /// <summary>Failed agent authentications per network client per window before further credentialed requests are refused.</summary>
    public int AuthenticationFailuresPerClient { get; init; } = 30;
    public int HeartbeatPerAgent { get; init; } = 12;
    public int PollPerAgent { get; init; } = 20;
    /// <summary>Push channel connection attempts (negotiate and upgrade count separately) per agent per window.</summary>
    public int ConnectPerAgent { get; init; } = 20;

    public static class Policies
    {
        public const string Enrollment = "agent-enrollment";
        public const string Heartbeat = "agent-heartbeat";
        public const string Poll = "agent-poll";
        public const string Connect = "agent-connect";
    }
}

/// <summary>
/// Network-client key for limiting. Never derived from credentials or tokens, so a limiter key cannot leak a secret.
/// IPv6 clients are grouped by /64 because a single host controls the whole prefix.
/// </summary>
public static class AgentClientKey
{
    public static string From(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }
        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes).ToString() + "/64";
    }
}

/// <summary>
/// Counts failed agent authentications per network client and reports when a client has exhausted its budget, so
/// further credentialed requests from it are refused before any database work. A hostile client on a shared address
/// can therefore briefly delay agents behind the same address; the budget is generous to keep that rare.
/// </summary>
public sealed class AgentAuthFailureLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;

    public AgentAuthFailureLimiter(AgentRateLimitOptions options)
    {
        RetryAfter = options.Window;
        _limiter = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetSlidingWindowLimiter(key, _ =>
            new SlidingWindowRateLimiterOptions
            {
                PermitLimit = options.AuthenticationFailuresPerClient,
                Window = options.Window,
                SegmentsPerWindow = 4,
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    }

    public TimeSpan RetryAfter { get; }

    public bool IsBlocked(string clientKey) => _limiter.GetStatistics(clientKey)?.CurrentAvailablePermits <= 0;

    public void RecordFailure(string clientKey) => _limiter.AttemptAcquire(clientKey).Dispose();

    public void Dispose() => _limiter.Dispose();
}

/// <summary>Limits enrollment attempts per token identifier, so one token cannot be ground against from many addresses.</summary>
public sealed class EnrollmentTokenThrottle : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> _limiter;

    public EnrollmentTokenThrottle(AgentRateLimitOptions options)
    {
        RetryAfter = options.Window;
        _limiter = PartitionedRateLimiter.Create<Guid, Guid>(key => RateLimitPartition.GetSlidingWindowLimiter(key, _ =>
            new SlidingWindowRateLimiterOptions
            {
                PermitLimit = options.EnrollmentPerToken,
                Window = options.Window,
                SegmentsPerWindow = 4,
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    }

    public TimeSpan RetryAfter { get; }

    public bool TryAcquire(Guid tokenId)
    {
        using var lease = _limiter.AttemptAcquire(tokenId);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}

public static class AgentRateLimiting
{
    public static IServiceCollection AddAgentRateLimiting(this IServiceCollection services)
    {
        services.AddSingleton(new AgentRateLimitOptions());
        services.AddSingleton<AgentAuthFailureLimiter>();
        services.AddSingleton<EnrollmentTokenThrottle>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(AgentRateLimitOptions.Policies.Enrollment, context =>
            {
                var limits = context.RequestServices.GetRequiredService<AgentRateLimitOptions>();
                return RateLimitPartition.GetSlidingWindowLimiter(AgentClientKey.From(context.Connection.RemoteIpAddress),
                    _ => Window(limits, limits.EnrollmentPerClient));
            });
            AddPerAgentPolicy(options, AgentRateLimitOptions.Policies.Heartbeat, limits => limits.HeartbeatPerAgent);
            AddPerAgentPolicy(options, AgentRateLimitOptions.Policies.Poll, limits => limits.PollPerAgent);
            AddPerAgentPolicy(options, AgentRateLimitOptions.Policies.Connect, limits => limits.ConnectPerAgent);
            options.OnRejected = async (context, cancellationToken) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : TimeSpan.FromSeconds(1);
                await WriteRateLimitedAsync(context.HttpContext, retryAfter, cancellationToken);
            };
        });
        return services;
    }

    /// <summary>Writes the uniform 429 response with a whole-second, at least one second, Retry-After.</summary>
    public static async Task WriteRateLimitedAsync(HttpContext context, TimeSpan retryAfter, CancellationToken cancellationToken)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await context.Response.WriteAsJsonAsync(new AgentApiError
        {
            Error = AgentErrorCodes.RateLimited,
            Action = AgentErrorActions.RetryLater,
            RetryAfterSeconds = seconds
        }, cancellationToken);
    }

    /// <summary>Partitions by the authenticated agent identifier; runs after authorization, so the key is never a credential.</summary>
    private static void AddPerAgentPolicy(RateLimiterOptions options, string name, Func<AgentRateLimitOptions, int> permits) =>
        options.AddPolicy(name, context =>
        {
            var limits = context.RequestServices.GetRequiredService<AgentRateLimitOptions>();
            var key = AgentAuthentication.TryGetIdentity(context.User) is { } identity
                ? "agent:" + identity.AgentId.Value.ToString("N")
                : "client:" + AgentClientKey.From(context.Connection.RemoteIpAddress);
            return RateLimitPartition.GetSlidingWindowLimiter(key, _ => Window(limits, permits(limits)));
        });

    private static SlidingWindowRateLimiterOptions Window(AgentRateLimitOptions limits, int permits) => new()
    {
        PermitLimit = permits,
        Window = limits.Window,
        SegmentsPerWindow = 4,
        QueueLimit = 0,
        AutoReplenishment = true
    };
}
