using BackupPlatform.Domain.Identity;
using BackupPlatform.Notifications;
using BackupPlatform.Storage;

namespace BackupPlatform.Api.Storage;

public sealed class NotificationEngineCapacitySink(NotificationEngine engine) : IStorageCapacityNotificationSink
{
    public Task NotifyCapacityEvaluatedAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        engine.SweepTenantAsync(tenantId, cancellationToken);
}
