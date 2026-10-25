using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Backups;

/// <summary>
/// Records that a read of a replica's stored object found it gone (not found, never "unreachable"), so the replica stops counting as a verified copy
/// (<see cref="BackupPlatform.Domain.Backups.BackupReplica.MarkMissing"/>) and a repair can rebuild its destination. Unknown, unverified or already
/// handled replicas are ignored.
/// </summary>
public interface IReplicaMissingReporter
{
    Task ReportMissingAsync(TenantId tenantId, Guid replicaId, CancellationToken cancellationToken);
}
