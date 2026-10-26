using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

public sealed class PostgreSqlReplicaMissingReporter(DbContextOptions<PlatformDbContext> options) : IReplicaMissingReporter
{
    public async Task ReportMissingAsync(TenantId tenantId, Guid replicaId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var artifactId = await context.BackupReplicas.AsNoTracking().Where(r => r.TenantId == tenantId && r.Id == replicaId)
            .Select(r => (Guid?)r.ArtifactId).SingleOrDefaultAsync(cancellationToken);
        if (artifactId is not { } artifact) return;
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        // Artifact first, like every writer of its replicas, so a repair completing on the same destination cannot interleave.
        await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM backup_artifacts WHERE "TenantId" = {tenantId.Value} AND "Id" = {artifact} FOR NO KEY UPDATE
            """).ToListAsync(cancellationToken);
        var replica = await context.BackupReplicas.SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == replicaId, cancellationToken);
        if (replica is null || !replica.MarkMissing()) return;
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
