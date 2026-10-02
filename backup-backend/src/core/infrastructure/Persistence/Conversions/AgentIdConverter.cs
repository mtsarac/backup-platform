using BackupPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BackupPlatform.Infrastructure.Persistence.Conversions;

internal sealed class AgentIdConverter : ValueConverter<AgentId, Guid>
{
    public AgentIdConverter()
        : base(id => id.Value, value => new AgentId(value))
    {
    }
}
