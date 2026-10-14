using System.Reflection;
using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Agents.Credentials;
using BackupPlatform.Agents.Enrollment;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Support;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.ManagedService;
using BackupPlatform.ManagedService.Plans;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.ManagedService.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Persistence;

/// <summary>PostgreSQL persistence with a tenant scope fixed for the context's entire lifetime.</summary>
/// <remarks>
/// Global filters protect ordinary LINQ queries, not raw SQL or IgnoreQueryFilters. This context
/// belongs to trusted infrastructure; it is not an authorization or database RLS boundary.
/// Do not expose it directly to modules or untrusted callers, and do not use context pooling.
/// SaveChanges guards tracked writes; bulk LINQ operations retain query filters but bypass those guards.
/// Agent.Capabilities is not persisted with the agent row; writing agents with nonempty capabilities is rejected. Reported
/// capability snapshots live in AgentCapabilityRecord rows, replaced as a whole by the capability store.
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
    public DbSet<TenantAgentEnrollmentPolicy> TenantAgentEnrollmentPolicies => Set<TenantAgentEnrollmentPolicy>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<AgentCredential> AgentCredentials => Set<AgentCredential>();
    public DbSet<AgentConnectivityRecord> AgentConnectivity => Set<AgentConnectivityRecord>();
    public DbSet<AgentCapabilityRecord> AgentCapabilities => Set<AgentCapabilityRecord>();
    public DbSet<PasswordCredential> PasswordCredentials => Set<PasswordCredential>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<BackupJob> BackupJobs => Set<BackupJob>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
    public DbSet<BackupRunAttempt> BackupRunAttempts => Set<BackupRunAttempt>();
    public DbSet<BackupArtifact> BackupArtifacts => Set<BackupArtifact>();
    public DbSet<BackupReplica> BackupReplicas => Set<BackupReplica>();
    public DbSet<BackupDestinationOutcome> BackupDestinationOutcomes => Set<BackupDestinationOutcome>();
    public DbSet<ReplicaRepairOperation> ReplicaRepairOperations => Set<ReplicaRepairOperation>();
    public DbSet<BackupLifecycleFact> BackupLifecycleFacts => Set<BackupLifecycleFact>();
    public DbSet<BackupScheduleOccurrence> BackupScheduleOccurrences => Set<BackupScheduleOccurrence>();
    public DbSet<RestoreOperation> RestoreOperations => Set<RestoreOperation>();
    public DbSet<RestoreAttempt> RestoreAttempts => Set<RestoreAttempt>();
    public DbSet<RestoreReplicaPin> RestoreReplicaPins => Set<RestoreReplicaPin>();
    public DbSet<DownloadSession> DownloadSessions => Set<DownloadSession>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<SupportAccessSession> SupportAccessSessions => Set<SupportAccessSession>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<TenantSubscription> TenantSubscriptions => Set<TenantSubscription>();
    public DbSet<QuotaPolicy> QuotaPolicies => Set<QuotaPolicy>();
    public DbSet<TenantStorageUsage> TenantStorageUsages => Set<TenantStorageUsage>();
    public DbSet<QuotaReservation> QuotaReservations => Set<QuotaReservation>();
    public DbSet<StorageConnection> StorageConnections => Set<StorageConnection>();
    public DbSet<GoogleDriveOAuthState> GoogleDriveOAuthStates => Set<GoogleDriveOAuthState>();
    public DbSet<GoogleDriveFileMetadata> GoogleDriveFileMetadata => Set<GoogleDriveFileMetadata>();
    public DbSet<GoogleDriveStagingReservation> GoogleDriveStagingReservations => Set<GoogleDriveStagingReservation>();
    public DbSet<BackupStorageCleanup> BackupStorageCleanups => Set<BackupStorageCleanup>();
    public DbSet<UploadBandwidthLimit> UploadBandwidthLimits => Set<UploadBandwidthLimit>();
    public DbSet<UploadBandwidthGrant> UploadBandwidthGrants => Set<UploadBandwidthGrant>();

    /// <summary>Explicit unfiltered context for trusted platform operations only.</summary>
    /// <remarks>Does not authorize the caller. Future application boundaries must authorize its use.</remarks>
    public static PlatformDbContext CreateForPlatformOperations(DbContextOptions<PlatformDbContext> options) => new(options);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PlatformDbContext).Assembly);
        modelBuilder.Entity<Tenant>().HasQueryFilter(tenant =>
            IsPlatformOperations || (CurrentTenantId != null && tenant.Id == CurrentTenantId));
        modelBuilder.Entity<AuditEvent>().HasQueryFilter(audit =>
            IsPlatformOperations || (CurrentTenantId != null && audit.TenantId == CurrentTenantId));
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
        if (StorageUsageProjection.Capture(this) is not null)
        {
            throw new InvalidOperationException("Replica and run lifecycle writes keep the storage usage projection current and must use SaveChangesAsync.");
        }
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <summary>
    /// A save that changes what a tenant stores (a replica published, changing status or removed) or ends a run that may hold a reservation also
    /// updates the tenant's usage projection, in the same transaction and under the tenant lock (taken after the rows are written), from the
    /// before/after of exactly those rows. It is idempotent by construction: the adjustment is the difference between the saved state and the same
    /// state with this save reverted, so a save that changes nothing adjusts nothing. Every other save is untouched.
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ValidateTrackedWrites();
        var projection = StorageUsageProjection.Capture(this);
        if (projection is null)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        await using var own = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(cancellationToken) : null;
        var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        await projection.ApplyAsync(this, cancellationToken);
        if (own is not null)
        {
            await own.CommitAsync(cancellationToken);
        }
        return saved;
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
                throw new InvalidOperationException("Agent.Capabilities is not persisted; nonempty capabilities cannot be saved with an agent (use the capability snapshot store).");
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
            if (!IsPlatformOperations && entry.Entity is PasswordCredential or UserSession)
            {
                throw new InvalidOperationException("Authentication state writes require explicit identity infrastructure operations.");
            }
            if (!IsPlatformOperations && entry.Entity is TenantAgentEnrollmentPolicy or AgentCredential or AgentConnectivityRecord or AgentCapabilityRecord)
            {
                throw new InvalidOperationException("Enrollment policy, agent credential, connectivity and capability writes require explicit platform operations.");
            }
            if (!IsPlatformOperations && entry.Entity is Plan or TenantSubscription or QuotaPolicy or TenantStorageUsage or QuotaReservation)
            {
                throw new InvalidOperationException("Plan, subscription and quota writes require explicit platform operations.");
            }
            if (entry.Entity is BackupReplica { StorageConnectionId: var connectionId } && connectionId == Guid.Empty && entry.State == EntityState.Added)
            {
                throw new InvalidOperationException("A replica must be assigned to a storage connection before it is saved.");
            }
            if (!IsPlatformOperations && entry.Entity is StorageConnection)
            {
                throw new InvalidOperationException("Storage connection writes require explicit platform operations.");
            }
            if (!IsPlatformOperations && entry.Entity is SupportAccessSession)
            {
                throw new InvalidOperationException("Support access sessions require explicit platform operations.");
            }
            if (!IsPlatformOperations && entry.Entity is AuditEvent)
            {
                throw new InvalidOperationException("Audit events require explicit platform operations.");
            }
            if (entry.Entity is AuditEvent && entry.State != EntityState.Added)
            {
                throw new InvalidOperationException("Audit events are append-only.");
            }
        }
    }
}
