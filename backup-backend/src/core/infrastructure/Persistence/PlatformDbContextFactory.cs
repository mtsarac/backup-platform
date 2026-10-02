using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BackupPlatform.Infrastructure.Persistence;

/// <summary>HTTP-independent EF tooling entry point; connection configuration is supplied externally.</summary>
/// <remarks>
/// Restore tooling with dotnet tool restore from backup-backend. Set BACKUP_PLATFORM_CONNECTION_STRING
/// before dotnet ef migrations add or database update --project src/core/infrastructure.
/// No credentials or fallback connection string are stored in source. Design-time scope is unresolved,
/// not platform-wide; migrations operate on schema independently of tenant query filters.
/// </remarks>
public sealed class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Set BACKUP_PLATFORM_CONNECTION_STRING externally before running EF tooling.");
        }
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(connectionString).Options;
        return new PlatformDbContext(options, new DesignTimeTenantContext());
    }

    private sealed class DesignTimeTenantContext : ITenantContext
    {
        public TenantId? TenantId => null;
    }
}
