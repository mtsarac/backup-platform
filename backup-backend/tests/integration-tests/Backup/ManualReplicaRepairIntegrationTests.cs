using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;
using BackupPlatform.Application.Backups;
using BackupPlatform.Api.Downloads;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Application.Restores;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Infrastructure.Downloads;
using BackupPlatform.Infrastructure.Restores;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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

public sealed partial class ManualReplicaRepairIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = BackupStoreHarness.T0;
    private static readonly byte[] Content = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly string Checksum = Convert.ToHexString(SHA256.HashData(Content)).ToLowerInvariant();

    private readonly ConnectionWorld _connections = new(fixture);
    private QuotaWorld World => _connections.Quota;
    private readonly HashSet<TenantId> _ownedTenants = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var context = fixture.CreatePlatformContext();
        var operations = await context.ReplicaRepairOperations
            .Where(r => _ownedTenants.Contains(r.TenantId) && (r.Status == ReplicaRepairStatus.Pending || r.Status == ReplicaRepairStatus.InProgress))
            .ToListAsync();
        foreach (var operation in operations)
            operation.CancelStale(DateTimeOffset.UtcNow);
        await context.SaveChangesAsync();
    }

    private sealed class InMemoryStorageProvider(string id) : IStorageProvider
    {
        public StorageProviderId Id { get; } = new(id);
        public StorageTopology Topology => StorageTopology.BackendRelayed;
        public StorageCapabilities Capabilities => new([StorageCapabilityIds.Read, StorageCapabilityIds.Delete]);

        public ConcurrentDictionary<string, byte[]> Storage { get; } = new();
        public ConcurrentBag<string> DeletedKeys { get; } = new();
        public bool ShouldFailRead { get; set; }
        public bool ShouldFailWrite { get; set; }
        public StorageFailureKind WriteFailureKind { get; set; } = StorageFailureKind.Io;
        public bool ShouldFailVerify { get; set; }
        public Func<byte[], Stream>? ReadStream { get; set; }
        public Func<Task>? BeforeRead { get; set; }
        public Func<StorageObjectKey, Task>? BeforeDelete { get; set; }
        public Action<StorageObjectKey>? BeforePut { get; set; }
        public Func<StorageObjectKey, Task>? BeforePutAsync { get; set; }

        public async Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            if (ShouldFailWrite) throw new StorageException(WriteFailureKind, "Simulated write failure");
            BeforePut?.Invoke(key);
            if (BeforePutAsync is not null) await BeforePutAsync(key);
            if (options.Overwrite == StorageOverwritePolicy.Fail && Storage.TryGetValue(key.Value, out var existing))
            {
                var existingChecksum = Convert.ToHexString(SHA256.HashData(existing)).ToLowerInvariant();
                if (existing.Length != options.ExpectedLength || existingChecksum != options.ExpectedChecksum?.Hex)
                    throw new StorageException(StorageFailureKind.AlreadyExists, "Conflicting target content");
                return new StorageObjectMetadata(new StorageLocation(Id, key), existing.Length, options.ExpectedChecksum, DateTimeOffset.UtcNow);
            }
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            var bytes = ms.ToArray();
            Storage[key.Value] = bytes;
            progress?.Report(bytes.Length);
            return new StorageObjectMetadata(new StorageLocation(Id, key), bytes.Length, new ContentChecksum(ChecksumAlgorithm.Sha256, Checksum), DateTimeOffset.UtcNow);
        }

        public async Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            if (BeforeRead is not null) await BeforeRead();
            if (ShouldFailRead) throw new StorageException(StorageFailureKind.Io, "Simulated read failure");
            if (Storage.TryGetValue(key.Value, out var bytes))
                return ReadStream?.Invoke(bytes) ?? new MemoryStream(bytes);
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

        public async Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            if (BeforeDelete is not null) await BeforeDelete(key);
            DeletedKeys.Add(key.Value);
            return Storage.TryRemove(key.Value, out _);
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

    private async Task<MultiSetup> SetupAsync(QuotaAccountingMode mode = QuotaAccountingMode.LogicalArtifactBytes, ISet<string>? verified = null, byte[]? content = null)
    {
        content ??= Content;
        var checksum = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var policy = await World.CreatePolicyAsync(mode, QuotaScopeRule.All, QuotaEnforcementMode.WarnOnly);
        var agent = await World.AgentAsync(100 * QuotaWorld.Gb, policy.Key);
        _ownedTenants.Add(agent.TenantId);
        var (_, central) = await _connections.DefaultsAsync(agent.TenantId);
        var sftp = await _connections.AddAsync(agent.TenantId, "sftp-1", "sftp");
        var drive = await _connections.AddAsync(agent.TenantId, "drive-1", "google-drive");

        var configuration = BackupStoreHarness.Configuration() with
        {
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
                replicaResults.Add(new BackupReplicaResult(provider, target, key, content.Length, checksum, "sha256", "Verified", "ReadBack", T0, T0, conn.Id));
                destReports.Add(new BackupDestinationReport(provider, conn.Id, "Verified", "Succeeded", "Verified", null, key, false, T0, T0));
            }
            else
            {
                destReports.Add(new BackupDestinationReport(provider, conn.Id, "Failed", "Failed", "NotRun", "StorageUnavailable", null, false, T0, T0));
            }
        }

        var completion = new Completion("Succeeded", null,
            new BackupArtifactResult(content.Length, checksum, checksum, "sha256", T0, "{}", "{}", replicaResults),
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
                p.Storage[KeyFor(provider, conn)] = content;
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

    [Theory]
    [InlineData(QuotaAccountingMode.PhysicalReplicaBytes, null, false)]
    [InlineData(QuotaAccountingMode.LogicalArtifactBytes, null, true)]
    [InlineData(QuotaAccountingMode.PhysicalReplicaBytes, "central", true)]
    [InlineData(QuotaAccountingMode.LogicalArtifactBytes, "sftp", false)]
    public async Task Repair_quota_admission_counts_only_additional_bytes_in_policy_scope(
        QuotaAccountingMode mode, string? countedProvider, bool allowed)
    {
        var setup = await SetupAsync(mode: mode, verified: new HashSet<string> { "central" });
        var scopes = countedProvider is null
            ? QuotaScopeRule.All
            : QuotaScopeRule.Create([new QuotaScopeSelector(QuotaScopeKind.ProviderType, [countedProvider])]);
        var policyKey = await CreatePolicyAsync(World, QuotaEnforcementMode.BlockNewBackups, mode, scopes);
        await World.AssignAsync(setup.Agent.TenantId, await World.PlanAsync(Content.Length - 1, policyKey));
        var usageBefore = await World.StoredUsageAsync(setup.Agent.TenantId);
        var reservationsBefore = await ReservationsAsync(fixture, setup.Agent.TenantId);

        var admission = await setup.RepairStore.AdmitRepairAsync(
            setup.Agent.TenantId, setup.Artifact.Id, setup.Sftp.Id, null, DateTimeOffset.UtcNow, default);

        Assert.Equal(allowed, admission.Allowed);
        Assert.Equal(allowed ? null : ReplicaRepairFailureCodes.QuotaExceeded, admission.DenialCode);
        Assert.Equal(allowed ? null : "Storage quota limit exceeded: quota_exceeded.", admission.DenialReason);
        var usageAfter = await World.StoredUsageAsync(setup.Agent.TenantId);
        Assert.NotNull(usageBefore);
        Assert.NotNull(usageAfter);
        Assert.Equal((usageBefore.UsedBytes, usageBefore.ReservedBytes), (usageAfter.UsedBytes, usageAfter.ReservedBytes));
        var reservationsAfter = await ReservationsAsync(fixture, setup.Agent.TenantId);
        Assert.Equal(reservationsBefore.Select(reservation => reservation.RunId), reservationsAfter.Select(reservation => reservation.RunId));
        Assert.Empty(await setup.RepairStore.ListOperationsAsync(setup.Agent.TenantId, setup.Artifact.Id, default));
    }

    private async Task<MultiSetup> SetupHardQuotaForOneRepairAsync()
    {
        var setup = await SetupAsync(mode: QuotaAccountingMode.PhysicalReplicaBytes, verified: new HashSet<string> { "central" });
        var policyKey = await CreatePolicyAsync(World, QuotaEnforcementMode.BlockNewBackups, QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeRule.All);
        // The central replica plus exactly one more copy fit.
        await World.AssignAsync(setup.Agent.TenantId, await World.PlanAsync(Content.Length * 2, policyKey));
        return setup;
    }

    [Fact]
    public async Task Repair_quota_hold_counts_against_later_repairs_until_the_operation_ends()
    {
        var setup = await SetupHardQuotaForOneRepairAsync();
        var tenantId = setup.Agent.TenantId;

        var first = await setup.RepairService.StartRepairAsync(tenantId, setup.Artifact.Id, new StartReplicaRepairRequest(StorageConnectionId: setup.Sftp.Id), null, default);
        Assert.True(first.Success);
        var held = await World.StoredUsageAsync(tenantId);
        Assert.Equal((Content.Length, Content.Length), (held!.UsedBytes, held.ReservedBytes));

        var second = await setup.RepairService.StartRepairAsync(tenantId, setup.Artifact.Id, new StartReplicaRepairRequest(StorageConnectionId: setup.Drive.Id), null, default);
        Assert.False(second.Success);
        Assert.Equal(ReplicaRepairFailureCodes.QuotaExceeded, second.Code);
        Assert.DoesNotContain(await setup.RepairStore.ListOperationsAsync(tenantId, setup.Artifact.Id, default), op => op.StorageConnectionId == setup.Drive.Id);

        Assert.True(await setup.RepairService.CancelRepairAsync(tenantId, setup.Artifact.Id, first.Operation!.Id, default));
        var released = await World.StoredUsageAsync(tenantId);
        Assert.Equal((Content.Length, 0L), (released!.UsedBytes, released.ReservedBytes));

        var retry = await setup.RepairService.StartRepairAsync(tenantId, setup.Artifact.Id, new StartReplicaRepairRequest(StorageConnectionId: setup.Drive.Id), null, default);
        Assert.True(retry.Success);
        var executed = await setup.RepairService.ExecuteRepairAsync(tenantId, setup.Artifact.Id, retry.Operation!.Id);
        Assert.Equal("Succeeded", executed!.Status);
        var settled = await World.StoredUsageAsync(tenantId);
        Assert.Equal((Content.Length * 2L, 0L), (settled!.UsedBytes, settled.ReservedBytes));
    }

    [Fact]
    public async Task Concurrent_repairs_admit_only_as_many_as_fit_the_hard_quota()
    {
        var setup = await SetupHardQuotaForOneRepairAsync();
        var tenantId = setup.Agent.TenantId;

        var results = await Task.WhenAll(
            setup.RepairService.StartRepairAsync(tenantId, setup.Artifact.Id, new StartReplicaRepairRequest(StorageConnectionId: setup.Sftp.Id), null, default),
            setup.RepairService.StartRepairAsync(tenantId, setup.Artifact.Id, new StartReplicaRepairRequest(StorageConnectionId: setup.Drive.Id), null, default));

        Assert.Single(results, result => result.Success);
        Assert.Single(results, result => result is { Success: false, Code: ReplicaRepairFailureCodes.QuotaExceeded });
        var usage = await World.StoredUsageAsync(tenantId);
        Assert.Equal((Content.Length, Content.Length), (usage!.UsedBytes, usage.ReservedBytes));
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
        Assert.Equal("Pending", op!.Status);
        Assert.Equal(ReplicaRepairFailureCodes.StorageWriteFailed, op.FailureCode);
        Assert.NotNull(op.NextRetryAt);
        Assert.Equal(1, op.AttemptCount);
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

    [Fact]
    public async Task Scenario18_Automatic_repair_worker_executes_due_repair_to_verified_without_new_backup()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central" });
        var artifact = setup.Artifact;

        var startResult = await setup.RepairService.StartRepairAsync(
            setup.Agent.TenantId, artifact.Id, new StartReplicaRepairRequest(StorageConnectionId: setup.Sftp.Id), null, default, executeInBackground: false);

        Assert.True(startResult.Success);
        Assert.Equal("Pending", startResult.Operation!.Status);
        var readEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var sourceProvider = (InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, "central", "central")!;
        sourceProvider.BeforeRead = async () =>
        {
            readEntered.TrySetResult();
            await releaseRead.Task.WaitAsync(deadline.Token);
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IBackupStorageResolver>(setup.Resolver);
        services.AddScoped<IReplicaRepairStore>(_ => new PostgreSqlReplicaRepairStore(World.Options()));
        services.AddScoped<ReplicaRepairService>();
        await using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var worker = new BackupPlatform.Api.Backups.AutomaticReplicaRepairWorker(
            sp.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            NullLogger<BackupPlatform.Api.Backups.AutomaticReplicaRepairWorker>.Instance);
        try
        {
            await worker.TickAsync(setup.Agent.TenantId, deadline.Token);
            await readEntered.Task.WaitAsync(deadline.Token);
            releaseRead.TrySetResult();
            await worker.WaitForRunningTasksAsync(deadline.Token);
        }
        finally
        {
            releaseRead.TrySetResult();
            using var shutdownDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await worker.StopAsync(shutdownDeadline.Token);
        }

        await using var context = fixture.CreatePlatformContext();
        var updatedOp = await context.ReplicaRepairOperations.AsNoTracking().SingleAsync(r => r.Id == startResult.Operation.Id);
        Assert.Equal(ReplicaRepairStatus.Succeeded, updatedOp.Status);
        Assert.NotNull(updatedOp.TargetReplicaId);

        var reloadedArtifact = await context.BackupArtifacts.Include(a => a.Replicas).SingleAsync(a => a.Id == artifact.Id);
        var repairedReplica = Assert.Single(reloadedArtifact.Replicas, r => r.Id == updatedOp.TargetReplicaId);
        Assert.Equal("Verified", repairedReplica.VerificationState);
        Assert.Equal(setup.Sftp.Id, repairedReplica.StorageConnectionId);
        var destinationProvider = (InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, "sftp", setup.Sftp.Id.ToString("D"))!;
        Assert.Equal(Content, destinationProvider.Storage[repairedReplica.Key]);

        // No new run or artifact created
        var runsCount = await context.BackupRuns.CountAsync(r => r.TenantId == setup.Agent.TenantId);
        Assert.Equal(1, runsCount);
        Assert.Equal(1, await context.BackupArtifacts.CountAsync(a => a.TenantId == setup.Agent.TenantId));
    }

    [Fact]
    public async Task Scenario19_Multi_node_concurrent_workers_claim_safely_without_duplicates()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central" });
        var artifact = setup.Artifact;

        var startResult = await setup.RepairService.StartRepairAsync(
            setup.Agent.TenantId, artifact.Id, new StartReplicaRepairRequest(StorageConnectionId: setup.Sftp.Id), null, default, executeInBackground: false);

        var store = new PostgreSqlReplicaRepairStore(World.Options());
        var now = DateTimeOffset.UtcNow;

        var task1 = store.ClaimDueRepairAsync("node-1", now, TimeSpan.FromMinutes(5), default, setup.Agent.TenantId);
        var task2 = store.ClaimDueRepairAsync("node-2", now, TimeSpan.FromMinutes(5), default, setup.Agent.TenantId);

        var results = await Task.WhenAll(task1, task2);
        var claimed = results.Where(r => r is not null).ToList();

        Assert.Single(claimed);
    }

    [Fact]
    public async Task Scenario21_Durable_orphan_replica_recorded_when_storage_delete_fails()
    {
        var setup = await SetupAsync(mode: QuotaAccountingMode.PhysicalReplicaBytes, verified: new HashSet<string> { "central" });
        var artifact = setup.Artifact;
        var store = new PostgreSqlReplicaRepairStore(World.Options());

        var targetKey = $"{setup.Agent.TenantId.Value:N}/job/run/attempt/orphan.bak";
        var now = DateTimeOffset.UtcNow;

        await store.RecordRepairOrphanAsync(
            setup.Agent.TenantId,
            artifact.Id,
            setup.Sftp.Id,
            "sftp",
            setup.Sftp.Id.ToString("D"),
            targetKey,
            Content.Length,
            Checksum,
            now,
            default);

        await using var context = fixture.CreatePlatformContext();
        var orphan = await context.BackupReplicas.SingleOrDefaultAsync(r => r.Key == targetKey);
        Assert.NotNull(orphan);
        Assert.Equal("Failed", orphan.VerificationState);
        Assert.Equal(BackupReplicaStatus.DeletePending, orphan.Status);
        Assert.True(BackupReplicaStorage.Occupies(orphan.Status));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadFallsBackBeforeHeadersAndPinsTheBytesItServes(bool corruptPrimary)
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "sftp" });
        var clock = new MutableClock();
        var store = new PostgreSqlDownloadStore(World.Options(), clock);
        var user = new User(UserId.New(), "Fallback downloader");
        await using (var context = fixture.CreatePlatformContext())
        {
            context.Users.Add(user);
            context.TenantMemberships.Add(new TenantMembership(setup.Agent.TenantId, user.Id, TenantRole.Admin));
            await context.SaveChangesAsync();
        }
        var primary = setup.Artifact.Replicas.Single(replica => replica.StorageProvider == "central");
        var fallback = setup.Artifact.Replicas.Single(replica => replica.StorageProvider == "sftp");
        var session = (await store.CreateAsync(new(setup.Agent.TenantId, setup.Artifact.Id, primary.Id, user.Id.Value,
            "backup.bak", clock.GetUtcNow(), TimeSpan.FromMinutes(15)), default))!;
        var provider = (InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, primary.StorageProvider, primary.StorageTarget)!;
        if (corruptPrimary) provider.Storage[primary.Key] = [8, 7, 6, 5, 4, 3, 2, 1];
        else provider.ShouldFailRead = true;
        var retention = new PostgreSqlBackupRetentionStore(World.Options());
        ((InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, fallback.StorageProvider, fallback.StorageTarget)!).BeforeRead = async () =>
        {
            Assert.Equal(fallback.Id, (await store.GetAsync(session.TenantId, session.Id, default))!.ReplicaId);
            await retention.RequestDeleteAsync(session.TenantId, session.ArtifactId, user.Id.Value, clock.GetUtcNow(), default);
            await using var context = fixture.CreatePlatformContext();
            var now = clock.GetUtcNow();
            await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE backup_replicas SET \"DeleteAfter\" = {now} WHERE \"TenantId\" = {session.TenantId.Value} AND \"ArtifactId\" = {session.ArtifactId}");
            Assert.DoesNotContain(await retention.ClaimDueDeletionsAsync("while-reading", now, TimeSpan.FromMinutes(2), 500, default), claim => claim.ReplicaId == fallback.Id);
        };
        var service = new DownloadService(store, setup.Resolver, new NoAgentDownloadSource(), clock, NullLogger<DownloadService>.Instance);
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        await new DownloadStreamer(store, service, clock, NullLogger<DownloadStreamer>.Instance).StreamAsync(http, session);
        Assert.Equal(Content, ((MemoryStream)http.Response.Body).ToArray());
        var completed = (await store.GetAsync(session.TenantId, session.Id, default))!;
        Assert.Equal((DownloadState.Completed, fallback.Id, (long)Content.Length), (completed.State, completed.ReplicaId, completed.TransferredBytes));
        Assert.NotNull(completed.ReleasedAt);
        Assert.Contains(await retention.ClaimDueDeletionsAsync("after-download", clock.GetUtcNow(), TimeSpan.FromMinutes(2), 500, default), claim => claim.ReplicaId == fallback.Id);
    }

    [Fact]
    public async Task ConcurrentDownloadSwitchesHaveOneWinnerAndFenceWrongOwnerExpiredLeaseAndForeignReplica()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "sftp", "google-drive" });
        var other = await SetupAsync(verified: new HashSet<string> { "central" });
        var clock = new MutableClock();
        var store = new PostgreSqlDownloadStore(World.Options(), clock);
        var user = new User(UserId.New(), "Source switch owner");
        await using (var context = fixture.CreatePlatformContext())
        {
            context.Users.Add(user);
            context.TenantMemberships.Add(new TenantMembership(setup.Agent.TenantId, user.Id, TenantRole.Admin));
            await context.SaveChangesAsync();
        }
        var primary = setup.Artifact.Replicas.Single(replica => replica.StorageProvider == "central");
        var session = (await store.CreateAsync(new(setup.Agent.TenantId, setup.Artifact.Id, primary.Id, user.Id.Value,
            "backup.bak", clock.GetUtcNow(), TimeSpan.FromMinutes(15)), default))!;
        var fallback = setup.Artifact.Replicas.Single(replica => replica.StorageProvider == "sftp");
        var alternate = setup.Artifact.Replicas.Single(replica => replica.StorageProvider == "google-drive");
        Assert.NotNull(await store.TransitionAsync(session.Id, new(DownloadState.Ready, DownloadState.Streaming, null, "owner", clock.GetUtcNow(), TimeSpan.FromSeconds(60)), default));
        Assert.Null(await store.SwitchReplicaAsync(session.Id, primary.Id, "wrong-owner", fallback, default));
        Assert.Null(await store.SwitchReplicaAsync(session.Id, primary.Id, "owner", other.Artifact.Replicas.Single(), default));
        var results = await Task.WhenAll(
            store.SwitchReplicaAsync(session.Id, primary.Id, "owner", fallback, default),
            new PostgreSqlDownloadStore(World.Options(), clock).SwitchReplicaAsync(session.Id, primary.Id, "owner", alternate, default));
        var winner = Assert.Single(results, result => result is not null)!;
        Assert.Equal(winner.ReplicaId, (await store.GetAsync(session.TenantId, session.Id, default))!.ReplicaId);
        await using (var context = fixture.CreatePlatformContext())
        {
            var rejected = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE download_sessions SET "ReplicaId" = {primary.Id}, "StorageProvider" = {primary.StorageProvider},
                    "StorageTarget" = {primary.StorageTarget}, "Key" = {primary.Key}, "LeaseOwner" = 'stale-owner'
                WHERE "Id" = {session.Id}
                """));
            Assert.Equal("23514", rejected.SqlState);
        }
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Null(await store.SwitchReplicaAsync(session.Id, winner.ReplicaId, "owner", primary, default));
    }

    [Fact]
    public async Task RestoreProbesUnavailablePrimaryAndSwitchesItsPinBeforeClaim()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "sftp" });
        var world = new RestoreWorld(fixture);
        await world.EnableRestoreAsync(setup.Agent);
        var now = DateTimeOffset.UtcNow;
        var store = new PostgreSqlRestoreStore(World.Options(), setup.Resolver);
        var operation = (await store.CreateAsync(RestoreWorld.Command(setup.Agent.TenantId, setup.Artifact.Id, setup.Agent.AgentId, now: now), default)).Operation!;
        var primary = setup.Artifact.Replicas.Single(replica => replica.StorageProvider == "central");
        var fallback = setup.Artifact.Replicas.Single(replica => replica.StorageProvider == "sftp");
        ((InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, primary.StorageProvider, primary.StorageTarget)!).ShouldFailRead = true;
        var claim = (await store.ClaimNextAsync(setup.Agent.TenantId, setup.Agent.AgentId, now, RestoreWorld.Lease, RestoreWorld.Unclaimed, default))!;
        Assert.Equal((fallback.Id, RestoreSourceAccess.BackendRelayed, RestoreStatus.Leased), (claim.Replica.Id, claim.Operation.SourceAccess, claim.Operation.Status));
        Assert.Equal(fallback.Id, (await world.OperationAsync(operation.Id)).Pin.ReplicaId);
        Assert.Equal(0, await world.ActivePinsAsync(primary.Id));
        Assert.Equal(1, await world.ActivePinsAsync(fallback.Id));
        Assert.False(claim.Operation.SwitchReplica(primary, RestoreSourceAccess.CentralStorage));
        Assert.Null(await store.ClaimNextAsync(setup.Agent.TenantId, setup.Agent.AgentId, now, RestoreWorld.Lease, RestoreWorld.Unclaimed, default));
        await store.StartAsync(RestoreWorld.Command(claim, now), RestoreWorld.Lease, default);
        await using var context = fixture.CreatePlatformContext();
        var movedOperation = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE restore_operations SET \"ReplicaId\" = {primary.Id} WHERE \"Id\" = {operation.Id}"));
        Assert.Equal("23514", movedOperation.SqlState);
        var movedPin = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE restore_replica_pins SET \"ReplicaId\" = {primary.Id} WHERE \"OperationId\" = {operation.Id}"));
        Assert.Equal("23514", movedPin.SqlState);
    }

    [Fact]
    public async Task RestoreClaimNetworkProbeDoesNotHoldDatabaseLocksAcrossProbe()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "sftp" });
        var world = new RestoreWorld(fixture);
        await world.EnableRestoreAsync(setup.Agent);
        var now = DateTimeOffset.UtcNow;
        var store = new PostgreSqlRestoreStore(World.Options(), setup.Resolver);
        var operation = (await store.CreateAsync(RestoreWorld.Command(setup.Agent.TenantId, setup.Artifact.Id, setup.Agent.AgentId, now: now), default)).Operation!;
        var primary = setup.Artifact.Replicas.Single(replica => replica.StorageProvider == "central");
        var fallback = setup.Artifact.Replicas.Single(replica => replica.StorageProvider == "sftp");

        var probeStarted = new TaskCompletionSource<bool>();
        var probeRelease = new TaskCompletionSource<bool>();
        var canReadProbeFinished = false;

        ((InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, fallback.StorageProvider, fallback.StorageTarget)!).BeforeRead = async () =>
        {
            probeStarted.TrySetResult(true);
            await probeRelease.Task;
            canReadProbeFinished = true;
        };

        // Primary fails read so it probes fallback (sftp), triggering BeforeRead
        ((InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, primary.StorageProvider, primary.StorageTarget)!).ShouldFailRead = true;

        var claimTask = Task.Run(() => store.ClaimNextAsync(setup.Agent.TenantId, setup.Agent.AgentId, now, RestoreWorld.Lease, RestoreWorld.Unclaimed, default));
        await probeStarted.Task;

        // While BeforeRead is blocked, an independent DB transaction must be able to acquire agent and replica row locks immediately without waiting for probe
        await using (var context = fixture.CreatePlatformContext())
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var tx = await context.Database.BeginTransactionAsync(timeoutCts.Token);
            var agentRows = await context.Database.SqlQuery<Guid>(
                $"SELECT \"Id\" AS \"Value\" FROM agents WHERE \"Id\" = {setup.Agent.AgentId.Value} AND \"TenantId\" = {setup.Agent.TenantId.Value} FOR UPDATE").ToListAsync(timeoutCts.Token);
            Assert.Single(agentRows);
            var lockedReplicas = await context.Database.SqlQuery<Guid>(
                $"SELECT \"Id\" AS \"Value\" FROM backup_replicas WHERE \"Id\" = {fallback.Id} FOR UPDATE").ToListAsync(timeoutCts.Token);
            Assert.Single(lockedReplicas);
            await tx.RollbackAsync(timeoutCts.Token);
        }

        Assert.False(canReadProbeFinished);
        probeRelease.TrySetResult(true);
        var claim = await claimTask;
        Assert.NotNull(claim);
        Assert.Equal(fallback.Id, claim.Replica.Id);
    }

    [Fact]
    public async Task RestoreCancelledDuringRemoteProbeIsSafelyRefusedAndNotHandedOut()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "sftp" });
        var world = new RestoreWorld(fixture);
        await world.EnableRestoreAsync(setup.Agent);
        var now = DateTimeOffset.UtcNow;
        var store = new PostgreSqlRestoreStore(World.Options(), setup.Resolver);
        var operation = (await store.CreateAsync(RestoreWorld.Command(setup.Agent.TenantId, setup.Artifact.Id, setup.Agent.AgentId, now: now), default)).Operation!;
        var primary = setup.Artifact.Replicas.Single(replica => replica.StorageProvider == "central");
        var fallback = setup.Artifact.Replicas.Single(replica => replica.StorageProvider == "sftp");

        var probeStarted = new TaskCompletionSource<bool>();
        var probeRelease = new TaskCompletionSource<bool>();

        ((InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, fallback.StorageProvider, fallback.StorageTarget)!).BeforeRead = async () =>
        {
            probeStarted.TrySetResult(true);
            await probeRelease.Task;
        };
        ((InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, primary.StorageProvider, primary.StorageTarget)!).ShouldFailRead = true;

        var claimTask = Task.Run(() => store.ClaimNextAsync(setup.Agent.TenantId, setup.Agent.AgentId, now, RestoreWorld.Lease, RestoreWorld.Unclaimed, default));
        await probeStarted.Task;

        // User cancels the restore while probe is running
        var cancelled = await store.RequestCancellationAsync(setup.Agent.TenantId, operation.Id, now.AddSeconds(1), default);
        Assert.NotNull(cancelled);
        Assert.Equal(RestoreStatus.Cancelled, cancelled.Status);

        probeRelease.TrySetResult(true);
        var claim = await claimTask;
        Assert.Null(claim);

        var stored = await world.OperationAsync(operation.Id);
        Assert.Equal(RestoreStatus.Cancelled, stored.Status);
        Assert.False(stored.Pin.IsActive);
    }

    [Fact]
    public async Task DisabledConnectionStillServesStoredRestore()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "sftp" });
        await new RestoreWorld(fixture).EnableRestoreAsync(setup.Agent);
        await using (var context = fixture.CreatePlatformContext())
        {
            var connection = await context.StorageConnections.SingleAsync(entity => entity.Id == setup.Sftp.Id);
            connection.SetEnabled(false, DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }
        var store = new PostgreSqlRestoreStore(World.Options(), setup.Resolver);
        var result = await store.CreateAsync(RestoreWorld.Command(setup.Agent.TenantId, setup.Artifact.Id, setup.Agent.AgentId, now: DateTimeOffset.UtcNow), default);
        Assert.Equal(RestoreCreateOutcome.Created, result.Outcome);
        Assert.Equal(RestoreSourceAccess.BackendRelayed, result.Operation!.SourceAccess);
    }

    [Fact]
    public async Task AdmissionPreservesExpiredTargetIntentAndQuotaUntilRecoveryRetriesIt()
    {
        var setup = await SetupAsync(QuotaAccountingMode.PhysicalReplicaBytes, new HashSet<string> { "central", "google-drive" });
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var source = setup.Artifact.Replicas.Single(r => r.StorageProvider == "central");
        var token = Guid.NewGuid();
        var operation = new ReplicaRepairOperation(Guid.NewGuid(), setup.Agent.TenantId, setup.Artifact.Id, setup.Sftp.Id,
            "sftp", source.Id, now.AddMinutes(-10), totalBytes: Content.Length, maxAttempts: 3);
        operation.Claim(token, "crashed-worker", now.AddMinutes(-10), TimeSpan.FromMinutes(1));
        operation.SetTargetIntent(source.Id, $"partial-{operation.Id:N}.bak");
        Assert.True((await setup.RepairStore.CreateOperationAsync(operation, default)).Allowed);
        var before = await World.StoredUsageAsync(setup.Agent.TenantId);

        var admission = await setup.RepairStore.AdmitRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, setup.Sftp.Id, null, now, default);

        Assert.False(admission.Allowed);
        Assert.Equal("repair_already_in_progress", admission.DenialCode);
        await using (var context = fixture.CreatePlatformContext())
        {
            var unchanged = await context.ReplicaRepairOperations.AsNoTracking().SingleAsync(r => r.Id == operation.Id);
            Assert.Equal(ReplicaRepairStatus.InProgress, unchanged.Status);
            Assert.Equal(operation.TargetKey, unchanged.TargetKey);
            Assert.Equal(token, unchanged.LeaseToken);
            Assert.Equal(operation.LeaseExpiresAt, unchanged.LeaseExpiresAt);
            Assert.Equal(1, unchanged.AttemptCount);
            Assert.Equal(3, unchanged.MaxAttempts);
            Assert.Equal(Content.Length, unchanged.ReservedQuotaBytes);
            Assert.Null(unchanged.FailureCode);
            Assert.Null(unchanged.CompletedAt);
        }
        var after = await World.StoredUsageAsync(setup.Agent.TenantId);
        Assert.Equal((before!.UsedBytes, before.ReservedBytes), (after!.UsedBytes, after.ReservedBytes));

        await setup.RepairStore.RecoverStaleRepairsAsync(now, TimeSpan.FromSeconds(10), default);
        await using var recoveredContext = fixture.CreatePlatformContext();
        var recovered = await recoveredContext.ReplicaRepairOperations.AsNoTracking().SingleAsync(r => r.Id == operation.Id);
        Assert.Equal(ReplicaRepairStatus.Pending, recovered.Status);
        Assert.Equal(operation.TargetKey, recovered.TargetKey);
        Assert.Equal(1, recovered.AttemptCount);
        Assert.Equal(3, recovered.MaxAttempts);
        Assert.Equal(Content.Length, recovered.ReservedQuotaBytes);
        Assert.Equal(now.AddSeconds(10), recovered.NextRetryAt);
        var retry = await setup.RepairStore.ClaimOperationAsync(setup.Agent.TenantId, operation.Id, Guid.NewGuid(), "recovery-worker",
            now.AddSeconds(10), TimeSpan.FromMinutes(1), default);
        Assert.NotNull(retry);
        Assert.Equal(2, retry.AttemptCount);
    }

    [Fact]
    public async Task ConcurrentManualAdmissionAndRecoveryKeepOneRetryableOperation()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "google-drive" });
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var operation = new ReplicaRepairOperation(Guid.NewGuid(), setup.Agent.TenantId, setup.Artifact.Id, setup.Sftp.Id,
            "sftp", setup.Artifact.Replicas.First().Id, now.AddMinutes(-10), totalBytes: Content.Length);
        operation.Claim(Guid.NewGuid(), "crashed-worker", now.AddMinutes(-10), TimeSpan.FromMinutes(1));
        operation.SetTargetIntent(setup.Artifact.Replicas.First().Id, $"partial-{operation.Id:N}.bak");
        Assert.True((await setup.RepairStore.CreateOperationAsync(operation, default)).Allowed);
        var name = $"repair-recovery-{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { ApplicationName = name }.ConnectionString;
        var options = new DbContextOptionsBuilder<BackupPlatform.Infrastructure.Persistence.PlatformDbContext>().UseNpgsql(connectionString).Options;
        await using var blocker = fixture.CreatePlatformContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM backup_artifacts WHERE "Id" = {setup.Artifact.Id} FOR NO KEY UPDATE
            """).ToListAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var recovery = new PostgreSqlReplicaRepairStore(options).RecoverStaleRepairsAsync(now, TimeSpan.FromSeconds(10), deadline.Token);
        try
        {
            await WaitForDatabaseLockAsync(name, deadline.Token);
            var manual = Enumerable.Range(0, 8).Select(_ => setup.RepairService.StartRepairAsync(setup.Agent.TenantId,
                setup.Artifact.Id, new StartReplicaRepairRequest(StorageConnectionId: setup.Sftp.Id), null, deadline.Token, executeInBackground: false)).ToArray();
            foreach (var result in await Task.WhenAll(manual))
            {
                Assert.False(result.Success);
                Assert.Equal("repair_already_in_progress", result.Code);
            }
        }
        finally
        {
            await transaction.RollbackAsync();
            await recovery;
        }
        await using var context = fixture.CreatePlatformContext();
        var history = Assert.Single(await context.ReplicaRepairOperations.AsNoTracking()
            .Where(r => r.TenantId == setup.Agent.TenantId && r.ArtifactId == setup.Artifact.Id).ToListAsync());
        Assert.Equal(operation.Id, history.Id);
        Assert.Equal(ReplicaRepairStatus.Pending, history.Status);
        Assert.Equal(operation.TargetKey, history.TargetKey);
        Assert.Equal(1, history.AttemptCount);
        Assert.Equal(now.AddSeconds(10), history.NextRetryAt);
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    [InlineData("Succeeded")]
    [InlineData("Exhausted")]
    public async Task DiscoveryNeverRestartsTerminalRepairHistory(string terminal)
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "google-drive" });
        var now = DateTimeOffset.UtcNow;
        var operation = new ReplicaRepairOperation(Guid.NewGuid(), setup.Agent.TenantId, setup.Artifact.Id, setup.Sftp.Id, "sftp", null, now.AddMinutes(-10),
            maxAttempts: terminal == "Exhausted" ? 1 : 5);
        var token = Guid.NewGuid();
        operation.Claim(token, "terminal-worker", now.AddMinutes(-10), TimeSpan.FromMinutes(5));
        if (terminal is "Failed" or "Exhausted") operation.Fail(token, ReplicaRepairFailureCodes.SourceChecksumMismatch, "permanent", now.AddMinutes(-9));
        else if (terminal == "Cancelled") operation.Cancel(token, now.AddMinutes(-9));
        else operation.Succeed(token, setup.Artifact.Replicas.First().Id, now.AddMinutes(-9));
        await setup.RepairStore.CreateOperationAsync(operation, default);
        Assert.Empty(await setup.RepairStore.DiscoverEligibleRepairsAsync(now, 10, default, setup.Agent.TenantId));
        var history = await setup.RepairStore.ListOperationsAsync(setup.Agent.TenantId, setup.Artifact.Id, default);
        Assert.Equal(operation.Id, Assert.Single(history).Id);
        Assert.Equal(terminal == "Exhausted" ? "Failed" : terminal, history[0].Status);
    }

    [Fact]
    public async Task DiscoveryIsTenantScopedGlobalAndConcurrentWithoutDuplicateOperations()
    {
        var first = await SetupAsync(verified: new HashSet<string> { "central", "google-drive" });
        var second = await SetupAsync(verified: new HashSet<string> { "central", "google-drive" });
        var now = DateTimeOffset.UtcNow;
        var discovered = await Task.WhenAll(
            first.RepairStore.DiscoverEligibleRepairsAsync(now, 10, default, first.Agent.TenantId),
            new PostgreSqlReplicaRepairStore(World.Options()).DiscoverEligibleRepairsAsync(now, 10, default, first.Agent.TenantId));
        var operation = Assert.Single(discovered.SelectMany(batch => batch));
        Assert.Equal(first.Agent.TenantId, operation.TenantId);
        Assert.Empty(await second.RepairStore.ListOperationsAsync(second.Agent.TenantId, second.Artifact.Id, default));
        var global = await first.RepairStore.DiscoverEligibleRepairsAsync(now, 10, default);
        Assert.Contains(global, entity => entity.TenantId == second.Agent.TenantId && entity.ArtifactId == second.Artifact.Id);
        Assert.NotNull(await first.RepairStore.ClaimDueRepairAsync("global", now, TimeSpan.FromMinutes(5), default));
    }

    [Fact]
    public async Task DiscoveryFindsOlderBrokenArtifactBehindThousandsOfHealthyArtifacts()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "google-drive" });
        var now = DateTimeOffset.UtcNow;
        await SeedDiscoveryArtifactsAsync(setup, 2000, now.AddMinutes(1), ["central", "sftp", "google-drive"]);
        var repair = Assert.Single(await setup.RepairStore.DiscoverEligibleRepairsAsync(now, 10, default, setup.Agent.TenantId));
        Assert.Equal(setup.Artifact.Id, repair.ArtifactId);
        Assert.Equal(setup.Sftp.Id, repair.StorageConnectionId);
    }

    [Fact]
    public async Task DiscoveryBoundsDeniedWorkAndResumesAcrossNewScopedStores()
    {
        var setup = await SetupAsync(QuotaAccountingMode.PhysicalReplicaBytes, new HashSet<string> { "central", "sftp" });
        var now = DateTimeOffset.UtcNow;
        await SeedDiscoveryArtifactsAsync(setup, 55, now.AddMinutes(1), ["central", "sftp"]);
        var scopes = QuotaScopeRule.Create([new QuotaScopeSelector(QuotaScopeKind.ProviderType, ["google-drive"])]);
        var policy = await CreatePolicyAsync(World, QuotaEnforcementMode.BlockNewBackups, QuotaAccountingMode.PhysicalReplicaBytes, scopes);
        await World.AssignAsync(setup.Agent.TenantId, await World.PlanAsync(Content.Length, policy));

        // Each newer 100-byte repair is denied; the older eight-byte repair fits. One tick may inspect only 50 candidates.
        Assert.Empty(await new PostgreSqlReplicaRepairStore(World.Options()).DiscoverEligibleRepairsAsync(now, 10, default, setup.Agent.TenantId));
        var repair = Assert.Single(await new PostgreSqlReplicaRepairStore(World.Options()).DiscoverEligibleRepairsAsync(now, 10, default, setup.Agent.TenantId));
        Assert.Equal(setup.Artifact.Id, repair.ArtifactId);
        Assert.Equal(setup.Drive.Id, repair.StorageConnectionId);
        Assert.Empty(await new PostgreSqlReplicaRepairStore(World.Options()).DiscoverEligibleRepairsAsync(now, 10, default, setup.Agent.TenantId));
    }

    [Fact]
    public async Task DiscoveryDoesNotLockHealthyRowsWhileWaitingToReserveAnActionableRepair()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "google-drive" });
        var now = DateTimeOffset.UtcNow;
        var healthyIds = await SeedDiscoveryArtifactsAsync(setup, 55, now.AddMinutes(1), ["central", "sftp", "google-drive"]);
        var name = $"repair-discovery-{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { ApplicationName = name }.ConnectionString;
        var options = new DbContextOptionsBuilder<BackupPlatform.Infrastructure.Persistence.PlatformDbContext>().UseNpgsql(connectionString).Options;
        await using var blocker = fixture.CreatePlatformContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM tenants WHERE "Id" = {setup.Agent.TenantId.Value} FOR NO KEY UPDATE
            """).ToListAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var discovery = new PostgreSqlReplicaRepairStore(options).DiscoverEligibleRepairsAsync(now, 10, deadline.Token, setup.Agent.TenantId);
        try
        {
            await WaitForDatabaseLockAsync(name, deadline.Token);
            await using var check = fixture.CreatePlatformContext();
            await using var checkTransaction = await check.Database.BeginTransactionAsync(deadline.Token);
            foreach (var healthyId in healthyIds)
                Assert.Single(await check.Database.SqlQuery<Guid>($"""
                    SELECT "Id" AS "Value" FROM backup_artifacts WHERE "Id" = {healthyId} FOR NO KEY UPDATE NOWAIT
                    """).ToListAsync(deadline.Token));
        }
        finally
        {
            await transaction.RollbackAsync();
            await discovery;
        }
        var repair = Assert.Single(await discovery);
        Assert.Equal(setup.Artifact.Id, repair.ArtifactId);
    }

    [Fact]
    public Task DiscoveryDeniedTenantDoesNotStarveAnOlderOtherTenant() => BackupStoreHarness.WithIsolatedDatabaseAsync(async kit =>
    {
        var suite = new ManualReplicaRepairIntegrationTests(kit.Database);
        var denied = await suite.SetupAsync(QuotaAccountingMode.PhysicalReplicaBytes, new HashSet<string> { "central", "sftp" });
        var quiet = await suite.SetupAsync(verified: new HashSet<string> { "central", "sftp" });
        var now = DateTimeOffset.UtcNow;
        await suite.SeedDiscoveryArtifactsAsync(denied, 55, now.AddMinutes(1), ["central", "sftp"]);
        var scopes = QuotaScopeRule.Create([new QuotaScopeSelector(QuotaScopeKind.ProviderType, ["google-drive"])]);
        var policy = await CreatePolicyAsync(suite.World, QuotaEnforcementMode.BlockNewBackups, QuotaAccountingMode.PhysicalReplicaBytes, scopes);
        await suite.World.AssignAsync(denied.Agent.TenantId, await suite.World.PlanAsync(1, policy));

        Assert.Empty(await new PostgreSqlReplicaRepairStore(suite.World.Options()).DiscoverEligibleRepairsAsync(now, 10, default));
        var discovered = Assert.Single(await new PostgreSqlReplicaRepairStore(suite.World.Options()).DiscoverEligibleRepairsAsync(now, 10, default));
        Assert.Equal(quiet.Agent.TenantId, discovered.TenantId);
        Assert.Equal(quiet.Artifact.Id, discovered.ArtifactId);
        await using var context = kit.Database.CreatePlatformContext();
        Assert.Equal(1, await context.ReplicaRepairOperations.CountAsync());
    });

    [Fact]
    public Task DiscoveryDifferentScopeWorkersUseRowLocksToPreventDuplicates() => BackupStoreHarness.WithIsolatedDatabaseAsync(async kit =>
    {
        var suite = new ManualReplicaRepairIntegrationTests(kit.Database);
        var setup = await suite.SetupAsync(verified: new HashSet<string> { "central", "google-drive" });
        var now = DateTimeOffset.UtcNow;
        var name = $"repair-scope-discovery-{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(kit.Database.ConnectionString) { ApplicationName = name }.ConnectionString;
        var options = new DbContextOptionsBuilder<BackupPlatform.Infrastructure.Persistence.PlatformDbContext>().UseNpgsql(connectionString).Options;
        await using var blocker = kit.Database.CreatePlatformContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM tenants WHERE "Id" = {setup.Agent.TenantId.Value} FOR NO KEY UPDATE
            """).ToListAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var scoped = new PostgreSqlReplicaRepairStore(options).DiscoverEligibleRepairsAsync(now, 10, deadline.Token, setup.Agent.TenantId);
        try
        {
            await suite.WaitForDatabaseLockAsync(name, deadline.Token);
            // Global and tenant-scoped calls have distinct process gates: only the held artifact lock prevents a second publication.
            Assert.Empty(await new PostgreSqlReplicaRepairStore(suite.World.Options()).DiscoverEligibleRepairsAsync(now, 10, deadline.Token));
        }
        finally
        {
            await transaction.RollbackAsync();
            await scoped.WaitAsync(deadline.Token);
        }
        var operation = Assert.Single(await scoped);
        await using var context = kit.Database.CreatePlatformContext();
        Assert.Equal(operation.Id, (await context.ReplicaRepairOperations.SingleAsync()).Id);
        Assert.Empty(await new PostgreSqlReplicaRepairStore(suite.World.Options()).DiscoverEligibleRepairsAsync(now, 10, deadline.Token));
    });

    private async Task WaitForDatabaseLockAsync(string name, CancellationToken cancellationToken)
    {
        await using var observer = new NpgsqlConnection(fixture.ConnectionString);
        await observer.OpenAsync(cancellationToken);
        await using var waiting = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                WHERE application_name = @name AND wait_event_type = 'Lock')
            """, observer);
        waiting.Parameters.AddWithValue("name", name);
        while (!Equals(true, await waiting.ExecuteScalarAsync(cancellationToken)))
            await Task.Delay(10, cancellationToken);
    }

    private async Task<IReadOnlyList<Guid>> SeedDiscoveryArtifactsAsync(
        MultiSetup setup, int count, DateTimeOffset createdAt, IReadOnlyCollection<string> verified)
    {
        await using var context = fixture.CreatePlatformContext();
        var baseAttempt = await context.BackupRunAttempts.AsNoTracking().SingleAsync(a => a.Id == setup.Artifact.AttemptId);
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var timestamp = createdAt.AddSeconds(i);
            var attempt = new BackupRunAttempt(Guid.NewGuid(), setup.Agent.TenantId, baseAttempt.RunId, setup.Agent.AgentId, i + 2, timestamp);
            var token = Guid.NewGuid();
            attempt.Claim(token, timestamp, TimeSpan.FromMinutes(5));
            attempt.Start(token, timestamp, "1.0", "1.0", "Linux");
            var artifact = new BackupArtifact(Guid.NewGuid(), setup.Agent.TenantId, attempt.Id, 100, Checksum, Checksum, "sha256", timestamp, "{}", "{}");
            foreach (var (provider, connection) in new[] { ("central", setup.Central), ("sftp", setup.Sftp), ("google-drive", setup.Drive) })
            {
                if (!verified.Contains(provider)) continue;
                artifact.AddReplica(new BackupReplica(Guid.NewGuid(), setup.Agent.TenantId, artifact.Id, provider,
                    provider == "central" ? "central" : connection.Id.ToString("D"), $"discovery-{artifact.Id:N}-{provider}", 100,
                    Checksum, "sha256", "Verified", "ReadBack", timestamp, timestamp,
                    BackupStorageExecutionLocation.Backend, null, storageConnectionId: connection.Id));
            }
            attempt.Complete(token, timestamp, new BackupPlatform.Domain.Backups.BackupCompletion(BackupStatus.Succeeded, null, artifact), new BackupRetentionPolicy());
            context.BackupRunAttempts.Add(attempt);
            context.BackupArtifacts.Add(artifact);
            ids.Add(artifact.Id);
        }
        await context.SaveChangesAsync();
        return ids;
    }

    [Fact]
    public async Task RepairSourceSwitchIsDurableTenantQualifiedAndLeaseFenced()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "google-drive" });
        var other = await SetupAsync(verified: new HashSet<string> { "central" });
        var now = DateTimeOffset.UtcNow;
        var created = Assert.Single(await setup.RepairStore.DiscoverEligibleRepairsAsync(now, 10, default, setup.Agent.TenantId));
        var claimed = (await setup.RepairStore.ClaimDueRepairAsync("source-switch", now, TimeSpan.FromMinutes(5), default, setup.Agent.TenantId))!;
        var alternate = setup.Artifact.Replicas.Single(replica => replica.StorageProvider == "google-drive");
        Assert.False(await setup.RepairStore.PinSourceAsync(setup.Agent.TenantId, created.Id, Guid.NewGuid(), alternate.Id, now, default));
        Assert.False(await setup.RepairStore.PinSourceAsync(setup.Agent.TenantId, created.Id, claimed.LeaseToken!.Value, other.Artifact.Replicas.Single().Id, now, default));
        Assert.True(await setup.RepairStore.PinSourceAsync(setup.Agent.TenantId, created.Id, claimed.LeaseToken.Value, alternate.Id, now, default));
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(alternate.Id, (await context.ReplicaRepairOperations.AsNoTracking().SingleAsync(entity => entity.Id == created.Id)).SourceReplicaId);
        Assert.False(await setup.RepairStore.PinSourceAsync(setup.Agent.TenantId, created.Id, claimed.LeaseToken.Value, alternate.Id, now.AddMinutes(5), default));
    }

    [Fact]
    public async Task DownloadCannotReadAlternativeWhenRetentionWinsItsPinRace()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "sftp" });
        var clock = new MutableClock();
        var store = new PostgreSqlDownloadStore(World.Options(), clock);
        var user = new User(UserId.New(), "Retention race owner");
        await using (var context = fixture.CreatePlatformContext())
        {
            context.Users.Add(user);
            context.TenantMemberships.Add(new TenantMembership(setup.Agent.TenantId, user.Id, TenantRole.Admin));
            await context.SaveChangesAsync();
        }
        var primary = setup.Artifact.Replicas.Single(replica => replica.StorageProvider == "central");
        var alternate = setup.Artifact.Replicas.Single(replica => replica.StorageProvider == "sftp");
        var session = (await store.CreateAsync(new(setup.Agent.TenantId, setup.Artifact.Id, primary.Id, user.Id.Value,
            "backup.bak", clock.GetUtcNow(), TimeSpan.FromMinutes(15)), default))!;
        var provider = (InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, primary.StorageProvider, primary.StorageTarget)!;
        provider.ShouldFailRead = true;
        provider.BeforeRead = async () =>
        {
            await new PostgreSqlBackupRetentionStore(World.Options()).RequestDeleteAsync(session.TenantId, session.ArtifactId, null, clock.GetUtcNow(), default);
        };
        var alternateOpened = false;
        ((InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, alternate.StorageProvider, alternate.StorageTarget)!).BeforeRead = () =>
        {
            alternateOpened = true;
            return Task.CompletedTask;
        };
        var service = new DownloadService(store, setup.Resolver, new NoAgentDownloadSource(), clock, NullLogger<DownloadService>.Instance);
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        await new DownloadStreamer(store, service, clock, NullLogger<DownloadStreamer>.Instance).StreamAsync(http, session);
        var failed = (await store.GetAsync(session.TenantId, session.Id, default))!;
        Assert.False(alternateOpened);
        Assert.Equal((DownloadState.Failed, primary.Id, 0L), (failed.State, failed.ReplicaId, failed.TransferredBytes));
        Assert.Equal(StatusCodes.Status409Conflict, http.Response.StatusCode);
        Assert.NotNull(failed.ReleasedAt);
    }

    [Fact]
    public async Task RestoreWithNoReadableCopyFailsWithoutHandoffAndReleasesPin()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "sftp" });
        var world = new RestoreWorld(fixture);
        await world.EnableRestoreAsync(setup.Agent);
        var now = DateTimeOffset.UtcNow;
        var store = new PostgreSqlRestoreStore(World.Options(), setup.Resolver);
        var operation = (await store.CreateAsync(RestoreWorld.Command(setup.Agent.TenantId, setup.Artifact.Id, setup.Agent.AgentId, now: now), default)).Operation!;
        foreach (var replica in setup.Artifact.Replicas)
            ((InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, replica.StorageProvider, replica.StorageTarget)!).ShouldFailRead = true;
        Assert.Null(await store.ClaimNextAsync(setup.Agent.TenantId, setup.Agent.AgentId, now, RestoreWorld.Lease, RestoreWorld.Unclaimed, default));
        var failed = await world.OperationAsync(operation.Id);
        Assert.Equal((RestoreStatus.Failed, RestoreTargetState.NotModified, RestoreFailureCodes.ReplicaUnavailable),
            (failed.Status, failed.TargetState, failed.FailureCode));
        Assert.False(failed.Pin.IsActive);
        Assert.Null(failed.StartedAt);
    }

    [Fact]
    public async Task HttpMidStreamFailureAbortsInsteadOfRetryingAnotherReplica()
    {
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "sftp" });
        var clock = new MutableClock();
        var store = new PostgreSqlDownloadStore(World.Options(), clock);
        var user = new User(UserId.New(), "HTTP fallback owner");
        await using (var context = fixture.CreatePlatformContext())
        {
            context.Users.Add(user);
            context.TenantMemberships.Add(new TenantMembership(setup.Agent.TenantId, user.Id, TenantRole.Admin));
            await context.SaveChangesAsync();
        }
        var primary = setup.Artifact.Replicas.Single(replica => replica.StorageProvider == "central");
        var session = (await store.CreateAsync(new(setup.Agent.TenantId, setup.Artifact.Id, primary.Id, user.Id.Value,
            "backup.bak", clock.GetUtcNow(), TimeSpan.FromMinutes(15)), default))!;
        var broken = new MidStreamFailure(Content);
        ((InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, primary.StorageProvider, primary.StorageTarget)!).ReadStream = _ => broken;
        var fallbackReads = 0;
        var fallback = setup.Artifact.Replicas.Single(replica => replica.StorageProvider == "sftp");
        ((InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, fallback.StorageProvider, fallback.StorageTarget)!).BeforeRead = () =>
        {
            Interlocked.Increment(ref fallbackReads);
            return Task.CompletedTask;
        };
        var service = new DownloadService(store, setup.Resolver, new NoAgentDownloadSource(), clock, NullLogger<DownloadService>.Instance);
        var streamer = new DownloadStreamer(store, service, clock, NullLogger<DownloadStreamer>.Instance);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.MapGet("/download", async (HttpContext http) =>
        {
            try { await streamer.StreamAsync(http, session); }
            finally { ended.TrySetResult(); }
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await app.StartAsync(timeout.Token);
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.GetAsync("/download", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal((long)Content.Length, response.Content.Headers.ContentLength);
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var prefix = new byte[2];
            await body.ReadExactlyAsync(prefix, timeout.Token);
            Assert.Equal(Content[..prefix.Length], prefix);
            broken.Continue.TrySetResult();
            await Assert.ThrowsAnyAsync<IOException>(() => body.CopyToAsync(Stream.Null, timeout.Token));
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var failed = (await store.GetAsync(session.TenantId, session.Id, default))!;
            Assert.Equal((DownloadState.Failed, primary.Id, "transfer_failed"), (failed.State, failed.ReplicaId, failed.FailureCode));
            Assert.Equal((long)Content.Length / 2, failed.TransferredBytes);
            Assert.NotNull(failed.ReleasedAt);
            Assert.False(failed.IsPinned(clock.GetUtcNow()));
            Assert.True(broken.Disposed);
            Assert.Equal(0, fallbackReads);
        }
        finally
        {
            broken.Continue.TrySetResult();
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(shutdown.Token);
        }
    }

    [Theory]
    [InlineData(1, "valid")]
    [InlineData(1, "bitflip")]
    [InlineData(1, "short")]
    [InlineData(1, "extra")]
    [InlineData(8, "valid")]
    [InlineData(8, "bitflip")]
    [InlineData(8, "short")]
    [InlineData(8, "extra")]
    [InlineData(131073, "valid")]
    [InlineData(131073, "bitflip")]
    [InlineData(131073, "short")]
    [InlineData(131073, "extra")]
    public async Task HttpNonSeekableDownloadRequiresExactLengthAndChecksum(int size, string variant)
    {
        var content = Enumerable.Range(0, size).Select(index => (byte)(index % 251)).ToArray();
        var setup = await SetupAsync(verified: new HashSet<string> { "central", "sftp" }, content: content);
        var clock = new MutableClock();
        var store = new PostgreSqlDownloadStore(World.Options(), clock);
        var user = new User(UserId.New(), "Integrity stream downloader");
        await using (var context = fixture.CreatePlatformContext())
        {
            context.Users.Add(user);
            context.TenantMemberships.Add(new TenantMembership(setup.Agent.TenantId, user.Id, TenantRole.Admin));
            await context.SaveChangesAsync();
        }
        var primary = setup.Artifact.Replicas.Single(replica => replica.StorageProvider == "central");
        var session = (await store.CreateAsync(new(setup.Agent.TenantId, setup.Artifact.Id, primary.Id, user.Id.Value,
            "backup.bak", clock.GetUtcNow(), TimeSpan.FromMinutes(15)), default))!;
        byte[] returned = variant switch
        {
            "short" => content[..^1],
            "extra" => [.. content, 255],
            _ => [.. content]
        };
        if (variant == "bitflip") returned[0] ^= 0xFF;
        var source = new NonSeekableStream(returned, size > 1 ? Math.Min(size, returned.Length) : null);
        ((InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, primary.StorageProvider, primary.StorageTarget)!).ReadStream = _ => source;
        var fallbackReads = 0;
        var fallback = setup.Artifact.Replicas.Single(replica => replica.StorageProvider == "sftp");
        ((InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, fallback.StorageProvider, fallback.StorageTarget)!).BeforeRead = () =>
        {
            Interlocked.Increment(ref fallbackReads);
            return Task.CompletedTask;
        };
        var service = new DownloadService(store, setup.Resolver, new NoAgentDownloadSource(), clock, NullLogger<DownloadService>.Instance);
        var streamer = new DownloadStreamer(store, service, clock, NullLogger<DownloadStreamer>.Instance);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.MapGet("/download", async (HttpContext http) =>
        {
            try { await streamer.StreamAsync(http, session); }
            finally { ended.TrySetResult(); }
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await app.StartAsync(timeout.Token);
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.GetAsync("/download", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (variant == "valid")
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal((long)size, response.Content.Headers.ContentLength);
                source.Continue.TrySetResult();
                Assert.Equal(content, await response.Content.ReadAsByteArrayAsync(timeout.Token));
            }
            else if (size == 1)
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Contains("integrity_mismatch", await response.Content.ReadAsStringAsync(timeout.Token));
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal((long)size, response.Content.Headers.ContentLength);
                await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
                var prefix = new byte[Math.Min(4, size - 1)];
                await body.ReadExactlyAsync(prefix, timeout.Token);
                Assert.Equal(returned[..prefix.Length], prefix);
                source.Continue.TrySetResult();
                await Assert.ThrowsAnyAsync<IOException>(() => body.CopyToAsync(Stream.Null, timeout.Token));
            }
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var persisted = (await store.GetAsync(session.TenantId, session.Id, default))!;
            var valid = variant == "valid";
            Assert.Equal((valid ? DownloadState.Completed : DownloadState.Failed, primary.Id, valid ? null : "integrity_mismatch"),
                (persisted.State, persisted.ReplicaId, persisted.FailureCode));
            Assert.Equal(valid ? (long)size : size - 1L, persisted.TransferredBytes);
            Assert.NotNull(persisted.ReleasedAt);
            Assert.False(persisted.IsPinned(clock.GetUtcNow()));
            Assert.True(source.Disposed);
            Assert.Equal(0, fallbackReads);
        }
        finally
        {
            source.Continue.TrySetResult();
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(shutdown.Token);
        }
    }

    [Theory]
    [InlineData("sftp")]
    [InlineData("google-drive")]
    [InlineData("central")]
    public async Task RepairNeverDeletesAnExistingObjectItDidNotCreate(string provider)
    {
        var setup = await SetupAsync(verified: new HashSet<string> { provider == "central" ? "google-drive" : "central" });
        var connectionId = provider == "sftp" ? setup.Sftp.Id : provider == "google-drive" ? setup.Drive.Id : setup.Central.Id;
        var target = provider == "central" ? "central" : connectionId.ToString("D");
        var destination = (InMemoryStorageProvider)setup.Resolver.Resolve(setup.Agent.TenantId, provider, target)!;
        byte[] existing = [42, 43, 44];
        destination.BeforePut = key => destination.Storage[key.Value] = existing;
        var started = await setup.RepairService.StartRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, new(StorageConnectionId: connectionId), null, default, executeInBackground: false);
        var result = await setup.RepairService.ExecuteRepairAsync(setup.Agent.TenantId, setup.Artifact.Id, started.Operation!.Id);
        Assert.Equal("Failed", result!.Status);
        Assert.Equal(ReplicaRepairFailureCodes.TargetConflict, result.FailureCode);
        Assert.Equal(existing, destination.Storage[result.TargetKey!]);
        Assert.Empty(destination.DeletedKeys);
        await using var context = fixture.CreatePlatformContext();
        var conflict = await context.BackupReplicas.SingleAsync(r => r.TenantId == setup.Agent.TenantId && r.Key == result.TargetKey);
        Assert.Equal(BackupReplicaStatus.Quarantined, conflict.Status);
        Assert.Equal(target, conflict.StorageTarget);
        Assert.False(conflict.IsRestorable(DateTimeOffset.UtcNow));
        Assert.True(BackupReplicaStorage.Occupies(conflict.Status));
        var retention = new PostgreSqlBackupRetentionStore(World.Options());
        await retention.RequestDeleteAsync(setup.Agent.TenantId, setup.Artifact.Id, null, DateTimeOffset.UtcNow, default);
        var deletions = await retention.ClaimDueDeletionsAsync("quarantine-test", DateTimeOffset.UtcNow.AddYears(1), TimeSpan.FromMinutes(1), 500, default);
        Assert.DoesNotContain(deletions, deletion => deletion.Key == conflict.Key);
        Assert.Equal(existing, destination.Storage[conflict.Key]);
    }

    private sealed class MidStreamFailure(byte[] bytes) : MemoryStream(bytes)
    {
        private int _reads;
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads >= 2)
            {
                await Continue.Task.WaitAsync(cancellationToken);
                throw new IOException("Simulated non-seekable provider failure.");
            }
            _reads++;
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, (int)Length / 4)], cancellationToken);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class NonSeekableStream(byte[] bytes, int? pauseAfterBytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Hold terminal reads until the client observes body bytes, rather than racing Kestrel's abort.
            if (pauseAfterBytes is { } threshold && Position >= threshold)
                await Continue.Task.WaitAsync(cancellationToken);
            var length = Math.Min(buffer.Length, Math.Min(4093, Math.Max(1, (int)Length / 3)));
            return await base.ReadAsync(buffer[..length], cancellationToken);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class NoAgentDownloadSource : IAgentDownloadSource
    {
        public Task<string?> GetUnavailableReasonAsync(TenantId tenantId, BackupReplica replica, CancellationToken cancellationToken) => Task.FromResult<string?>("agent_unavailable");
        public Task<Stream> OpenReadAsync(DownloadSession session, CancellationToken cancellationToken) => throw new InvalidOperationException("These replicas must be backend-read.");
        public Task ReleaseAsync(Guid sessionId, CancellationToken cancellationToken) => throw new InvalidOperationException("No agent spool was allocated.");
    }
}
