using BackupPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BackupPlatform.Infrastructure.Persistence.Conversions;

internal sealed class TenantIdConverter : ValueConverter<TenantId, Guid>
{
    public TenantIdConverter()
        : base(id => id.Value, value => new TenantId(value))
    {
    }
}
