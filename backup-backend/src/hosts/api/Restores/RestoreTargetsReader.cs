using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Restores;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;

namespace BackupPlatform.Api.Restores;

public sealed record RestoreTargetAgent(Guid AgentId, string? HostName, string Status, string Connectivity, bool CanRestore, bool CanPrecheck, bool CanDiscover,
    bool SourceEligible, string? SourceAccess, string? RejectionCode, string? RejectionMessage);

public sealed record RestoreTargetJob(Guid JobId, string JobName, Guid AgentId, string Database, RestoreTargetView Target);

public sealed record RestoreTargets(Guid ArtifactId, string SourceDatabase, string BackupType, long SizeBytes, IReadOnlyList<RestoreTargetAgent> Agents, IReadOnlyList<RestoreTargetJob> Targets,
    string Engine = "mssql");

/// <summary>
/// What a restore of one artifact can be aimed at: the tenant's agents (with what each can do and whether it can read the backup) and the SQL Servers its
/// backup jobs point at. One composition for the tenant restore wizard and for support restores, so both always agree on eligibility.
/// </summary>
public sealed class RestoreTargetsReader(IBackupRetentionStore retentionStore, IBackupUiStore uiStore, AgentConnectivityQueryService agentService, TimeProvider time)
{
    public async Task<RestoreTargets?> ReadAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var artifactRecord = await retentionStore.GetArtifactAsync(tenantId, artifactId, cancellationToken);
        if (artifactRecord is null) return null;
        var artifact = artifactRecord.Artifact;
        var engine = BackupArtifactEngine.Read(artifact.SourceMetadataJson);
        var agentViews = await agentService.ListAsync(tenantId, AgentConnectivityFilter.All, cancellationToken);
        var jobs = await uiStore.ListJobsAsync(tenantId, now, cancellationToken);

        var agents = agentViews.Select(view =>
        {
            var targetAgentId = new AgentId(view.Agent.Id.Value);
            bool Supports(string identifier) => view.Capabilities?.Capabilities.Any(c => c.Identifier == identifier && c.Support == CapabilitySupport.Supported) == true;
            var selectedReplica = RestoreSourceEvaluator.SelectReplica(artifact, targetAgentId, now);
            RestoreSourceRejection? rejection = selectedReplica is null ? RestoreSourceEvaluator.Explain(artifact, targetAgentId, now) : null;
            var sourceAccess = selectedReplica is not null ? RestoreSourceEvaluator.Evaluate(artifact, selectedReplica, targetAgentId, now).Access?.ToString() : null;
            return new RestoreTargetAgent(view.Agent.Id.Value, view.Agent.HostName, view.Agent.Status.ToString(), view.Connectivity.ToString().ToLowerInvariant(),
                engine is not null && Supports(RestoreAgentCapability.ForPurpose(RestorePurpose.Execution, engine)),
                engine is not null && Supports(RestoreAgentCapability.ForPurpose(RestorePurpose.Precheck, engine)),
                engine is not null && Supports(RestoreAgentCapability.ForPurpose(RestorePurpose.Discovery, engine)),
                selectedReplica is not null, sourceAccess, rejection is not null ? RestoreService.RejectionCode(rejection.Value) : null,
                rejection is not null ? RestoreService.RejectionMessage(rejection.Value) : null);
        }).ToList();

        var targets = jobs.Where(job => job.Configuration is not null && job.Configuration.Engine == engine && !string.IsNullOrWhiteSpace(job.Configuration.Host))
            .Select(job => new RestoreTargetJob(job.Id, job.Name, job.AgentId, job.Configuration.Database,
                new RestoreTargetView(job.Configuration.Host, job.Configuration.Port, job.Configuration.IntegratedSecurity, job.Configuration.DatabaseCredentialReference,
                    job.Configuration.Encrypt, job.Configuration.TrustServerCertificate))).ToList();
        return new RestoreTargets(artifact.Id, artifactRecord.DatabaseName ?? string.Empty, artifact.BackupType, artifact.SizeBytes, agents, targets, engine ?? "unsupported");
    }
}
