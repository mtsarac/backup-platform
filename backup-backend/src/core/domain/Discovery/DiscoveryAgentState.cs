using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Discovery;

public enum DiscoveryScanState
{
    Idle = 0,
    Requested,
    Running
}

/// <summary>
/// Scan scheduling and last result of one agent. Every mutation happens under the row lock of the store, so several backend nodes cannot
/// start parallel scans of one agent: a claim succeeds only when no scan started within <see cref="RunningTimeout"/>.
/// </summary>
public sealed class DiscoveryAgentState : ITenantOwned
{
    public const int DefaultIntervalMinutes = 360;
    public const int MinimumIntervalMinutes = 15;
    public const int MaximumIntervalMinutes = 10_080;
    /// <summary>A claimed scan that has not reported within this time is considered dead and may be claimed again.</summary>
    public static readonly TimeSpan RunningTimeout = TimeSpan.FromMinutes(10);
    /// <summary>Manual requests closer together than this are refused.</summary>
    public static readonly TimeSpan MinimumManualInterval = TimeSpan.FromSeconds(30);
    public const double JitterFraction = 0.10;

    private DiscoveryAgentState() { TenantId = null!; AgentId = null!; SourcesJson = "[]"; }

    private DiscoveryAgentState(TenantId tenantId, AgentId agentId)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(agentId);
        TenantId = tenantId;
        AgentId = agentId;
        Enabled = true;
        ScanIntervalMinutes = DefaultIntervalMinutes;
        IncludeDocker = true;
        SourcesJson = "[]";
    }

    /// <summary>A newly seen discovery-capable agent: its first scan is requested immediately.</summary>
    public static DiscoveryAgentState CreateInitial(TenantId tenantId, AgentId agentId, string initialReason, DateTimeOffset now)
    {
        var state = new DiscoveryAgentState(tenantId, agentId);
        state.ScanRequestedAt = now;
        state.ScanRequestReason = initialReason;
        return state;
    }

    public AgentId AgentId { get; private set; }
    public TenantId TenantId { get; private set; }
    public bool Enabled { get; private set; }
    public int ScanIntervalMinutes { get; private set; }
    public bool IncludeDocker { get; private set; }
    public DateTimeOffset? ScanRequestedAt { get; private set; }
    public string? ScanRequestReason { get; private set; }
    public string? RequestedInstanceKey { get; private set; }
    /// <summary>The claimed scan whose report is accepted; a report with any other id is stale.</summary>
    public Guid? ScanId { get; private set; }
    public string? ScanReason { get; private set; }
    public DateTimeOffset? ScanStartedAt { get; private set; }
    public DateTimeOffset? NextScanDueAt { get; private set; }
    public DateTimeOffset? LastScanAt { get; private set; }
    public DateTimeOffset? LastSuccessfulScanAt { get; private set; }
    public string? LastOutcome { get; private set; }
    public string? LastFailureCode { get; private set; }
    public string SourcesJson { get; private set; }

    public bool IsRunning(DateTimeOffset now) => ScanId is not null && ScanStartedAt is { } started && now - started < RunningTimeout;

    public DiscoveryScanState StateAt(DateTimeOffset now) =>
        IsRunning(now) ? DiscoveryScanState.Running : ScanRequestedAt is not null ? DiscoveryScanState.Requested : DiscoveryScanState.Idle;

    /// <summary>The reason to put into the heartbeat answer, or null when nothing is due.</summary>
    public string? DueReason(DateTimeOffset now, string periodicReason)
    {
        if (!Enabled || IsRunning(now)) return null;
        if (ScanRequestedAt is not null) return ScanRequestReason;
        return NextScanDueAt is { } due && due <= now ? periodicReason : null;
    }

    /// <summary>False when a scan was requested or started within <see cref="MinimumManualInterval"/>, so repeated clicks cannot flood the agent.</summary>
    public bool CanRequestManual(DateTimeOffset now) => ManualRetryAfter(now) <= TimeSpan.Zero;

    public TimeSpan ManualRetryAfter(DateTimeOffset now)
    {
        var last = ScanRequestedAt is { } requested && ScanStartedAt is { } started ? (requested > started ? requested : started) : ScanRequestedAt ?? ScanStartedAt;
        return last is { } at ? MinimumManualInterval - (now - at) : TimeSpan.Zero;
    }

    public void RequestScan(string reason, string? instanceKey, DateTimeOffset now)
    {
        ScanRequestedAt = now;
        ScanRequestReason = reason;
        RequestedInstanceKey = instanceKey;
    }

    /// <summary>
    /// Takes the scan for the caller. Until it reports, the next scan is due when the claim would time out, so an agent that crashes mid-scan is
    /// asked again after <see cref="RunningTimeout"/> instead of after a whole interval.
    /// </summary>
    public bool TryClaim(DateTimeOffset now, Guid scanId, string periodicReason, out string reason)
    {
        reason = DueReason(now, periodicReason)!;
        if (reason is null) return false;
        ScanId = scanId;
        ScanReason = reason;
        ScanStartedAt = now;
        ScanRequestedAt = null;
        ScanRequestReason = null;
        NextScanDueAt = now + RunningTimeout;
        return true;
    }

    /// <summary>The instance key of the claimed scan, cleared with the request that carried it.</summary>
    public string? TakeRequestedInstanceKey()
    {
        var key = RequestedInstanceKey;
        RequestedInstanceKey = null;
        return key;
    }

    /// <param name="jitter">In -1..1; spreads the periodic schedule of many agents. A failed scan is retried after the minimum interval.</param>
    public bool Complete(Guid scanId, DateTimeOffset now, string outcome, string? failureCode, string sourcesJson, double jitter = 0)
    {
        if (ScanId != scanId) return false;
        ScanId = null;
        ScanReason = null;
        LastScanAt = now;
        LastOutcome = outcome;
        LastFailureCode = failureCode;
        SourcesJson = sourcesJson;
        if (outcome != "Failed") LastSuccessfulScanAt = now;
        NextScanDueAt = now + (outcome == "Failed" ? TimeSpan.FromMinutes(MinimumIntervalMinutes) : Interval(jitter));
        return true;
    }

    public void ChangeSettings(bool enabled, int scanIntervalMinutes, bool includeDocker, DateTimeOffset now)
    {
        if (scanIntervalMinutes is < MinimumIntervalMinutes or > MaximumIntervalMinutes)
            throw new ArgumentOutOfRangeException(nameof(scanIntervalMinutes), "The scan interval is outside the supported range.");
        var enabling = enabled && !Enabled;
        Enabled = enabled;
        ScanIntervalMinutes = scanIntervalMinutes;
        IncludeDocker = includeDocker;
        if (!enabled)
        {
            ScanRequestedAt = null;
            ScanRequestReason = null;
            RequestedInstanceKey = null;
            NextScanDueAt = null;
        }
        else if (enabling)
        {
            RequestScan("manual", null, now);
            NextScanDueAt = now + Interval(0);
        }
        else
        {
            NextScanDueAt = (ScanStartedAt ?? now) + Interval(0);
        }
    }

    private TimeSpan Interval(double jitter) =>
        TimeSpan.FromMinutes(ScanIntervalMinutes * (1 + Math.Clamp(jitter, -1, 1) * JitterFraction));
}
