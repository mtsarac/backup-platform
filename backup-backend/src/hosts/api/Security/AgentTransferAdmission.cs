using System.Threading.RateLimiting;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupPlatform.Api.Security;

/// <summary>Per-node admission before a relay holds a pooled session; DI owns both limiters and their idle-partition cleanup.</summary>
public sealed class AgentTransferAdmission : IDisposable
{
    private const int ConnectionHeadroom = 4;
    private readonly PartitionedRateLimiter<TenantId> _global;
    private readonly PartitionedRateLimiter<TenantId> _tenants;
    private readonly PartitionedRateLimiter<TenantId> _limiter;

    public AgentTransferAdmission(DbContextOptions<PlatformDbContext> options, IHostApplicationLifetime lifetime)
    {
        using var database = PlatformDbContext.CreateForPlatformOperations(options);
        var pool = new NpgsqlConnectionStringBuilder(database.Database.GetConnectionString());
        // Reserve connections for normal API traffic and the transfer's separate re-fence/intent transactions.
        GlobalSlots = Math.Clamp(pool.MaxPoolSize - ConnectionHeadroom, 0, 4);
        TenantSlots = Math.Min(2, GlobalSlots);
        Stopping = lifetime.ApplicationStopping;
        _global = PartitionedRateLimiter.Create<TenantId, string>(_ => RateLimitPartition.GetConcurrencyLimiter("node", _ =>
            new ConcurrencyLimiterOptions { PermitLimit = Math.Max(1, GlobalSlots), QueueLimit = 0 }));
        _tenants = PartitionedRateLimiter.Create<TenantId, TenantId>(tenant => RateLimitPartition.GetConcurrencyLimiter(tenant, _ =>
            new ConcurrencyLimiterOptions { PermitLimit = Math.Max(1, TenantSlots), QueueLimit = 0 }));
        _limiter = PartitionedRateLimiter.CreateChained(_global, _tenants);
    }

    public int GlobalSlots { get; }
    public int TenantSlots { get; }
    public CancellationToken Stopping { get; }

    public RateLimitLease? TryAcquire(TenantId tenant) => GlobalSlots == 0 || Stopping.IsCancellationRequested
        ? null : _limiter.AttemptAcquire(tenant);

    public void Dispose()
    {
        _limiter.Dispose();
        _global.Dispose();
        _tenants.Dispose();
    }
}
