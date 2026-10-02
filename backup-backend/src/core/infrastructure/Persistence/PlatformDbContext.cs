using System.Reflection;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Persistence;

/// <summary>PostgreSQL persistence with a tenant scope fixed for the context's entire lifetime.</summary>
/// <remarks>
/// Global filters protect ordinary LINQ queries, not raw SQL or IgnoreQueryFilters. This context
/// belongs to trusted infrastructure; it is not an authorization or database RLS boundary.
/// Do not expose it directly to modules or untrusted callers, and do not use context pooling.
/// SaveChanges guards tracked writes; bulk LINQ operations retain query filters but bypass those guards.
/// Capabilities are deliberately not persisted yet; writing agents with nonempty capabilities is rejected.
/// Mutable rows use PostgreSQL's system xmin as a shadow concurrency token. Tenant ownership also
/// participates in update/delete predicates, including writes of disconnected entities.
/// Global users remain tenant-independent; their future access rules belong to application authorization.
/// Created/updated timestamps are intentionally deferred until their domain or audit semantics are defined.
/// </remarks>
public sealed class PlatformDbContext : DbContext
{
    private readonly TenantId? _tenantId;
    private readonly bool _platformOperations;

    public PlatformDbContext(DbContextOptions<PlatformDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);
        _tenantId = tenantContext.TenantId;
    }

    private PlatformDbContext(DbContextOptions<PlatformDbContext> options) : base(options)
    {
        _platformOperations = true;
    }

    internal TenantId? CurrentTenantId => _tenantId;
    internal bool IsPlatformOperations => _platformOperations;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<PlatformAdmin> PlatformAdmins => Set<PlatformAdmin>();
    public DbSet<Agent> Agents => Set<Agent>();

    /// <summary>Explicit unfiltered context for trusted platform operations only.</summary>
    /// <remarks>Does not authorize the caller. Future application boundaries must authorize its use.</remarks>
    public static PlatformDbContext CreateForPlatformOperations(DbContextOptions<PlatformDbContext> options) => new(options);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PlatformDbContext).Assembly);
        modelBuilder.Entity<Tenant>().HasQueryFilter(tenant =>
            IsPlatformOperations || (CurrentTenantId != null && tenant.Id == CurrentTenantId));
        var configureFilter = typeof(PlatformDbContext).GetMethod(nameof(ConfigureTenantFilter), BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType))
            {
                configureFilter.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
            }
        }
    }

    private void ConfigureTenantFilter<TEntity>(ModelBuilder modelBuilder) where TEntity : class, ITenantOwned
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(entity =>
            IsPlatformOperations || (CurrentTenantId != null && EF.Property<TenantId>(entity, nameof(ITenantOwned.TenantId)) == CurrentTenantId));
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateTrackedWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidateTrackedWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ValidateTrackedWrites()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }
            if (entry.Entity is Agent agent && agent.Capabilities.Count != 0 && entry.State != EntityState.Deleted)
            {
                throw new InvalidOperationException("Capability persistence is not available; nonempty capabilities cannot be saved.");
            }
            if (entry.Entity is ITenantOwned owned)
            {
                var originalTenantId = entry.Property(nameof(ITenantOwned.TenantId)).OriginalValue;
                if (entry.State != EntityState.Added && !Equals(originalTenantId, owned.TenantId))
                {
                    throw new InvalidOperationException("Tenant ownership cannot be changed.");
                }
                if (!IsPlatformOperations && (CurrentTenantId is null || owned.TenantId != CurrentTenantId))
                {
                    throw new InvalidOperationException("A matching trusted tenant context is required for tenant-owned writes.");
                }
            }
            if (!IsPlatformOperations && entry.Entity is Tenant tenant && (CurrentTenantId is null || tenant.Id != CurrentTenantId))
            {
                throw new InvalidOperationException("A matching trusted tenant context is required for tenant writes.");
            }
            if (!IsPlatformOperations && entry.Entity is PlatformAdmin)
            {
                throw new InvalidOperationException("Platform grants require explicit platform operations.");
            }
        }
    }
}
