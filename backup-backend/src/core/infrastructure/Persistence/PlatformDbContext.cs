using Microsoft.EntityFrameworkCore.Storage;
using System.Reflection;
using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Agents.Credentials;
using BackupPlatform.Agents.Enrollment;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Discovery;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Lists;
using BackupPlatform.Infrastructure.ManagedService;
using BackupPlatform.ManagedService.Plans;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.ManagedService.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Persistence;

/// <summary>PostgreSQL persistence with a tenant scope fixed for the context's entire lifetime. This class is the filtered tenant context; platform-wide work uses <see cref="PlatformOperationsDbContext"/>.</summary>
/// <remarks>
/// Global filters protect ordinary LINQ queries, not raw SQL or IgnoreQueryFilters. This context
/// belongs to trusted infrastructure; it is not an authorization or database RLS boundary.
/// Audit rows of a tenant operation go through <see cref="RecordAudit"/>: the filtered context never tracks them (ValidateTrackedWrites rejects them),
/// a platform-operations companion on the same connection and transaction writes them, so they commit or roll back with the tenant's own changes.
/// Do not expose it directly to modules or untrusted callers, and do not use context pooling.
/// SaveChanges guards tracked writes; bulk LINQ operations retain query filters but bypass those guards.
/// Agent.Capabilities is not persisted with the agent row; writing agents with nonempty capabilities is rejected. Reported
/// capability snapshots live in AgentCapabilityRecord rows, replaced as a whole by the capability store.
/// Mutable rows use PostgreSQL's system xmin as a shadow concurrency token. Tenant ownership also
/// participates in update/delete predicates, including writes of disconnected entities.
/// Global users remain tenant-independent; their future access rules belong to application authorization.
/// Created/updated timestamps are intentionally deferred until their domain or audit semantics are defined.
/// </remarks>
public class PlatformDbContext : DbContext
{
    private readonly DbContextOptions<PlatformDbContext> _options;
    private readonly TenantId? _tenantId;
    private readonly bool _platformOperations;
    private List<AuditEvent>? _deferredAudit;
    private PlatformOperationsDbContext? _companion;

    public PlatformDbContext(DbContextOptions<PlatformDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(tenantContext);
        _options = options;
        _tenantId = tenantContext.TenantId;
    }

    private protected PlatformDbContext(DbContextOptions<PlatformDbContext> options) : base(options)
    {
        _options = options;
        _platformOperations = true;
    }

    /// <summary>The filtered context of a tenant request. Unresolved tenants and a tenant other than <paramref name="expected"/> are programming errors, not empty results.</summary>
    public static PlatformDbContext ForTenant(DbContextOptions<PlatformDbContext> options, ITenantContext tenant, TenantId? expected = null)
    {
        if (tenant.TenantId is not { } id)
        {
            throw new InvalidOperationException("Tenant data needs a resolved tenant; platform and agent work belongs to the platform-operations stores.");
        }
        if (expected is { } other && other != id)
        {
            throw new InvalidOperationException("The operation names a tenant other than the one of the request.");
        }
        return new PlatformDbContext(options, tenant);
    }

    internal TenantId? CurrentTenantId => _tenantId;
    internal bool IsPlatformOperations => _platformOperations;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<TenantAgentEnrollmentPolicy> TenantAgentEnrollmentPolicies => Set<TenantAgentEnrollmentPolicy>();
    public DbSet<PlatformAgentSettings> PlatformAgentSettings => Set<PlatformAgentSettings>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<AgentCredential> AgentCredentials => Set<AgentCredential>();
    public DbSet<AgentConnectivityRecord> AgentConnectivity => Set<AgentConnectivityRecord>();
    public DbSet<AgentCapabilityRecord> AgentCapabilities => Set<AgentCapabilityRecord>();
    public DbSet<PasswordCredential> PasswordCredentials => Set<PasswordCredential>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<KnownSignIn> KnownSignIns => Set<KnownSignIn>();
    public DbSet<AccountEmailToken> AccountEmailTokens => Set<AccountEmailToken>();
    public DbSet<LoginThrottleEntry> LoginThrottleEntries => Set<LoginThrottleEntry>();
    public DbSet<TotpCredential> TotpCredentials => Set<TotpCredential>();
    public DbSet<RecoveryCode> RecoveryCodes => Set<RecoveryCode>();
    public DbSet<PasskeyCredential> PasskeyCredentials => Set<PasskeyCredential>();
    public DbSet<PasskeyChallenge> PasskeyChallenges => Set<PasskeyChallenge>();
    public DbSet<BackupJob> BackupJobs => Set<BackupJob>();
    public DbSet<BackupRun> BackupRuns => Set<BackupRun>();
    public DbSet<BackupRunAttempt> BackupRunAttempts => Set<BackupRunAttempt>();
    public DbSet<BackupArtifact> BackupArtifacts => Set<BackupArtifact>();
    public DbSet<BackupReplica> BackupReplicas => Set<BackupReplica>();
    public DbSet<BackupDestinationOutcome> BackupDestinationOutcomes => Set<BackupDestinationOutcome>();
    public DbSet<ReplicaRepairOperation> ReplicaRepairOperations => Set<ReplicaRepairOperation>();
    public DbSet<TenantWrapKey> TenantWrapKeys => Set<TenantWrapKey>();
    public DbSet<TenantRecoveryKey> TenantRecoveryKeys => Set<TenantRecoveryKey>();
    public DbSet<BackupLifecycleFact> BackupLifecycleFacts => Set<BackupLifecycleFact>();
    public DbSet<BackupScheduleOccurrence> BackupScheduleOccurrences => Set<BackupScheduleOccurrence>();
    public DbSet<RestoreOperation> RestoreOperations => Set<RestoreOperation>();
    public DbSet<RestoreAttempt> RestoreAttempts => Set<RestoreAttempt>();
    public DbSet<RestoreReplicaPin> RestoreReplicaPins => Set<RestoreReplicaPin>();
    public DbSet<DownloadSession> DownloadSessions => Set<DownloadSession>();
    public DbSet<BackupShare> BackupShares => Set<BackupShare>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<PlatformAuditRetentionSettings> PlatformAuditRetentionSettings => Set<PlatformAuditRetentionSettings>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<TenantSubscription> TenantSubscriptions => Set<TenantSubscription>();
    public DbSet<QuotaPolicy> QuotaPolicies => Set<QuotaPolicy>();
    public DbSet<TenantStorageUsage> TenantStorageUsages => Set<TenantStorageUsage>();
    public DbSet<QuotaReservation> QuotaReservations => Set<QuotaReservation>();
    public DbSet<StorageConnection> StorageConnections => Set<StorageConnection>();
    public DbSet<StorageCapacitySnapshot> StorageCapacitySnapshots => Set<StorageCapacitySnapshot>();
    public DbSet<StorageCapacityTask> StorageCapacityTasks => Set<StorageCapacityTask>();
    public DbSet<GoogleDriveOAuthState> GoogleDriveOAuthStates => Set<GoogleDriveOAuthState>();
    public DbSet<GoogleDriveFileMetadata> GoogleDriveFileMetadata => Set<GoogleDriveFileMetadata>();
    public DbSet<GoogleDriveStagingReservation> GoogleDriveStagingReservations => Set<GoogleDriveStagingReservation>();
    public DbSet<BackupStorageCleanup> BackupStorageCleanups => Set<BackupStorageCleanup>();
    public DbSet<UploadBandwidthLimit> UploadBandwidthLimits => Set<UploadBandwidthLimit>();
    public DbSet<UploadBandwidthGrant> UploadBandwidthGrants => Set<UploadBandwidthGrant>();
    public DbSet<BandwidthProfile> BandwidthProfiles => Set<BandwidthProfile>();
    public DbSet<TransferScheduleWindow> TransferScheduleWindows => Set<TransferScheduleWindow>();
    public DbSet<BandwidthProfileAssignment> BandwidthProfileAssignments => Set<BandwidthProfileAssignment>();
    public DbSet<BackupTransferLease> BackupTransferLeases => Set<BackupTransferLease>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<NotificationPolicy> NotificationPolicies => Set<NotificationPolicy>();
    public DbSet<NotificationCondition> NotificationConditions => Set<NotificationCondition>();
    public DbSet<NotificationEvent> NotificationEvents => Set<NotificationEvent>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();
    public DbSet<NotificationTransportSettings> NotificationTransports => Set<NotificationTransportSettings>();
    public DbSet<DiscoveryAgentState> DiscoveryAgentStates => Set<DiscoveryAgentState>();
    public DbSet<DiscoveredInstance> DiscoveredInstances => Set<DiscoveredInstance>();
    public DbSet<DiscoveredDatabase> DiscoveredDatabases => Set<DiscoveredDatabase>();

    /// <summary>
    /// Platform-owned rows a tenant operation writes alongside its own (notifications, usage). The companion shares this context's connection and open transaction,
    /// and <see cref="SaveChangesAsync(bool, CancellationToken)"/> saves it after this context's own changes, so both commit or roll back together.
    /// </summary>
    public PlatformOperationsDbContext PlatformCompanion()
    {
        if (_platformOperations || CurrentTenantId is null)
        {
            throw new InvalidOperationException("Only a tenant context has a platform-operations companion.");
        }
        var transaction = Database.CurrentTransaction
            ?? throw new InvalidOperationException("The platform-operations companion joins the business transaction, which must be open.");
        if (_companion is null)
        {
            _companion = PlatformOperationsDbContext.CreateSharing(_options, Database.GetDbConnection());
            _companion.Database.UseTransaction(transaction.GetDbTransaction());
        }
        return _companion;
    }

    /// <summary>The platform side of this operation: itself when it is a platform-operations context, else its companion. Platform-owned rows (notifications, storage connections, discovery inventory) and platform-wide reads (capacity across tenants) go through it.</summary>
    internal PlatformDbContext PlatformSide() => _platformOperations ? this : PlatformCompanion();

    /// <summary>
    /// Records an audit row for the operation this context performs. A platform-operations context tracks it like any entity; a tenant context
    /// keeps it for <see cref="SaveChangesAsync(bool, CancellationToken)"/>, which writes it through a platform-operations companion in the same transaction.
    /// </summary>
    public void RecordAudit(AuditEvent auditEvent)
    {
        if (_platformOperations)
        {
            AuditEvents.Add(auditEvent);
            return;
        }
        if (CurrentTenantId is null || auditEvent.TenantId != CurrentTenantId)
        {
            throw new InvalidOperationException("A tenant context records audit events of its own tenant only.");
        }
        (_deferredAudit ??= []).Add(auditEvent);
    }

    public IQueryable<UserId> TenantAdminIds(TenantId? tenantId) =>
        from membership in TenantMemberships
        join user in Users on membership.UserId equals user.Id
        where membership.TenantId == tenantId && user.Role == UserRole.UserAdmin
        select user.Id;

    public IQueryable<UserNotification> VisibleUserNotifications(UserId userId) =>
        from item in UserNotifications
        join notificationEvent in NotificationEvents on item.EventId equals notificationEvent.Id
        where item.UserId == userId && (notificationEvent.TenantId == null
            || Users.Any(user => user.Id == userId && user.Role == UserRole.PlatformAdmin)
            || TenantMemberships.Any(membership => membership.UserId == userId && membership.TenantId == notificationEvent.TenantId))
        select item;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PlatformDbContext).Assembly);
        TextFold.RegisterWith(modelBuilder);
        modelBuilder.Entity<Tenant>().HasQueryFilter(tenant =>
            IsPlatformOperations || (CurrentTenantId != null && tenant.Id == CurrentTenantId));
        modelBuilder.Entity<AuditEvent>().HasQueryFilter(audit =>
            IsPlatformOperations || (CurrentTenantId != null && audit.TenantId == CurrentTenantId));
        modelBuilder.Entity<NotificationEvent>().HasQueryFilter(notification =>
            IsPlatformOperations || (CurrentTenantId != null && notification.TenantId == CurrentTenantId));
        modelBuilder.Entity<UserNotification>().HasQueryFilter(_ => IsPlatformOperations);
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

    public override void Dispose()
    {
        _companion?.Dispose();
        base.Dispose();
    }

    public override async ValueTask DisposeAsync()
    {
        if (_companion is not null)
        {
            await _companion.DisposeAsync();
        }
        await base.DisposeAsync();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ValidateTrackedWrites();
        if (StorageUsageProjection.Capture(this) is not null || _deferredAudit is { Count: > 0 } || _companion is not null && _companion.ChangeTracker.HasChanges())
        {
            throw new InvalidOperationException("Replica and run lifecycle writes keep the storage usage projection current and tenant audit rows are written beside the save; both must use SaveChangesAsync.");
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
        var audit = _deferredAudit is { Count: > 0 } ? _deferredAudit : null;
        var companionChanged = _companion is not null && _companion.ChangeTracker.HasChanges();
        if (projection is null && audit is null && !companionChanged)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        await using var own = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(cancellationToken) : null;
        var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (projection is not null)
        {
            await projection.ApplyAsync(this, cancellationToken);
        }
        if (audit is not null)
        {
            PlatformCompanion().AuditEvents.AddRange(audit);
            audit.Clear();
        }
        if (_companion is not null)
        {
            await _companion.SaveChangesAsync(cancellationToken);
        }
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
            if (!IsPlatformOperations && entry.Entity is PasswordCredential or UserSession or TotpCredential or RecoveryCode)
            {
                throw new InvalidOperationException("Authentication state writes require explicit identity infrastructure operations.");
            }
            if (!IsPlatformOperations && (entry.Entity is TenantAgentEnrollmentPolicy or AgentCredential or AgentConnectivityRecord or AgentCapabilityRecord || entry.Entity is BackupPlatform.Agents.Enrollment.PlatformAgentSettings))
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
            if (!IsPlatformOperations && entry.Entity is NotificationChannel or NotificationPolicy or NotificationCondition or NotificationEvent or NotificationDelivery or NotificationTransportSettings or UserNotification or BackupPlatform.Infrastructure.Persistence.Configurations.NotificationHistorySettings)
            {
                throw new InvalidOperationException("Notification writes require explicit platform operations.");
            }
            if (!IsPlatformOperations && entry.Entity is DiscoveryAgentState or DiscoveredInstance or DiscoveredDatabase)
            {
                throw new InvalidOperationException("Discovery inventory writes require explicit platform operations.");
            }
            if (!IsPlatformOperations && (entry.Entity is AuditEvent || entry.Entity is BackupPlatform.Domain.Audit.PlatformAuditRetentionSettings))
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
