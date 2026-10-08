using BackupPlatform.ManagedService.Quota;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BackupPlatform.Infrastructure.Persistence.Conversions;

internal sealed class QuotaPolicyIdConverter : ValueConverter<QuotaPolicyId, Guid>
{
    public QuotaPolicyIdConverter()
        : base(id => id.Value, value => new QuotaPolicyId(value))
    {
    }
}
