using BackupPlatform.Application.Tenancy;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Downloads;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

/// <summary>Test seam over both download stores: tenant calls run bound to the tenant the call (or the session it names) belongs to, agent and worker calls on the platform-operations store.</summary>
internal sealed class DownloadStoreFacade(DbContextOptions<PlatformDbContext> options, ICentralAuthorizationService centralAuth, TimeProvider? time = null) : IDownloadStore, IDownloadPlatformStore
{
    private readonly PostgreSqlDownloadPlatformStore _platform = new(options, centralAuth, time);
    private IDownloadStore For(TenantId tenantId) => new PostgreSqlDownloadStore(options, new MutableTenantContext(tenantId), centralAuth, time);

    private async Task<IDownloadStore> ForSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var context = PlatformOperationsDbContext.Create(options);
        var tenant = await context.DownloadSessions.AsNoTracking().Where(session => session.Id == sessionId).Select(session => (TenantId?)session.TenantId).SingleOrDefaultAsync(cancellationToken);
        return For(tenant ?? throw new InvalidOperationException("The download session does not exist."));
    }

    public Task<BackupArtifact?> GetArtifactAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken) => For(tenantId).GetArtifactAsync(tenantId, artifactId, cancellationToken);
    public Task<DownloadSession?> CreateAsync(DownloadCreateCommand command, CancellationToken cancellationToken) => For(command.TenantId).CreateAsync(command, cancellationToken);
    public Task<DownloadSession?> GetAsync(TenantId tenantId, Guid sessionId, CancellationToken cancellationToken) => For(tenantId).GetAsync(tenantId, sessionId, cancellationToken);
    public async Task<DownloadSession?> TransitionAsync(Guid sessionId, DownloadTransition transition, CancellationToken cancellationToken) => await (await ForSessionAsync(sessionId, cancellationToken)).TransitionAsync(sessionId, transition, cancellationToken);
    public Task<DownloadSession?> ExpireSessionAsync(TenantId tenantId, Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken) => For(tenantId).ExpireSessionAsync(tenantId, sessionId, now, cancellationToken);
    public async Task<DownloadSession?> SwitchReplicaAsync(Guid sessionId, Guid currentReplicaId, string? expectedOwner, BackupReplica newReplica, CancellationToken cancellationToken) => await (await ForSessionAsync(sessionId, cancellationToken)).SwitchReplicaAsync(sessionId, currentReplicaId, expectedOwner, newReplica, cancellationToken);
    public Task RecordRefusalAsync(DownloadRefusalCommand command, CancellationToken cancellationToken) => For(command.TenantId).RecordRefusalAsync(command, cancellationToken);
    public Task<IReadOnlyList<DownloadSession>> ListPreparingAsync(TenantId tenantId, Guid agentId, CancellationToken cancellationToken) => _platform.ListPreparingAsync(tenantId, agentId, cancellationToken);
    public Task<int> ExpireAsync(DateTimeOffset now, CancellationToken cancellationToken) => _platform.ExpireAsync(now, cancellationToken);
}
