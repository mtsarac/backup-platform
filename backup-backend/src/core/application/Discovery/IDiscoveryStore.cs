using BackupPlatform.Application.Agents;
using BackupPlatform.Contracts.Discovery;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Discovery;

public enum RescanStatus
{
    Requested = 1,
    NotFound,
    Unsupported,
    Disabled,
    TooSoon,
    CredentialsRequired
}

public sealed record RescanOutcome(RescanStatus Status, DiscoveryAgentStatus? Agent = null, TimeSpan? RetryAfter = null);

public enum ReportStatus
{
    Applied = 1,
    /// <summary>The scan id is not the agent's current one (superseded, already reported, or never claimed).</summary>
    Superseded,
    AgentNotFound,
    Invalid
}

public enum BindStatus
{
    Bound = 1,
    NotFound,
    Invalid
}

public sealed record BindOutcome(BindStatus Status, DiscoveryInstanceSummary? Instance = null);

/// <summary>
/// Persistence of the discovery inventory. Every method names its tenant, and every write of one agent is serialised by a row lock on the agent's
/// scan-state row, so several backend nodes converge on one state.
/// </summary>
public interface IDiscoveryStore
{
    /// <summary>
    /// The reason a scan is due for the calling agent, or null. An agent that has never been seen and advertises the discovery capability gets its
    /// initial scan requested here (one indexed read for known agents).
    /// </summary>
    Task<string?> GetDueReasonAsync(AgentIdentity caller, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Atomically claims the due scan, or returns null when nothing is due or one is already running.</summary>
    Task<AgentDiscoveryPlan?> ClaimAsync(AgentIdentity caller, Guid scanId, double jitter, DateTimeOffset now, CancellationToken cancellationToken);

    Task<ReportStatus> ApplyReportAsync(AgentIdentity caller, Guid scanId, AgentDiscoveryReport report, string sourcesJson, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<DiscoveryAgentStatus>> ListAgentsAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<DiscoveryInstanceSummary>> ListInstancesAsync(TenantId tenantId, Guid? agentId, string? engine, bool includeMissing, CancellationToken cancellationToken);

    Task<DiscoveryInstanceDetail?> GetInstanceAsync(TenantId tenantId, Guid instanceId, CancellationToken cancellationToken);

    /// <summary>Requests a scan: of <paramref name="agentId"/>, or (verify) of the agent owning <paramref name="instanceId"/>, narrowed to that instance, whose binding must exist. Exactly one of the two is given.</summary>
    Task<RescanOutcome> RequestRescanAsync(TenantId tenantId, AgentId? agentId, Guid? instanceId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);

    Task<DiscoveryAgentStatus?> ChangeSettingsAsync(TenantId tenantId, AgentId agentId, bool enabled, int scanIntervalMinutes, bool includeDocker, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);

    Task<BindOutcome> BindCredentialAsync(TenantId tenantId, Guid instanceId, string? credentialReference, bool integratedSecurity, bool encrypt, bool trustServerCertificate, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);
}
