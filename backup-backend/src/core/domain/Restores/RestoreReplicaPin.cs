using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Restores;

/// <summary>
/// A restore's hold on the replica it reads. While an unreleased pin exists, retention may still move the replica to DeletePending but no
/// delete worker (backend or agent) may claim it for physical removal. The pin is created with the operation and released by the same
/// state change that ends it, so a terminal operation can never keep a replica alive and a live one can never lose it.
/// </summary>
public sealed class RestoreReplicaPin : ITenantOwned
{
    private RestoreReplicaPin() { TenantId = null!; }
    internal RestoreReplicaPin(Guid id, TenantId tenantId, Guid operationId, Guid replicaId, DateTimeOffset createdAt)
    {
        BackupGuard.Id(id);
        BackupGuard.Id(operationId);
        BackupGuard.Id(replicaId);
        Id = id;
        TenantId = tenantId;
        OperationId = operationId;
        ReplicaId = replicaId;
        CreatedAt = BackupGuard.Timestamp(createdAt);
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid ReplicaId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }
    public bool IsActive => ReleasedAt is null;

    internal void Release(DateTimeOffset now) => ReleasedAt ??= BackupGuard.Timestamp(now);

    internal void SwitchReplica(Guid newReplicaId)
    {
        if (ReleasedAt is not null) throw new InvalidOperationException("Cannot switch replica of released pin.");
        BackupGuard.Id(newReplicaId);
        ReplicaId = newReplicaId;
    }
}
