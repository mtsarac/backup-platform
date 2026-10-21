using System.Text.Json;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Storage;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.Storage;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Storage;

public sealed class PostgreSqlStorageCapacityTaskQueue(DbContextOptions<PlatformDbContext> options,
    AgentConnectivityOptions connectivity) : IStorageCapacityTaskQueue
{
    public async Task<StorageCapacityResponse> EnqueueAsync(TenantId tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(db, tenantId, connectionId, cancellationToken);
        var pending = await PendingAsync(db, tenantId, connectionId, now, cancellationToken);
        if (pending is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return Pending(pending);
        }
        var connection = await db.StorageConnections.SingleOrDefaultAsync(c => c.Id == connectionId && c.TenantId == tenantId, cancellationToken);
        var agent = connection is { EffectiveEnabled: true }
            ? (await EligibleAgentsAsync(db, tenantId, now, cancellationToken)).FirstOrDefault() : Guid.Empty;
        StorageCapacityResponse response;
        if (agent == Guid.Empty)
        {
            var snapshot = StorageCapacitySnapshot.UnavailableResult(tenantId, connectionId, now, "No authorized compatible online agent is available.");
            db.StorageCapacitySnapshots.Add(snapshot);
            response = StorageCapacityResponse.From(snapshot);
        }
        else
        {
            var task = new StorageCapacityTask(tenantId, connectionId, agent, now);
            db.StorageCapacityTasks.Add(task);
            response = Pending(task);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    public async Task<StorageCapacityResponse?> GetPendingAsync(TenantId tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(db, tenantId, connectionId, cancellationToken);
        var task = await PendingAsync(db, tenantId, connectionId, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return task is null ? null : Pending(task);
    }

    public async Task<CapacityProbeTaskRequest?> ClaimAsync(AgentIdentity identity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = PlatformDbContext.CreateForPlatformOperations(options);
        if (!(await EligibleAgentsAsync(db, identity.TenantId, now, cancellationToken)).Contains(identity.AgentId.Value)) return null;
        var connectionIds = await db.StorageCapacityTasks.AsNoTracking()
            .Where(t => t.TenantId == identity.TenantId && t.AgentId == identity.AgentId.Value && t.CompletedAt == null)
            .OrderBy(t => t.CreatedAt).Select(t => t.ConnectionId).Take(32).ToListAsync(cancellationToken);
        foreach (var connectionId in connectionIds)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await LockAsync(db, identity.TenantId, connectionId, cancellationToken);
            var task = await PendingAsync(db, identity.TenantId, connectionId, now, cancellationToken);
            var connection = await db.StorageConnections.SingleOrDefaultAsync(c => c.Id == connectionId && c.TenantId == identity.TenantId, cancellationToken);
            CapacityProbeTaskRequest? request = null;
            if (task is not null && task.AgentId == identity.AgentId.Value && (task.LeaseExpiresAt is null || task.LeaseExpiresAt <= now)
                && connection is { EffectiveEnabled: true })
            {
                task.Claim(now, connection.UpdatedAt);
                request = new CapacityProbeTaskRequest
                {
                    TaskId = task.Id, ConnectionId = task.ConnectionId, TenantId = identity.TenantId.Value, AgentId = task.AgentId,
                    LeaseToken = task.LeaseToken!.Value, LeaseExpiresAt = task.LeaseExpiresAt!.Value,
                    ProviderType = connection.ProviderType, Configuration = connection.Configuration
                };
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (request is not null) return request;
        }
        return null;
    }

    public async Task<bool> IsLiveAsync(AgentIdentity identity, Guid taskId, Guid token, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = PlatformDbContext.CreateForPlatformOperations(options);
        var task = await db.StorageCapacityTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == taskId && t.TenantId == identity.TenantId, cancellationToken);
        return task is not null && await IsLiveAsync(db, identity, task, token, now, cancellationToken);
    }

    public async Task<BackupMutationResult> CompleteAsync(AgentIdentity identity, Guid taskId, CapacityProbeCompletionRequest request,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = PlatformDbContext.CreateForPlatformOperations(options);
        var connectionId = await db.StorageCapacityTasks.Where(t => t.Id == taskId && t.TenantId == identity.TenantId)
            .Select(t => (Guid?)t.ConnectionId).SingleOrDefaultAsync(cancellationToken);
        if (connectionId is null) return BackupMutationResult.NotFound;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(db, identity.TenantId, connectionId.Value, cancellationToken);
        var task = await db.StorageCapacityTasks.SingleAsync(t => t.Id == taskId && t.TenantId == identity.TenantId, cancellationToken);
        if (request.Result is null) return BackupMutationResult.Conflict;
        var result = request.Result with { ErrorMessage = null };
        if (task.AgentId != identity.AgentId.Value || task.LeaseToken != request.LeaseToken || request.LeaseToken == Guid.Empty
            || result.TaskId != taskId || result.ConnectionId != task.ConnectionId || !Valid(result)) return BackupMutationResult.Conflict;
        var json = JsonSerializer.Serialize(result);
        if (task.CompletedAt is not null) return task.CompletionJson is { } saved && JsonSerializer.Deserialize<CapacityProbeTaskResult>(saved) == result ? BackupMutationResult.Accepted : BackupMutationResult.Conflict;
        if (!await IsLiveAsync(db, identity, task, request.LeaseToken, now, cancellationToken)) return BackupMutationResult.Conflict;
        // Agent free text can contain provider credentials; persist only a bounded public failure code.
        var snapshot = result.Succeeded
            ? StorageCapacitySnapshot.AvailableResult(identity.TenantId, task.ConnectionId, result.TotalBytes, result.UsedBytes, result.FreeBytes, now)
            : StorageCapacitySnapshot.UnavailableResult(identity.TenantId, task.ConnectionId, now, result.ErrorCode);
        task.Complete(now, json);
        db.StorageCapacitySnapshots.Add(snapshot);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return BackupMutationResult.Accepted;
    }

    private async Task<StorageCapacityTask?> PendingAsync(PlatformDbContext db, TenantId tenantId, Guid connectionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var task = await db.StorageCapacityTasks.SingleOrDefaultAsync(t => t.TenantId == tenantId && t.ConnectionId == connectionId && t.CompletedAt == null, cancellationToken);
        if (task is null) return null;
        if (task.ExpiresAt > now && (task.Attempts < 3 || task.LeaseExpiresAt > now)
            && (await EligibleAgentsAsync(db, tenantId, now, cancellationToken)).Contains(task.AgentId)
            && await db.StorageConnections.AnyAsync(c => c.Id == connectionId && c.TenantId == tenantId && c.Enabled && !c.AdministrativelyDisabled, cancellationToken)) return task;
        task.Complete(now, null);
        db.StorageCapacitySnapshots.Add(StorageCapacitySnapshot.UnavailableResult(tenantId, connectionId, now, "Capacity probe expired or its agent is unavailable."));
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }
    private async Task<bool> IsLiveAsync(PlatformDbContext db, AgentIdentity identity, StorageCapacityTask task, Guid token,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        task.CompletedAt is null && task.AgentId == identity.AgentId.Value && task.LeaseToken == token && token != Guid.Empty
        && task.LeaseExpiresAt > now && task.ExpiresAt > now
        && (await EligibleAgentsAsync(db, identity.TenantId, now, cancellationToken)).Contains(identity.AgentId.Value)
        && await db.StorageConnections.AnyAsync(c => c.Id == task.ConnectionId && c.TenantId == identity.TenantId
            && c.Enabled && !c.AdministrativelyDisabled && c.UpdatedAt == task.ConnectionUpdatedAt, cancellationToken);

    private Task<List<Guid>> EligibleAgentsAsync(PlatformDbContext db, TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now - connectivity.OfflineTimeout;
        return db.Database.SqlQuery<Guid>($"""
            SELECT a."Id" AS "Value" FROM agents a
            JOIN agent_connectivity live ON live."AgentId" = a."Id" AND live."TenantId" = a."TenantId"
            JOIN agent_capabilities cap ON cap."AgentId" = a."Id" AND cap."TenantId" = a."TenantId"
            WHERE a."TenantId" = {tenantId.Value} AND a."Status" = 2 AND live."LastSeenAt" >= {cutoff}
              AND cap."Identifier" = {CapacityProbeProtocol.Capability} AND cap."Support" = 1
              AND cap."Revision" = live."CapabilityRevision"
            ORDER BY a."Id"
            """).ToListAsync(cancellationToken);
    }

    private static Task LockAsync(PlatformDbContext db, TenantId tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({$"capacity:{tenantId.Value:N}:{connectionId:N}"}, 0))", cancellationToken);

    private static StorageCapacityResponse Pending(StorageCapacityTask task) => new()
    {
        ConnectionId = task.ConnectionId, TaskId = task.Id, Status = "Pending", ReportedAt = task.CreatedAt
    };

    private static bool Valid(CapacityProbeTaskResult result) => result.Succeeded
        ? result.TotalBytes is > 0 && result.UsedBytes is >= 0 && result.FreeBytes is >= 0
            && result.UsedBytes <= result.TotalBytes && result.FreeBytes <= result.TotalBytes
            && result.UsedBytes == result.TotalBytes - result.FreeBytes && result.ErrorCode is null && result.ErrorMessage is null
        : result.TotalBytes is null && result.UsedBytes is null && result.FreeBytes is null
            && result.ErrorCode is CapacityProbeFailureCodes.CapacityUnavailable or CapacityProbeFailureCodes.TargetNotFound
                or CapacityProbeFailureCodes.InvalidConfiguration or CapacityProbeFailureCodes.AuthenticationFailed or CapacityProbeFailureCodes.ConnectionFailed;
}
