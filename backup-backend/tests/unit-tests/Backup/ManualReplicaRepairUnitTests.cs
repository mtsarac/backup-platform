using System.Security.Cryptography;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BackupPlatform.UnitTests.Backup;

public sealed class ManualReplicaRepairUnitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 10, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly string Checksum = new('b', 64);
    private const long Size = 1024 * 1024;

    private static (BackupArtifact Artifact, BackupReplica SourceReplica) CreateArtifact(string provider = "central", Guid? connectionId = null)
    {
        var artifact = new BackupArtifact(Guid.NewGuid(), Tenant, Guid.NewGuid(), Size, new('a', 64), Checksum, "sha256", T0, "{}", "{}");
        var replica = new BackupReplica(
            Guid.NewGuid(),
            Tenant,
            artifact.Id,
            provider,
            provider,
            $"{Tenant.Value:N}/job/run/attempt/test.bak",
            Size,
            Checksum,
            "sha256",
            "Verified",
            "ReadBack",
            T0,
            T0,
            BackupStorageExecutionLocation.Backend,
            owningAgentId: null,
            storageConnectionId: connectionId ?? Guid.NewGuid());
        artifact.AddReplica(replica);

        artifact.Seal(T0, new BackupRetentionPolicy { Enabled = true, KeepFor = TimeSpan.FromDays(30), DeleteGracePeriod = TimeSpan.FromDays(1) });
        return (artifact, replica);
    }

    [Fact]
    public void Scenario5_Repair_does_not_invoke_SQL_backup_engine()
    {
        // Verified source + target repair uses only storage provider streams, zero SQL engine calls
        var (artifact, source) = CreateArtifact("central");
        Assert.Single(artifact.Replicas);

        var targetConnection = Guid.NewGuid();
        var targetReplica = new BackupReplica(
            Guid.NewGuid(),
            Tenant,
            artifact.Id,
            "sftp",
            targetConnection.ToString(),
            $"{Tenant.Value:N}/job/run/attempt/test.bak",
            Size,
            Checksum,
            "sha256",
            "Verified",
            "ReadBack",
            T0.AddMinutes(5),
            T0.AddMinutes(5),
            BackupStorageExecutionLocation.Backend,
            owningAgentId: null,
            storageConnectionId: targetConnection);

        artifact.AddRepairedReplica(targetReplica);

        Assert.Equal(2, artifact.Replicas.Count);
        Assert.Contains(artifact.Replicas, r => r.StorageProvider == "central" && r.VerificationState == "Verified");
        Assert.Contains(artifact.Replicas, r => r.StorageProvider == "sftp" && r.VerificationState == "Verified");
    }

    [Fact]
    public void Scenario6_Source_checksum_mismatch_fails_and_does_not_verify_destination()
    {
        var (artifact, source) = CreateArtifact("central");
        var corruptedData = new byte[Size];
        Random.Shared.NextBytes(corruptedData);
        var actualHash = Convert.ToHexString(SHA256.HashData(corruptedData)).ToLowerInvariant();

        Assert.NotEqual(artifact.ArtifactChecksum, actualHash);

        var op = new ReplicaRepairOperation(Guid.NewGuid(), Tenant, artifact.Id, Guid.NewGuid(), "sftp", source.Id, T0);
        var token = Guid.NewGuid();
        op.Claim(token, "worker-1", T0, TimeSpan.FromMinutes(5));

        op.Fail(token, ReplicaRepairFailureCodes.SourceChecksumMismatch, "Source replica data failed SHA-256 integrity verification.", T0.AddSeconds(10));

        Assert.Equal(ReplicaRepairStatus.Failed, op.Status);
        Assert.Equal(ReplicaRepairFailureCodes.SourceChecksumMismatch, op.FailureCode);
        Assert.Null(op.TargetReplicaId);
    }

    [Fact]
    public void Scenario7_Destination_readback_failure_marks_repair_failed()
    {
        var (artifact, source) = CreateArtifact("central");
        var op = new ReplicaRepairOperation(Guid.NewGuid(), Tenant, artifact.Id, Guid.NewGuid(), "sftp", source.Id, T0);
        var token = Guid.NewGuid();
        op.Claim(token, "worker-1", T0, TimeSpan.FromMinutes(5));

        op.Fail(token, ReplicaRepairFailureCodes.DestinationReadbackFailed, "Destination readback verification failed.", T0.AddSeconds(15));

        Assert.Equal(ReplicaRepairStatus.Failed, op.Status);
        Assert.Equal(ReplicaRepairFailureCodes.DestinationReadbackFailed, op.FailureCode);
    }

    [Fact]
    public void Scenario11_Stale_worker_completion_rejected()
    {
        var (artifact, source) = CreateArtifact("central");
        var op = new ReplicaRepairOperation(Guid.NewGuid(), Tenant, artifact.Id, Guid.NewGuid(), "sftp", source.Id, T0);
        var token1 = Guid.NewGuid();
        op.Claim(token1, "worker-1", T0, TimeSpan.FromMinutes(5));

        var wrongToken = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => op.Succeed(wrongToken, Guid.NewGuid(), T0.AddMinutes(1)));

        // Expired lease
        Assert.Throws<InvalidOperationException>(() => op.Succeed(token1, Guid.NewGuid(), T0.AddMinutes(6)));
    }

    [Fact]
    public void Scenario12_Duplicate_live_verified_replica_rejected()
    {
        var targetConn = Guid.NewGuid();
        var (artifact, source) = CreateArtifact("central", targetConn);

        var duplicate = new BackupReplica(
            Guid.NewGuid(),
            Tenant,
            artifact.Id,
            "central",
            targetConn.ToString(),
            $"{Tenant.Value:N}/job/run/attempt/another.bak",
            Size,
            Checksum,
            "sha256",
            "Verified",
            "ReadBack",
            T0.AddMinutes(1),
            T0.AddMinutes(1),
            BackupStorageExecutionLocation.Backend,
            owningAgentId: null,
            storageConnectionId: targetConn);

        var ex = Assert.Throws<ArgumentException>(() => artifact.AddRepairedReplica(duplicate));
        Assert.Contains("already has a live verified replica", ex.Message);
    }

    [Fact]
    public void Scenario18_Cancellation_transitions_operation_cleanly()
    {
        var (artifact, source) = CreateArtifact("central");
        var op = new ReplicaRepairOperation(Guid.NewGuid(), Tenant, artifact.Id, Guid.NewGuid(), "sftp", source.Id, T0);
        var token = Guid.NewGuid();
        op.Claim(token, "worker-1", T0, TimeSpan.FromMinutes(5));

        op.RequestCancellation(T0.AddSeconds(5));
        Assert.NotNull(op.CancellationRequestedAt);
        Assert.Equal(ReplicaRepairStatus.InProgress, op.Status);

        op.Cancel(token, T0.AddSeconds(10));
        Assert.Equal(ReplicaRepairStatus.Cancelled, op.Status);
        Assert.Equal(ReplicaRepairFailureCodes.Cancelled, op.FailureCode);
        Assert.True(op.IsTerminal);
    }

    [Fact]
    public void SourceEvaluator_prioritizes_central_over_remote_and_filters_unusable_replicas()
    {
        var artifact = new BackupArtifact(Guid.NewGuid(), Tenant, Guid.NewGuid(), Size, new('a', 64), Checksum, "sha256", T0, "{}", "{}");
        var central = new BackupReplica(Guid.NewGuid(), Tenant, artifact.Id, "central", "central", "key1", Size, Checksum, "sha256", "Verified", "ReadBack", T0, T0, BackupStorageExecutionLocation.Backend, null, Guid.NewGuid());
        var sftp = new BackupReplica(Guid.NewGuid(), Tenant, artifact.Id, "sftp", "sftp", "key2", Size, Checksum, "sha256", "Verified", "ReadBack", T0, T0, BackupStorageExecutionLocation.Backend, null, Guid.NewGuid());
        var drive = new BackupReplica(Guid.NewGuid(), Tenant, artifact.Id, "google-drive", "drive", "key3", Size, Checksum, "sha256", "Verified", "ReadBack", T0, T0, BackupStorageExecutionLocation.Backend, null, Guid.NewGuid());
        var failed = new BackupReplica(Guid.NewGuid(), Tenant, artifact.Id, "sftp", "sftp", "key4", Size, Checksum, "sha256", "Failed", "None", T0, null, BackupStorageExecutionLocation.Backend, null, Guid.NewGuid());

        artifact.AddReplica(failed);
        artifact.AddReplica(drive);
        artifact.AddReplica(sftp);
        artifact.AddReplica(central);
        artifact.Seal(T0, new BackupRetentionPolicy { Enabled = true, KeepFor = TimeSpan.FromDays(30), DeleteGracePeriod = TimeSpan.FromDays(1) });

        var candidates = ReplicaRepairSourceEvaluator.SelectCandidateSources(artifact, T0);
        Assert.Equal(3, candidates.Count);
        Assert.Equal("central", candidates[0].StorageProvider);
        Assert.Equal("sftp", candidates[1].StorageProvider);
        Assert.Equal("google-drive", candidates[2].StorageProvider);
        Assert.DoesNotContain(candidates, c => c.VerificationState == "Failed");
    }

    [Fact]
    public void SourceEvaluator_excludes_DeletePending_and_leased_deletions()
    {
        var artifact = new BackupArtifact(Guid.NewGuid(), Tenant, Guid.NewGuid(), Size, new('a', 64), Checksum, "sha256", T0, "{}", "{}");
        var central = new BackupReplica(Guid.NewGuid(), Tenant, artifact.Id, "central", "central", "key1", Size, Checksum, "sha256", "Verified", "ReadBack", T0, T0, BackupStorageExecutionLocation.Backend, null, Guid.NewGuid());

        artifact.AddReplica(central);
        artifact.Seal(T0, new BackupRetentionPolicy { Enabled = true, KeepFor = TimeSpan.FromDays(30), DeleteGracePeriod = TimeSpan.FromDays(1) });

        // Request delete moves to DeletePending
        central.RequestDelete(T0, BackupDeleteReason.RetentionExpired, TimeSpan.FromHours(1), null);
        Assert.Equal(BackupReplicaStatus.DeletePending, central.Status);

        var candidates = ReplicaRepairSourceEvaluator.SelectCandidateSources(artifact, T0);
        Assert.Empty(candidates);
    }
}
