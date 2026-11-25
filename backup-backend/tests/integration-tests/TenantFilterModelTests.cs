using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BackupPlatform.IntegrationTests;

public sealed class TenantFilterModelTests
{
    [Fact]
    public void EveryTenantOwnedEntityHasAQueryFilter()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options;
        using var context = new PlatformDbContext(options, new TenantContext());
        var entityTypes = context.GetService<IDesignTimeModel>().Model.GetEntityTypes()
            .Where(entity => typeof(ITenantOwned).IsAssignableFrom(entity.ClrType)).ToArray();

        Assert.NotEmpty(entityTypes);
        Assert.All(entityTypes, entity => Assert.True(entity.GetDeclaredQueryFilters().Any(),
            $"{entity.ClrType.FullName} has no tenant query filter."));
    }

    private sealed class TenantContext : ITenantContext
    {
        public TenantId? TenantId { get; } = TenantId.New();
    }
}
