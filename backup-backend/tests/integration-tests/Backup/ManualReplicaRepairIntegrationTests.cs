using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.IntegrationTests.Quota;
using BackupPlatform.IntegrationTests.Storage.Connections;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.StorageProviders.Abstractions;
using Xunit;
using Completion = BackupPlatform.Application.Backups.BackupCompletion;
using static BackupPlatform.IntegrationTests.Quota.AdmissionTestKit;

namespace BackupPlatform.IntegrationTests.Backup;

public sealed class ManualReplicaRepairIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTimeOffset T0 = BackupStoreHarness.T0;
    private static readonly byte[] Content = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly string Checksum = Convert.ToHexString(SHA256.HashData(Content)).ToLowerInvariant();

    private readonly ConnectionWorld _connections = new(fixture);
    private QuotaWorld World => _connections.Quota;

    private sealed class InMemoryStorageProvider(string id) : IStorageProvider
    {
        public StorageProviderId Id { get; } = new(id);
        public StorageTopology Topology => StorageTopology.BackendRelayed;
        public StorageCapabilities Capabilities => new([StorageCapabilityIds.Read, StorageCapabilityIds.Delete]);

        public ConcurrentDictionary<string, byte[]> Storage { get; } = new();
        public ConcurrentBag<string> DeletedKeys { get; } = new();
        public bool ShouldFailRead { get; set; }
        public bool ShouldFailWrite { get; set; }
        public bool ShouldFailVerify { get; set; }

        public Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            if (ShouldFailWrite) throw new StorageException(StorageFailureKind.Io, "Simulated write failure");
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            var bytes = ms.ToArray();
            Storage[key.Value] = bytes;
            progress?.Report(bytes.Length);
            return Task.FromResult(new StorageObjectMetadata(new StorageLocation(Id, key), bytes.Length, new ContentChecksum(ChecksumAlgorithm.Sha256, Checksum), DateTimeOffset.UtcNow));
        }

        public Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            if (ShouldFailRead) throw new StorageException(StorageFailureKind.Io, "Simulated read failure");
            if (Storage.TryGetValue(key.Value, out var bytes))
                return Task.FromResult<Stream>(new MemoryStream(bytes));
            throw new StorageException(StorageFailureKind.NotFound, "Key not found");
        }

        public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken)
        {
            if (ShouldFailVerify)
                return Task.FromResult(new StorageVerificationResult(new StorageLocation(Id, key), StorageVerificationStatus.ChecksumMismatch, StorageVerificationMethod.ReadBack, expected.SizeBytes, expected.Checksum));

            if (Storage.TryGetValue(key.Value, out var bytes))
            {
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                var status = hash == expected.Checksum.Hex && bytes.Length == expected.SizeBytes
                    ? StorageVerificationStatus.Verified
                    : StorageVerificationStatus.ChecksumMismatch;
                return Task.FromResult(new StorageVerificationResult(new StorageLocation(Id, key), status, StorageVerificationMethod.ReadBack, bytes.Length, expected.Checksum));
            }
            return Task.FromResult(new StorageVerificationResult(new StorageLocation(Id, key), StorageVerificationStatus.Missing, StorageVerificationMethod.ReadBack, null, null));
        }

        public Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            DeletedKeys.Add(key.Value);
            return Task.FromResult(Storage.TryRemove(key.Value, out _));
        }

        public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken) =>
            Task.FromResult(Storage.ContainsKey(key.Value));

        public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken) =>
            Task.FromResult<StorageObjectMetadata?>(null);

        public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeResolver : IBackupStorageResolver
    {
        public ConcurrentDictionary<string, InMemoryStorageProvider> Providers { get; } = new();

        public IReadOnlyList<BackupManagedStorage> ManagedStorages => [];

        public IStorageProvider? Resolve(TenantId tenantId, string storageProvider, string storageTarget)
        {
            var key = $"{storageProvider}:{storageTarget}";
            return Providers.GetOrAdd(key, _ => new InMemoryStorageProvider(storageProvider));
        }
    }

    private sealed record MultiSetup(
        StoreAgent Agent,
        BackupJob Job,
        StorageConnection Central,
        StorageConnection Sftp,
        StorageConnection Drive,
        BackupArtifact Artifact,
        FakeResolver Resolver,
        PostgreSqlReplicaRepairStore RepairStore,
        ReplicaRepairService RepairService);

    private async Task<MultiSetup> SetupAsync(QuotaAccountingMode mode = QuotaAccountingMode.LogicalArtifactBytes, ISet<string>? verified = null)
    {
        var policy = await World.CreatePolicyAsync(mode, QuotaScopeRule.All, QuotaEnforcementMode.WarnOnly);
        var agent = await World.AgentAsync(100 * QuotaWorld.Gb, policy.Key);
        var (_, central) = await _connections.DefaultsAsync(agent.TenantId);
        var sftp = await _connections.AddAsync(agent.TenantId, "sftp-1", "sftp");
        var drive = await _connections.AddAsync(agent.TenantId, "drive-1", "google-drive");

        var configuration = BackupStoreHarness.Configuration() with
        {
            StorageProvider = "central",
            StorageConnectionId = central.Id,
            StorageExecutionLocation = BackupStorageExecutionLocation.Backend,
            Destinations =
            [
                new BackupDestination("central", central.Id, true),
                new BackupDestination("sftp", sftp.Id),
                new BackupDestination("google-drive", drive.Id)
            ],
            RequiredVerifiedReplicaCount = 2
        };

        var job = new BackupJob(Guid.NewGuid(), agent.TenantId, "Replicated", agent.AgentId, configuration, T0, null, BackupOverlapPolicy.Queue);
        job = (await World.RunStore().CreateJobAsync(job, default))!;

        await using (var context = fixture.CreatePlatformContext())
        {
            context.AgentCapabilities.Add(new BackupPlatform.Agents.Capabilities.AgentCapabilityRecord(
                agent.AgentId, agent.TenantId, BackupTaskProtocol.MultiDestinationCapability,
                BackupPlatform.Domain.Capabilities.CapabilitySupport.Supported, null, "rev", T0));
            await context.SaveChangesAsync();
        }

        var store = World.RunStore();
        var started = await store.StartRunAsync(agent.TenantId, job.Id, $"key-{Guid.NewGuid():N}", T0, default);
        var claim = (await BackupStoreHarness.ClaimAsync(store, agent, T0))!;
        await BackupStoreHarness.StartAttemptAsync(store, claim, T0);

        verified ??= new HashSet<string> { "central", "google-drive" };

        string KeyFor(string provider, StorageConnection conn) =>
            $"{ManagedBackupLayout.AttemptScope(agent.TenantId.Value, job.Id, started.Run!.Id, claim.Attempt.Id)}/db_{provider}_FULL.bak";

        var replicaResults = new List<BackupReplicaResult>();
        var destReports = new List<BackupDestinationReport>();

        foreach (var (provider, conn) in new[] { ("central", central), ("sftp", sftp), ("google-drive", drive) })
        {
            var isVer = verified.Contains(provider);
            var key = KeyFor(provider, conn);
            var target = provider == "central" ? "central" : conn.Id.ToString("D");

            if (isVer)
            {
                replicaResults.Add(new BackupReplicaResult(provider, target, key, Content.Length, Checksum, "sha256", "Verified", "ReadBack", T0, T0, conn.Id));
                destReports.Add(new BackupDestinationReport(provider, conn.Id, "Verified", "Succeeded", "Verified", null, key, false, T0, T0));
            }
            else
            {
                destReports.Add(new BackupDestinationReport(provider, conn.Id, "Failed", "Failed", "NotRun", "StorageUnavailable", null, false, T0, T0));
            }
        }

        var completion = new Completion("Succeeded", null,
            new BackupArtifactResult(Content.Length, Checksum, Checksum, "sha256", T0, "{}", "{}", replicaResults),
            destReports);

        await BackupStoreHarness.CompleteAsync(store, claim, completion, T0);

        await using var loadCtx = fixture.CreatePlatformContext();
        var artifact = await loadCtx.BackupArtifacts.Include(a => a.Replicas).SingleAsync(a => a.TenantId == agent.TenantId && a.AttemptId == claim.Attempt.Id);

        var resolver = new FakeResolver();
        foreach (var (provider, conn) in new[] { ("central", central), ("sftp", sftp), ("google-drive", drive) })
        {
            if (verified.Contains(provider))
            {
                var target = provider == "central" ? "central" : conn.Id.ToString("D");
                var p = (InMemoryStorageProvider)resolver.Resolve(agent.TenantId, provider, target)!;
                p.Storage[KeyFor(provider, conn)] = Content;
            }
        }

        var repairStore = new PostgreSqlReplicaRepairStore(World.Options());
        var repairService = new ReplicaRepairService(repairStore, resolver, TimeProvider.System, NullLogger<ReplicaRepairService>.Instance);

        return new MultiSetup(agent, job, central, sftp, drive, artifact, resolver, repairStore, repairService);
    }

    [Fact]
    public async Task Scenario1_Central_to_SFTP_repair_succeeds_and_updates_replication_health()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "google-drive" });
        var artifact = setup.Artifact;

        Assert.Equal(2, artifact.Replicas.Count);
        Assert.DoesNotContain(artifact.Replicas, r => r.StorageProvider == "sftp" && r.VerificationState == "Verified");

        var startResult = await setup.RepairService.StartRepairAsync(
            setup.Agent.TenantId,
            artifact.Id,
            new StartReplicaRepairRequest(StorageConnectionId: setup.Sftp.Id),
            null,
            default,
            executeInBackground: false);

        Assert.True(startResult.Success);
        Assert.NotNull(startResult.Operation);

        var op = await setup.RepairService.ExecuteRepairAsync(setup.Agent.TenantId, artifact.Id, startResult.Operation.Id);
        Assert.NotNull(op);
        Assert.True(op.Status == "Succeeded", $"Status: {op.Status}, Code: {op.FailureCode}, Reason: {op.FailureReason}");
        Assert.NotNull(op.TargetReplicaId);

        await using var context = fixture.CreatePlatformContext();
        var reloadedArtifact = await context.BackupArtifacts.Include(a => a.Replicas).SingleAsync(a => a.Id == artifact.Id);
        Assert.Equal(3, reloadedArtifact.Replicas.Count);
        var sftpReplica = Assert.Single(reloadedArtifact.Replicas, r => r.StorageProvider == "sftp");
        Assert.Equal("Verified", sftpReplica.VerificationState);
        Assert.Equal(setup.Sftp.Id, sftpReplica.StorageConnectionId);

        var health = BackupReplicationEvaluator.ForArtifact(2, 3, reloadedArtifact.Replicas);
        Assert.Equal(BackupReplicationStatuses.Healthy, health.Status);
        Assert.True(health.PolicySatisfied);
    }

    [Fact]
    public async Task Scenario2_SFTP_to_Central_repair_succeeds()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "sftp", "google-drive" });
        var artifact = setup.Artifact;

        Assert.Equal(2, artifact.Replicas.Count);
        Assert.DoesNotContain(artifact.Replicas, r => r.StorageProvider == "central" && r.VerificationState == "Verified");

        var startResult = await setup.RepairService.StartRepairAsync(
            setup.Agent.TenantId,
            artifact.Id,
            new StartReplicaRepairRequest(StorageConnectionId: setup.Central.Id),
            null,
            default,
            executeInBackground: false);

        Assert.True(startResult.Success);
        var op = await setup.RepairService.ExecuteRepairAsync(setup.Agent.TenantId, artifact.Id, startResult.Operation!.Id);
        Assert.Equal("Succeeded", op!.Status);

        await using var context = fixture.CreatePlatformContext();
        var reloaded = await context.BackupArtifacts.Include(a => a.Replicas).SingleAsync(a => a.Id == artifact.Id);
        Assert.Contains(reloaded.Replicas, r => r.StorageProvider == "central" && r.VerificationState == "Verified");
    }

    [Fact]
    public async Task Scenario3_Central_to_GoogleDrive_repair_succeeds()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "sftp" });
        var artifact = setup.Artifact;

        var startResult = await setup.RepairService.StartRepairAsync(
            setup.Agent.TenantId,
            artifact.Id,
            new StartReplicaRepairRequest(StorageConnectionId: setup.Drive.Id),
            null,
            default,
            executeInBackground: false);

        Assert.True(startResult.Success);
        var op = await setup.RepairService.ExecuteRepairAsync(setup.Agent.TenantId, artifact.Id, startResult.Operation!.Id);
        Assert.Equal("Succeeded", op!.Status);

        await using var context = fixture.CreatePlatformContext();
        var reloaded = await context.BackupArtifacts.Include(a => a.Replicas).SingleAsync(a => a.Id == artifact.Id);
        Assert.Contains(reloaded.Replicas, r => r.StorageProvider == "google-drive" && r.VerificationState == "Verified");
    }

    [Fact]
    public async Task Scenario4_GoogleDrive_to_Central_repair_succeeds()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "google-drive" });
        var artifact = setup.Artifact;

        var startResult = await setup.RepairService.StartRepairAsync(
            setup.Agent.TenantId,
            artifact.Id,
            new StartReplicaRepairRequest(StorageConnectionId: setup.Central.Id),
            null,
            default,
            executeInBackground: false);

        Assert.True(startResult.Success);
        var op = await setup.RepairService.ExecuteRepairAsync(setup.Agent.TenantId, artifact.Id, startResult.Operation!.Id);
        Assert.Equal("Succeeded", op!.Status);

        await using var context = fixture.CreatePlatformContext();
        var reloaded = await context.BackupArtifacts.Include(a => a.Replicas).SingleAsync(a => a.Id == artifact.Id);
        Assert.Contains(reloaded.Replicas, r => r.StorageProvider == "central" && r.VerificationState == "Verified");
    }

    [Fact]
    public async Task Scenario8_Source_retention_pinning_prevents_retention_worker_and_trigger_from_deleting_source()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central" });
        var artifact = setup.Artifact;
        var sourceReplica = artifact.Replicas.Single(r => r.StorageProvider == "central");

        var repairOp = new ReplicaRepairOperation(
            Guid.NewGuid(), setup.Agent.TenantId, artifact.Id, setup.Sftp.Id, "sftp", sourceReplica.Id, T0);
        repairOp.Claim(Guid.NewGuid(), "worker-1", T0, TimeSpan.FromMinutes(10));
        await setup.RepairStore.CreateOperationAsync(repairOp, default);

        await using var context = fixture.CreatePlatformContext();

        // 1. Database trigger trg_backup_replica_pin_guard prevents setting status to Deleting/Deleted
        var pgEx = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE backup_replicas SET \"Status\" = 'Deleting' WHERE \"Id\" = {sourceReplica.Id}"));
        Assert.Equal("23514", pgEx.SqlState);

        // 2. Retention store claim skips pinned replica
        var retentionStore = new PostgreSqlBackupRetentionStore(World.Options());
        var candidates = await retentionStore.ClaimDueDeletionsAsync("retention-node", T0.AddDays(40), TimeSpan.FromMinutes(10), 10, default);
        Assert.DoesNotContain(candidates, c => c.ReplicaId == sourceReplica.Id);
    }

    [Fact]
    public async Task Scenario9_Concurrent_repair_fencing_prevents_duplicate_active_repairs()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central" });
        var artifact = setup.Artifact;

        var first = await setup.RepairService.StartRepairAsync(
            setup.Agent.TenantId, artifact.Id, new StartReplicaRepairRequest(StorageConnectionId: setup.Sftp.Id), null, default, executeInBackground: false);
        Assert.True(first.Success);

        var second = await setup.RepairService.StartRepairAsync(
            setup.Agent.TenantId, artifact.Id, new StartReplicaRepairRequest(StorageConnectionId: setup.Sftp.Id), null, default, executeInBackground: false);
        Assert.False(second.Success);
        Assert.Equal("repair_already_in_progress", second.Code);

        // DB level uniqueness enforcement
        await using var context = fixture.CreatePlatformContext();
        var duplicateOp = new ReplicaRepairOperation(
            Guid.NewGuid(), setup.Agent.TenantId, artifact.Id, setup.Sftp.Id, "sftp", null, T0);
        context.ReplicaRepairOperations.Add(duplicateOp);
        var dbEx = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.IsType<PostgresException>(dbEx.InnerException);
    }

    [Fact]
    public async Task Scenario10_Restart_during_repair_detects_incomplete_operation_and_allows_superseding()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central" });
        var artifact = setup.Artifact;

        var expiredOp = new ReplicaRepairOperation(
            Guid.NewGuid(), setup.Agent.TenantId, artifact.Id, setup.Sftp.Id, "sftp", null, T0.AddHours(-1));
        expiredOp.Claim(Guid.NewGuid(), "old-worker", T0.AddHours(-1), TimeSpan.FromMinutes(5));
        await setup.RepairStore.CreateOperationAsync(expiredOp, default);

        var startResult = await setup.RepairService.StartRepairAsync(
            setup.Agent.TenantId, artifact.Id, new StartReplicaRepairRequest(StorageConnectionId: setup.Sftp.Id), null, default);
        Assert.True(startResult.Success);

        await using var context = fixture.CreatePlatformContext();
        var old = await context.ReplicaRepairOperations.SingleAsync(r => r.Id == expiredOp.Id);
        Assert.Equal(ReplicaRepairStatus.Failed, old.Status);
        Assert.Equal("lease_expired", old.FailureCode);
    }

    [Fact]
    public async Task Scenario13_Cross_tenant_repair_is_rejected()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central" });
        var otherTenant = TenantId.New();

        var result = await setup.RepairService.StartRepairAsync(
            otherTenant, setup.Artifact.Id, new StartReplicaRepairRequest(StorageConnectionId: setup.Sftp.Id), null, default);

        Assert.False(result.Success);
        Assert.Equal("artifact_not_found", result.Code);
    }

    [Fact]
    public async Task Scenario14_Unauthorized_destination_not_in_snapshot_is_rejected()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central" });
        var arbitraryConn = Guid.NewGuid();

        var result = await setup.RepairService.StartRepairAsync(
            setup.Agent.TenantId, setup.Artifact.Id, new StartReplicaRepairRequest(StorageConnectionId: arbitraryConn), null, default);

        Assert.False(result.Success);
        Assert.Equal("unauthorized_destination", result.Code);
    }

    [Fact]
    public async Task Scenario15_Disabled_storage_connection_is_rejected()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central" });

        await using (var context = fixture.CreatePlatformContext())
        {
            var sftpConn = await context.StorageConnections.SingleAsync(c => c.Id == setup.Sftp.Id);
            sftpConn.SetEnabled(false, T0);
            await context.SaveChangesAsync();
        }

        var result = await setup.RepairService.StartRepairAsync(
            setup.Agent.TenantId, setup.Artifact.Id, new StartReplicaRepairRequest(StorageConnectionId: setup.Sftp.Id), null, default);

        Assert.False(result.Success);
        Assert.Equal("storage_connection_disabled", result.Code);
    }

    [Fact]
    public async Task Scenario16_Physical_quota_accounting_adds_replica_bytes_under_physical_mode()
    {
        var setup = await SetupAsync(mode: QuotaAccountingMode.PhysicalReplicaBytes, verified: new HashSet<string> { "central" });
        var artifact = setup.Artifact;

        await using var context = fixture.CreatePlatformContext();
        var usageBefore = await context.TenantStorageUsages.SingleAsync(u => u.TenantId == setup.Agent.TenantId);
        Assert.Equal(Content.Length, usageBefore.UsedBytes);

        var startResult = await setup.RepairService.StartRepairAsync(
            setup.Agent.TenantId, artifact.Id, new StartReplicaRepairRequest(StorageConnectionId: setup.Sftp.Id), null, default, executeInBackground: false);

        var op = await setup.RepairService.ExecuteRepairAsync(setup.Agent.TenantId, artifact.Id, startResult.Operation!.Id);
        Assert.Equal("Succeeded", op!.Status);

        var usageAfter = await context.TenantStorageUsages.AsNoTracking().SingleAsync(u => u.TenantId == setup.Agent.TenantId);
        Assert.Equal(Content.Length * 2, usageAfter.UsedBytes);
    }

    [Fact]
    public async Task Scenario17_Partial_upload_orphan_recovery_and_cleanup_on_failure()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central" });
        var artifact = setup.Artifact;

        var sftpProvider = (InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, "sftp", setup.Sftp.Id.ToString("D"))!;
        sftpProvider.ShouldFailWrite = true;

        var startResult = await setup.RepairService.StartRepairAsync(
            setup.Agent.TenantId, artifact.Id, new StartReplicaRepairRequest(StorageConnectionId: setup.Sftp.Id), null, default, executeInBackground: false);

        var op = await setup.RepairService.ExecuteRepairAsync(setup.Agent.TenantId, artifact.Id, startResult.Operation!.Id);
        Assert.Equal("Failed", op!.Status);
        Assert.Equal(ReplicaRepairFailureCodes.StorageWriteFailed, op.FailureCode);

        Assert.Empty(sftpProvider.Storage);
    }

    [Fact]
    public async Task Scenario20_Legacy_single_destination_job_regression()
    {
        var agent = await World.AgentAsync(100 * QuotaWorld.Gb);
        var job = await World.JobAsync(agent);
        var (run, _) = await World.PublishAsync(World.RunStore(), agent, job, 100, [ReplicaSpec.Central], T0);

        var reloaded = (await World.RunStore().GetRunAsync(agent.TenantId, run.Id, default))!;

        Assert.Equal(BackupStatus.Succeeded, reloaded.Status);
        Assert.Empty(reloaded.CurrentAttempt.DestinationOutcomes);
        var replication = BackupOrchestrator.ToView(reloaded).Replication!;
        Assert.Equal(("Healthy", 1, 1), (replication.Status, replication.VerifiedReplicas, replication.DestinationCount));
    }
}
