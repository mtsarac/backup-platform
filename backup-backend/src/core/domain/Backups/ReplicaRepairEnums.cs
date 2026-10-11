namespace BackupPlatform.Domain.Backups;

public enum ReplicaRepairStatus
{
    Pending = 1,
    InProgress = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelled = 5
}
