using BackupPlatform.Application.Lists;
using BackupPlatform.Application.Platform;
using BackupPlatform.Infrastructure.Lists;
using BackupPlatform.Domain.Storage;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>Artifact row lock and record projection the tenant and platform-operations retention stores share.</summary>
internal static class RetentionArtifacts
{
    /// <summary>
    /// Locks the artifact row, tenant-qualified so another tenant's id is simply not found. With <paramref name="skipLocked"/> a row somebody else
    /// holds counts as not obtained instead of blocking.
    /// </summary>
    internal static async Task<bool> LockArtifactAsync(PlatformDbContext context, TenantId tenantId, Guid artifactId, bool skipLocked, CancellationToken cancellationToken)
    {
        List<Guid> locked;
        if (skipLocked)
        {
            locked = await context.Database.SqlQuery<Guid>(
                $"SELECT \"Id\" AS \"Value\" FROM backup_artifacts WHERE \"Id\" = {artifactId} AND \"TenantId\" = {tenantId.Value} FOR UPDATE SKIP LOCKED").ToListAsync(cancellationToken);
        }
        else
        {
            locked = await context.Database.SqlQuery<Guid>(
                $"SELECT \"Id\" AS \"Value\" FROM backup_artifacts WHERE \"Id\" = {artifactId} AND \"TenantId\" = {tenantId.Value} FOR UPDATE").ToListAsync(cancellationToken);
        }
        return locked.Count > 0;
    }

    internal sealed record ArtifactOwner(Guid RunId, Guid JobId, string JobName, string DatabaseName, string? AgentName, AgentId AgentId, BackupConfiguration Snapshot);

    internal static async Task<Dictionary<Guid, ArtifactOwner>> ResolveOwnersAsync(PlatformDbContext context, TenantId tenantId, Guid[] attemptIds, CancellationToken cancellationToken)
    {
        var rows = await (from attempt in context.BackupRunAttempts.AsNoTracking()
                          join run in context.BackupRuns.AsNoTracking() on new { attempt.RunId, attempt.TenantId } equals new { RunId = run.Id, run.TenantId }
                          join job in context.BackupJobs.AsNoTracking() on new { run.JobId, run.TenantId } equals new { JobId = job.Id, job.TenantId }
                          join agent in context.Agents.AsNoTracking() on new { run.AgentId, run.TenantId } equals new { AgentId = agent.Id, agent.TenantId }
                          where attempt.TenantId == tenantId && attemptIds.Contains(attempt.Id)
                          select new { AttemptId = attempt.Id, attempt.RunId, run.JobId, JobName = job.Name,
                              Snapshot = run.Configuration, AgentName = agent.HostName, run.AgentId }).ToListAsync(cancellationToken);
        return rows.ToDictionary(row => row.AttemptId, row => new ArtifactOwner(row.RunId, row.JobId, row.JobName, row.Snapshot.Database, row.AgentName, row.AgentId, row.Snapshot));
    }

    internal static BackupArtifactRecord ToRecord(BackupArtifact artifact, Dictionary<Guid, ArtifactOwner> owners)
    {
        // Tenant-qualified joins resolve only safe display labels, never configuration credentials or paths.
        if (!owners.TryGetValue(artifact.AttemptId, out var owner)) throw new InvalidOperationException("The artifact's run could not be resolved.");
        return new BackupArtifactRecord(artifact, owner.RunId, owner.JobId, owner.JobName, owner.DatabaseName, owner.AgentName, owner.AgentId.Value, owner.Snapshot);
    }

    internal static async Task<BackupArtifactRecord?> GetAsync(PlatformDbContext context, TenantId tenantId, Guid artifactId, CancellationToken cancellationToken)
    {
        var artifact = await context.BackupArtifacts.AsNoTracking().Include(entity => entity.Replicas)
            .SingleOrDefaultAsync(entity => entity.TenantId == tenantId && entity.Id == artifactId, cancellationToken);
        if (artifact is null) return null;
        var owners = await ResolveOwnersAsync(context, tenantId, [artifact.AttemptId], cancellationToken);
        return ToRecord(artifact, owners);
    }
}
