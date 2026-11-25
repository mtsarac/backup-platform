using System.Text.Json;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Discovery;
using BackupPlatform.Contracts.Discovery;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Discovery;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using BackupPlatform.Application.Tenancy;

namespace BackupPlatform.Infrastructure.Discovery;

/// <summary>Discovery inventory of the request's tenant on the filtered context.</summary>
public sealed class PostgreSqlDiscoveryStore(DbContextOptions<PlatformDbContext> options, ITenantContext tenant) : IDiscoveryStore
{
    private readonly DiscoveryStoreCore core = new(tenantId => PlatformDbContext.ForTenant(options, tenant, tenantId));

    public Task<IReadOnlyList<DiscoveryAgentStatus>> ListAgentsAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken) => core.ListAgentsAsync(tenantId, now, cancellationToken);
    public Task<IReadOnlyList<DiscoveryInstanceSummary>> ListInstancesAsync(TenantId tenantId, Guid? agentId, string? engine, bool includeMissing, CancellationToken cancellationToken) => core.ListInstancesAsync(tenantId, agentId, engine, includeMissing, cancellationToken);
    public Task<DiscoveryInstanceDetail?> GetInstanceAsync(TenantId tenantId, Guid instanceId, CancellationToken cancellationToken) => core.GetInstanceAsync(tenantId, instanceId, cancellationToken);
    public Task<RescanOutcome> RequestRescanAsync(TenantId tenantId, AgentId? agentId, Guid? instanceId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken) => core.RequestRescanAsync(tenantId, agentId, instanceId, actor, now, cancellationToken);
    public Task<DiscoveryAgentStatus?> ChangeSettingsAsync(TenantId tenantId, AgentId agentId, bool enabled, int scanIntervalMinutes, bool includeDocker, UserId actor, DateTimeOffset now, CancellationToken cancellationToken) => core.ChangeSettingsAsync(tenantId, agentId, enabled, scanIntervalMinutes, includeDocker, actor, now, cancellationToken);
    public Task<BindOutcome> BindCredentialAsync(TenantId tenantId, Guid instanceId, string? credentialReference, bool integratedSecurity, bool encrypt, bool trustServerCertificate, UserId actor, DateTimeOffset now, CancellationToken cancellationToken) => core.BindCredentialAsync(tenantId, instanceId, credentialReference, integratedSecurity, encrypt, trustServerCertificate, actor, now, cancellationToken);
}
