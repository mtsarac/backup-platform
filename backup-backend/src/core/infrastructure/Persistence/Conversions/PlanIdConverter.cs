using BackupPlatform.ManagedService.Plans;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BackupPlatform.Infrastructure.Persistence.Conversions;

internal sealed class PlanIdConverter : ValueConverter<PlanId, Guid>
{
    public PlanIdConverter()
        : base(id => id.Value, value => new PlanId(value))
    {
    }
}
