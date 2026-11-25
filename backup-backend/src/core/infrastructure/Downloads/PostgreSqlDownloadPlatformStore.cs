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

/// <summary>Agent-served downloads and the session expiry sweep, on the unfiltered platform-operations context.</summary>
public sealed class PostgreSqlDownloadPlatformStore(DbContextOptions<PlatformDbContext> options, ICentralAuthorizationService centralAuth, TimeProvider? time = null) : IDownloadPlatformStore
{
    private readonly DownloadStoreCore core = new(_ => PlatformOperationsDbContext.Create(options), centralAuth, time);

    public Task<BackupArtifact?> GetArtifactAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken) => core.GetArtifactAsync(tenantId, artifactId, cancellationToken);
    public Task<DownloadSession?> GetAsync(TenantId tenantId, Guid sessionId, CancellationToken cancellationToken) => core.GetAsync(tenantId, sessionId, cancellationToken);
    public Task<IReadOnlyList<DownloadSession>> ListPreparingAsync(TenantId tenantId, Guid agentId, CancellationToken cancellationToken) => core.ListPreparingAsync(tenantId, agentId, cancellationToken);
    public Task<DownloadSession?> TransitionAsync(Guid sessionId, DownloadTransition transition, CancellationToken cancellationToken) => core.TransitionAsync(sessionId, transition, cancellationToken);
    public Task<int> ExpireAsync(DateTimeOffset now, CancellationToken cancellationToken) => core.ExpireAsync(now, cancellationToken);
}
