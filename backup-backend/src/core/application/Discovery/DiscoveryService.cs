using BackupPlatform.Application.Agents;
using BackupPlatform.Contracts.Discovery;
using BackupPlatform.Domain.Discovery;
using BackupPlatform.Domain.Identity;
using System.Text.Json;

namespace BackupPlatform.Application.Discovery;

public sealed record DiscoveryReportResult(ReportStatus Status, string? InvalidField);

public sealed record DiscoverySettingsRequest(bool Enabled, int ScanIntervalMinutes, bool IncludeDocker);

public sealed record DiscoveryCredentialRequest(string? CredentialReference, bool IntegratedSecurity, bool Encrypt = true, bool TrustServerCertificate = false);

/// <summary>
/// Agent-facing scan claim/report and tenant-facing inventory operations. Authorization (tenant role, agent principal) happens at the endpoint;
/// the tenant or agent identity always arrives from it, never from a request body.
/// </summary>
public sealed class DiscoveryService(IDiscoveryStore store, TimeProvider time)
{
    private static readonly JsonSerializerOptions SourceJson = new(JsonSerializerDefaults.Web);

    public Task<string?> GetScanReasonAsync(AgentIdentity caller, CancellationToken cancellationToken) =>
        store.GetDueReasonAsync(caller, time.GetUtcNow(), cancellationToken);

    public Task<AgentDiscoveryPlan?> ClaimAsync(AgentIdentity caller, CancellationToken cancellationToken) =>
        store.ClaimAsync(caller, Guid.NewGuid(), Random.Shared.NextDouble() * 2 - 1, time.GetUtcNow(), cancellationToken);

    public async Task<DiscoveryReportResult> ReportAsync(AgentIdentity caller, Guid scanId, AgentDiscoveryReport? report, CancellationToken cancellationToken)
    {
        if (!DiscoveryReportValidator.TryValidate(report, out var invalidField))
        {
            return new DiscoveryReportResult(ReportStatus.Invalid, invalidField);
        }
        var sources = JsonSerializer.Serialize(report!.Sources.Select(source => new DiscoverySourceView(source.Source, source.Status, source.ReasonCode)), SourceJson);
        var status = await store.ApplyReportAsync(caller, scanId, report, sources, time.GetUtcNow(), cancellationToken);
        return new DiscoveryReportResult(status, null);
    }

    public Task<IReadOnlyList<DiscoveryAgentStatus>> ListAgentsAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        store.ListAgentsAsync(tenantId, time.GetUtcNow(), cancellationToken);

    public Task<IReadOnlyList<DiscoveryInstanceSummary>> ListInstancesAsync(TenantId tenantId, Guid? agentId, string? engine, bool includeMissing, CancellationToken cancellationToken) =>
        store.ListInstancesAsync(tenantId, agentId, engine, includeMissing, cancellationToken);

    public Task<DiscoveryInstanceDetail?> GetInstanceAsync(TenantId tenantId, Guid instanceId, CancellationToken cancellationToken) =>
        store.GetInstanceAsync(tenantId, instanceId, cancellationToken);

    public Task<RescanOutcome> RescanAsync(TenantId tenantId, Guid agentId, UserId actor, CancellationToken cancellationToken) =>
        store.RequestRescanAsync(tenantId, new AgentId(agentId), null, actor, time.GetUtcNow(), cancellationToken);

    public Task<RescanOutcome> VerifyAsync(TenantId tenantId, Guid instanceId, UserId actor, CancellationToken cancellationToken) =>
        store.RequestRescanAsync(tenantId, null, instanceId, actor, time.GetUtcNow(), cancellationToken);

    /// <exception cref="ArgumentOutOfRangeException">The interval is outside the supported range.</exception>
    public Task<DiscoveryAgentStatus?> ChangeSettingsAsync(TenantId tenantId, Guid agentId, DiscoverySettingsRequest request, UserId actor, CancellationToken cancellationToken)
    {
        if (request.ScanIntervalMinutes is < DiscoveryAgentState.MinimumIntervalMinutes or > DiscoveryAgentState.MaximumIntervalMinutes)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The scan interval is outside the supported range.");
        }
        return store.ChangeSettingsAsync(tenantId, new AgentId(agentId), request.Enabled, request.ScanIntervalMinutes, request.IncludeDocker, actor, time.GetUtcNow(), cancellationToken);
    }

    public Task<BindOutcome> BindCredentialAsync(TenantId tenantId, Guid instanceId, DiscoveryCredentialRequest request, UserId actor, CancellationToken cancellationToken)
    {
        var reference = string.IsNullOrWhiteSpace(request.CredentialReference) ? null : request.CredentialReference.Trim();
        if (reference is not null && !DiscoveryReportValidator.IsValidReference(reference))
        {
            return Task.FromResult(new BindOutcome(BindStatus.Invalid));
        }
        return store.BindCredentialAsync(tenantId, instanceId, reference, request.IntegratedSecurity, request.Encrypt, request.TrustServerCertificate, actor, time.GetUtcNow(), cancellationToken);
    }
}
