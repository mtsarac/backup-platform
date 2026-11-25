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

    private static readonly string[] CompanionAllowed =
    [
        "core/infrastructure/Backups/BackupShareQueries.cs",
        "core/infrastructure/Backups/PostgreSqlBackupShareStore.cs",
        "core/infrastructure/Backups/ReplicaRepairStoreCore.cs",
        "core/infrastructure/Discovery/DiscoveryStoreCore.cs",
        "core/infrastructure/Persistence/PlatformDbContext.cs",
        "core/infrastructure/Storage/StorageConnectionStoreCore.cs",
    ];

    private static bool UsesCompanion(string source) =>
        source.Contains("PlatformCompanion", StringComparison.Ordinal)
        || source.Contains("PlatformSide", StringComparison.Ordinal);

    private static bool IsUnauthorizedCompanionAccess(string file, string source) =>
        UsesCompanion(source) && !CompanionAllowed.Contains(file, StringComparer.Ordinal);

    [Fact]
    public void OnlyAllowListedFilesAccessThePlatformCompanion()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src"));
        Assert.True(Directory.Exists(root), $"Source directory not found: {root}");
        var users = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(file => (File: Path.GetRelativePath(root, file).Replace('\\', '/'), Source: File.ReadAllText(file)))
            .Where(item => UsesCompanion(item.Source)).ToArray();

        Assert.Empty(users.Where(item => IsUnauthorizedCompanionAccess(item.File, item.Source)).Select(item => item.File));
        Assert.Empty(CompanionAllowed.Except(users.Select(item => item.File)));
    }

    [Theory]
    [InlineData("var platform = context.PlatformCompanion();")]
    [InlineData("var platform = context.PlatformSide();")]
    public void CompanionAccessIsDetectedWithoutNamingThePlatformContext(string source)
    {
        Assert.True(IsUnauthorizedCompanionAccess("core/infrastructure/Backups/TenantStore.cs", source));
    }

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
