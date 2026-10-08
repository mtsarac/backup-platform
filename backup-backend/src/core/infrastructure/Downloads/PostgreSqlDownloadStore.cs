using BackupPlatform.Application.Downloads;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Downloads;

/// <summary>Replica locks serialize durable holds with deletion; session locks fence lifecycle writes and their audits.</summary>
public sealed class PostgreSqlDownloadStore(DbContextOptions<PlatformDbContext> options, TimeProvider? time = null) : IDownloadStore
{
    private const int MaxBatch = 500;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<BackupArtifact?> GetArtifactAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.BackupArtifacts.AsNoTracking().Include(artifact => artifact.Replicas)
            .SingleOrDefaultAsync(artifact => artifact.TenantId == tenantId && artifact.Id == artifactId, cancellationToken);
    }

    public async Task<DownloadSession?> CreateAsync(DownloadCreateCommand command, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (!await IsAdministratorAsync(context, command.TenantId, command.UserId, cancellationToken)) return null;
        var artifactLocked = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM backup_artifacts
            WHERE "TenantId" = {command.TenantId.Value} AND "Id" = {command.ArtifactId} FOR NO KEY UPDATE
            """).ToListAsync(cancellationToken);
        if (artifactLocked.Count == 0) return null;
        var replicaLocked = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM backup_replicas
            WHERE "TenantId" = {command.TenantId.Value} AND "ArtifactId" = {command.ArtifactId} AND "Id" = {command.ReplicaId} FOR UPDATE
            """).ToListAsync(cancellationToken);
        if (replicaLocked.Count == 0) return null;
        // Fresh statements after both locks: neither a retention mutation nor a physical claim can invalidate eligibility before the hold commits.
        var artifact = await context.BackupArtifacts.AsNoTracking().SingleAsync(entity => entity.TenantId == command.TenantId && entity.Id == command.ArtifactId, cancellationToken);
        var replica = await context.BackupReplicas.AsNoTracking().SingleAsync(entity => entity.TenantId == command.TenantId && entity.ArtifactId == command.ArtifactId && entity.Id == command.ReplicaId, cancellationToken);
        if (replica.Status != BackupReplicaStatus.Available || replica.VerificationState != "Verified" ||
            replica.Checksum != artifact.ArtifactChecksum || replica.SizeBytes != artifact.SizeBytes || artifact.ChecksumAlgorithm != "sha256") return null;
        var session = new DownloadSession(Guid.NewGuid(), artifact, replica, command.UserId, command.Filename, command.Now, command.Lifetime);
        context.DownloadSessions.Add(session);
        AddAudit(context, session, command.Now, "download.requested");
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return session;
    }

    public async Task<DownloadSession?> GetAsync(TenantId tenantId, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.DownloadSessions.AsNoTracking().SingleOrDefaultAsync(session => session.TenantId == tenantId && session.Id == sessionId, cancellationToken);
    }

    public async Task<IReadOnlyList<DownloadSession>> ListPreparingAsync(TenantId tenantId, Guid agentId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var now = _time.GetUtcNow();
        return await context.DownloadSessions.AsNoTracking().Where(session => session.TenantId == tenantId && session.AgentId == agentId &&
            session.State == DownloadState.Preparing && session.ReleasedAt == null && session.ExpiresAt > now &&
            (session.LeaseExpiresAt == null || session.LeaseExpiresAt > now))
            .OrderBy(session => session.CreatedAt).ThenBy(session => session.Id).Take(MaxBatch).ToListAsync(cancellationToken);
    }

    public async Task<DownloadSession?> TransitionAsync(Guid sessionId, DownloadTransition transition, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (transition.ExpectedState == DownloadState.Preparing && transition.State == DownloadState.Preparing &&
            transition.ExpectedOwner is null && transition.Owner is not null)
        {
            var target = await context.DownloadSessions.AsNoTracking().Where(entity => entity.Id == sessionId)
                .Select(entity => new { entity.TenantId, entity.AgentId }).SingleOrDefaultAsync(cancellationToken);
            if (target?.AgentId is not { } agentId ||
                !await BackupPlatform.Infrastructure.Backups.PostgreSqlBackupRunStore.LockAgentAsync(context, target.TenantId, new AgentId(agentId), cancellationToken)) return null;
            // Lock the shared agent before any session, so concurrent polls cannot claim different downloads for its single worker.
            var busy = await context.DownloadSessions.AnyAsync(entity => entity.TenantId == target.TenantId && entity.AgentId == agentId &&
                entity.State == DownloadState.Preparing && entity.LeaseOwner != null && entity.ReleasedAt == null &&
                entity.ExpiresAt > transition.Now && entity.LeaseExpiresAt > transition.Now, cancellationToken);
            if (busy) return null;
        }
        var session = await LockSessionAsync(context, sessionId, cancellationToken);
        if (session is null) return null;
        if (transition.State == DownloadState.Streaming && transition.ExpectedState != DownloadState.Streaming &&
            !await IsAdministratorAsync(context, session.TenantId, session.UserId, cancellationToken)) return null;
        var previous = session.State;
        if (!session.Transition(transition.ExpectedState, transition.ExpectedOwner, transition.State, transition.Owner,
            transition.Now, transition.LeaseDuration, transition.Bytes, transition.FailureCode)) return null;
        if (previous != session.State || (transition.ExpectedOwner is null && session.LeaseOwner is not null))
            AddAudit(context, session, transition.Now, $"download.{session.State.ToString().ToLowerInvariant()}");
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return session;
    }

    public async Task<int> ExpireAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        now = now.ToUniversalTime();
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var ids = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM download_sessions
            WHERE "ReleasedAt" IS NULL AND ("ExpiresAt" <= {now} OR "LeaseExpiresAt" <= {now})
            ORDER BY "ExpiresAt", "Id" LIMIT {MaxBatch} FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);
        var sessions = await context.DownloadSessions.Where(session => ids.Contains(session.Id)).ToListAsync(cancellationToken);
        var expired = 0;
        foreach (var session in sessions)
        {
            if (!session.Expire(now)) continue;
            AddAudit(context, session, now, "download.expired");
            expired++;
        }
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return expired;
    }

    public async Task<DownloadSession?> ExpireSessionAsync(TenantId tenantId, Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var ids = await context.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM download_sessions
            WHERE "TenantId" = {tenantId.Value} AND "Id" = {sessionId} FOR UPDATE
            """).ToListAsync(cancellationToken);
        if (ids.Count == 0) return null;
        var session = await context.DownloadSessions.SingleAsync(entity => entity.TenantId == tenantId && entity.Id == sessionId, cancellationToken);
        if (session.Expire(now))
        {
            AddAudit(context, session, now, "download.expired");
            await context.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return session;
    }

    private static async Task<DownloadSession?> LockSessionAsync(PlatformDbContext context, Guid sessionId, CancellationToken cancellationToken)
    {
        var ids = await context.Database.SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM download_sessions WHERE \"Id\" = {sessionId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return ids.Count == 0 ? null : await context.DownloadSessions.SingleAsync(session => session.Id == sessionId, cancellationToken);
    }

    private static Task<bool> IsAdministratorAsync(PlatformDbContext context, TenantId tenantId, Guid userId, CancellationToken cancellationToken) =>
        context.TenantMemberships.AnyAsync(membership => membership.TenantId == tenantId && membership.UserId == new UserId(userId) &&
            (membership.Role == TenantRole.Owner || membership.Role == TenantRole.Admin), cancellationToken);

    private static void AddAudit(PlatformDbContext context, DownloadSession session, DateTimeOffset now, string action) =>
        context.AuditEvents.Add(AuditEvent.Record(now, new UserId(session.UserId), AuditActorKind.TenantAdministrator, action, session.TenantId,
            "download", session.Id.ToString("D"), new Dictionary<string, object?>
            {
                ["artifactId"] = session.ArtifactId, ["replicaId"] = session.ReplicaId, ["sessionId"] = session.Id,
                ["transferredBytes"] = session.TransferredBytes, ["status"] = session.State.ToString(), ["failure"] = session.FailureCode,
                ["startedAt"] = action == "download.streaming" ? now : (DateTimeOffset?)null,
                ["completedAt"] = session.ReleasedAt, ["result"] = session.IsTerminal ? session.State.ToString() : null
            }));
}
