namespace BackupPlatform.ArchitectureTests;

/// <summary>
/// Only these files may open the unfiltered platform-operations context. The list is a ratchet: it may shrink as stores are
/// split into tenant-scoped and platform parts, and growing it needs a reviewed reason (tenant code uses the filtered context).
/// </summary>
public sealed class PlatformOperationsContextTests
{
    private static readonly string[] Allowed =
    [
        "core/infrastructure/Agents/PostgreSqlAgentCapabilityStore.cs",
        "core/infrastructure/Agents/PostgreSqlAgentConnectivityStore.cs",
        "core/infrastructure/Agents/PostgreSqlAgentEnrollmentStore.cs",
        "core/infrastructure/Agents/PostgreSqlAgentRegistryStore.cs",
        "core/infrastructure/Agents/PostgreSqlPlatformAgentSettingsStore.cs",
        "core/infrastructure/Agents/PostgreSqlPlatformAgentStore.cs",
        "core/infrastructure/Backups/BackupTransferLease.cs",
        "core/infrastructure/Backups/PostgreSqlAgentReplicaDeletionStore.cs",
        "core/infrastructure/Backups/PostgreSqlBackupHealthPlatformStore.cs",
        "core/infrastructure/Backups/PostgreSqlBackupRetentionPlatformStore.cs",
        "core/infrastructure/Backups/PostgreSqlBackupRunPlatformStore.cs",
        "core/infrastructure/Backups/PostgreSqlBackupSchedulePlatformStore.cs",
        "core/infrastructure/Backups/PostgreSqlBackupSharePlatformStore.cs",
        "core/infrastructure/Backups/PostgreSqlJobCredentialClaims.cs",
        "core/infrastructure/Backups/PostgreSqlReplicaMissingReporter.cs",
        "core/infrastructure/Backups/PostgreSqlReplicaRepairPlatformStore.cs",
        "core/infrastructure/Backups/PostgreSqlTenantRecoveryKeyPlatformStore.cs",
        "core/infrastructure/Backups/PostgreSqlTenantWrapKeyPlatformStore.cs",
        "core/infrastructure/Backups/PostgreSqlUploadBandwidthPlatformStore.cs",
        "core/infrastructure/Discovery/PostgreSqlDiscoveryPlatformStore.cs",
        "core/infrastructure/Downloads/PostgreSqlDownloadPlatformStore.cs",
        "core/infrastructure/Identity/PostgreSqlIdentityStore.cs",
        "core/infrastructure/Identity/PostgreSqlUserAccessStore.cs",
        "core/infrastructure/Identity/UserBootstrapper.cs",
        "core/infrastructure/ManagedService/PostgreSqlBackupAdmissionPolicy.cs",
        "core/infrastructure/ManagedService/PostgreSqlManagedServiceStore.cs",
        "core/infrastructure/ManagedService/PostgreSqlQuotaPolicyStore.cs",
        "core/infrastructure/ManagedService/PostgreSqlStorageUsageService.cs",
        "core/infrastructure/ManagedService/PostgreSqlTenantEntitlementReader.cs",
        "core/infrastructure/Notifications/PostgreSqlNotificationObservationSource.cs",
        "core/infrastructure/Notifications/PostgreSqlNotificationStore.cs",
        "core/infrastructure/Persistence/PlatformDbContext.cs",
        "core/infrastructure/Persistence/PlatformOperationsDbContext.cs",
        "core/infrastructure/Platform/PostgreSqlPlatformAdministrationStore.cs",
        "core/infrastructure/Restores/PostgreSqlRestorePlatformStore.cs",
        "core/infrastructure/Scheduling/SchedulingServiceCollectionExtensions.cs",
        "core/infrastructure/Secrets/PostgreSqlSecretCiphertextRepository.cs",
        "core/infrastructure/Storage/PostgreSqlStorageCapacitySnapshotStore.cs",
        "core/infrastructure/Storage/PostgreSqlStorageCapacityTaskQueue.cs",
        "core/infrastructure/Storage/PostgreSqlStorageConnectionPlatformStore.cs",
        "core/infrastructure/Tenancy/PostgreSqlCentralAuthorizationService.cs",
        "hosts/api/Backups/EfGoogleDriveFileCatalog.cs",
        "hosts/api/Backups/GoogleDriveOAuthService.cs",
        "hosts/api/Backups/GoogleDriveRefreshSession.cs",
        "hosts/api/Backups/PostgreSqlGoogleDriveStagingStore.cs",
        "hosts/api/Databases/IdempotencyFingerprintKey.cs",
        "hosts/api/Downloads/ShareRevocationCancellationWorker.cs",
        "hosts/api/Endpoints/AgentLogIngestEndpoint.cs",
        "hosts/api/Endpoints/AgentLogRetentionWorker.cs",
        "hosts/api/Endpoints/AuditIntegrityEndpoint.cs",
        "hosts/api/Endpoints/UserNotificationEndpoints.cs",
        "hosts/api/Notifications/SecurityNotificationMailWorker.cs",
        "hosts/api/Push/InAppNotificationFanout.cs",
        "hosts/api/Security/LoginThrottle.cs",
        "hosts/api/Storage/StorageCapacityPeriodicProbeWorker.cs",
    ];

    [Fact]
    public void OnlyAllowListedFilesOpenTheUnfilteredPlatformContext()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src"));
        Assert.True(Directory.Exists(root), $"Source directory not found: {root}");
        var users = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && File.ReadAllText(file).Contains("PlatformOperationsDbContext", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();

        Assert.Empty(users.Except(Allowed));
        Assert.Empty(Allowed.Except(users));
    }
}
