using BackupPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BackupPlatform.Infrastructure.Persistence.Conversions;

internal sealed class UserIdConverter : ValueConverter<UserId, Guid>
{
    public UserIdConverter()
        : base(id => id.Value, value => new UserId(value))
    {
    }
}
