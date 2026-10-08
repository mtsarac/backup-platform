using BackupPlatform.ManagedService.Subscriptions;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BackupPlatform.Infrastructure.Persistence.Conversions;

internal sealed class SubscriptionIdConverter : ValueConverter<SubscriptionId, Guid>
{
    public SubscriptionIdConverter()
        : base(id => id.Value, value => new SubscriptionId(value))
    {
    }
}
