using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Storage;

public interface IStorageCapacityProbeTrigger
{
    void EnqueueProbe(TenantId tenantId, Guid connectionId);
}
