using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Downloads;

/// <summary>Download sessions of the request's tenant. Every context it opens is the filtered one of that tenant.</summary>
public sealed class PostgreSqlDownloadStore(DbContextOptions<PlatformDbContext> options, ITenantContext tenant, ICentralAuthorizationService centralAuth, TimeProvider? time = null) : IDownloadStore
{
    private readonly DownloadStoreCore core = new(tenantId => PlatformDbContext.ForTenant(options, tenant, tenantId), centralAuth, time);

    public Task<BackupArtifact?> GetArtifactAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken) => core.GetArtifactAsync(tenantId, artifactId, cancellationToken);
    public Task<DownloadSession?> CreateAsync(DownloadCreateCommand command, CancellationToken cancellationToken) => core.CreateAsync(command, cancellationToken);
    public Task<DownloadSession?> GetAsync(TenantId tenantId, Guid sessionId, CancellationToken cancellationToken) => core.GetAsync(tenantId, sessionId, cancellationToken);
    public Task<DownloadSession?> TransitionAsync(Guid sessionId, DownloadTransition transition, CancellationToken cancellationToken) => core.TransitionAsync(sessionId, transition, cancellationToken);
    public Task<DownloadSession?> ExpireSessionAsync(TenantId tenantId, Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken) => core.ExpireSessionAsync(tenantId, sessionId, now, cancellationToken);
    public Task<DownloadSession?> SwitchReplicaAsync(Guid sessionId, Guid currentReplicaId, string? expectedOwner, BackupReplica newReplica, CancellationToken cancellationToken) => core.SwitchReplicaAsync(sessionId, currentReplicaId, expectedOwner, newReplica, cancellationToken);
    public Task RecordRefusalAsync(DownloadRefusalCommand command, CancellationToken cancellationToken) => core.RecordRefusalAsync(command, cancellationToken);
}
