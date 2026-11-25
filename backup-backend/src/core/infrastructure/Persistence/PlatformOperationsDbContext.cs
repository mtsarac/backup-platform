using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Persistence;

/// <summary>
/// Unfiltered context for work that is platform-wide by nature: background workers, agent identity and heartbeat, authentication, platform administration.
/// Tenant request code uses the filtered <see cref="PlatformDbContext"/>; an architecture test pins the files that may name this type.
/// </summary>
/// <remarks>Does not authorize the caller. Callers authorize their use.</remarks>
public sealed class PlatformOperationsDbContext : PlatformDbContext
{
    private PlatformOperationsDbContext(DbContextOptions<PlatformDbContext> options) : base(options)
    {
    }

    public static PlatformOperationsDbContext Create(DbContextOptions<PlatformDbContext> options) => new(options);

    /// <summary>A companion that joins the connection (and then the transaction) of a context that already holds them.</summary>
    internal static PlatformOperationsDbContext CreateSharing(DbContextOptions<PlatformDbContext> options, DbConnection connection) =>
        new(new DbContextOptionsBuilder<PlatformDbContext>(options).UseNpgsql(connection).Options);
}
