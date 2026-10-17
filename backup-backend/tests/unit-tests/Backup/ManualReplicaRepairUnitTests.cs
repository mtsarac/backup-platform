using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
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

    [Theory]
    [InlineData(1, 0.0, 30)]
    [InlineData(2, 0.0, 60)]
    [InlineData(3, 0.0, 120)]
    [InlineData(1, 1.0, 24)] // jitter reduces the delay by up to 20%
    [InlineData(10, 0.0, 3600)] // capped at 1 hour
    public void Scenario5_RetryPolicy_exponential_backoff_and_jitter_bounds(int attempt, double jitter, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), ReplicaRepairRetryPolicy.CalculateDelay(attempt, jitter));

    [Fact]
    public void Scenario6_RetryPolicy_max_attempts_and_exhaustion()
    {
        var op = new ReplicaRepairOperation(Guid.NewGuid(), Tenant, Guid.NewGuid(), Guid.NewGuid(), "sftp", null, T0, maxAttempts: 3);
        Assert.False(op.IsExhausted);
        Assert.Equal(0, op.AttemptCount);

        var t1 = Guid.NewGuid();
        op.Claim(t1, "worker-1", T0, TimeSpan.FromMinutes(5));
        Assert.Equal(1, op.AttemptCount);
        op.ScheduleRetry(t1, ReplicaRepairFailureCodes.StorageUnavailable, "fail 1", T0.AddSeconds(30), T0.AddSeconds(5));
        Assert.Equal(ReplicaRepairStatus.Pending, op.Status);
        Assert.False(op.IsExhausted);

        var t2 = Guid.NewGuid();
        op.Claim(t2, "worker-2", T0.AddSeconds(35), TimeSpan.FromMinutes(5));
        Assert.Equal(2, op.AttemptCount);
        op.ScheduleRetry(t2, ReplicaRepairFailureCodes.StorageUnavailable, "fail 2", T0.AddSeconds(90), T0.AddSeconds(40));
        Assert.Equal(ReplicaRepairStatus.Pending, op.Status);
        Assert.False(op.IsExhausted);

        var t3 = Guid.NewGuid();
        op.Claim(t3, "worker-3", T0.AddSeconds(95), TimeSpan.FromMinutes(5));
        Assert.Equal(3, op.AttemptCount);
        op.Fail(t3, ReplicaRepairFailureCodes.StorageUnavailable, "exhausted", T0.AddSeconds(100));
        Assert.Equal(ReplicaRepairStatus.Failed, op.Status);
        Assert.True(op.IsExhausted);
        Assert.Null(op.NextRetryAt);
    }

    [Theory]
    [InlineData(ReplicaRepairFailureCodes.StorageUnavailable, true)]
    [InlineData(ReplicaRepairFailureCodes.OperationTimedOut, true)]
    [InlineData(ReplicaRepairFailureCodes.StorageWriteFailed, true)]
    [InlineData(ReplicaRepairFailureCodes.SourceInaccessible, true)]
    [InlineData(ReplicaRepairFailureCodes.LeaseLost, true)]
    [InlineData("lease_expired", true)]
    [InlineData(ReplicaRepairFailureCodes.InternalError, true)]
    [InlineData(ReplicaRepairFailureCodes.ArtifactExpired, false)]
    [InlineData(ReplicaRepairFailureCodes.AlreadyVerified, false)]
    [InlineData(ReplicaRepairFailureCodes.Cancelled, false)]
    [InlineData(ReplicaRepairFailureCodes.UnsupportedSource, false)]
    [InlineData(ReplicaRepairFailureCodes.SourceChecksumMismatch, false)]
    [InlineData(ReplicaRepairFailureCodes.QuotaExceeded, false)]
    [InlineData(ReplicaRepairFailureCodes.DestinationNotConfigured, false)]
    [InlineData("storage_connection_disabled", false)]
    public void Scenario7_Transient_vs_permanent_failure_classification(string code, bool transient) =>
        Assert.Equal(transient, ReplicaRepairRetryPolicy.IsTransient(code));

    [Fact]
    public void Scenario16_AddOrphanReplica_registers_unverified_replica_bound_for_cleanup()
    {
        var (artifact, source) = CreateArtifact("central");
        var orphan = new BackupReplica(
            Guid.NewGuid(),
            Tenant,
            artifact.Id,
            "sftp",
            "sftp-target",
            $"{Tenant.Value:N}/job/run/attempt/failed.bak",
            Size,
            Checksum,
            "sha256",
            "Failed",
            "RepairCleanupFailed",
            T0,
            verifiedAt: null,
            BackupStorageExecutionLocation.Backend,
            owningAgentId: null,
            storageConnectionId: Guid.NewGuid());

        artifact.AddOrphanReplica(orphan, T0);

        Assert.Equal(2, artifact.Replicas.Count);
        var registered = artifact.Replicas.Single(r => r.Id == orphan.Id);
        Assert.Equal("Failed", registered.VerificationState);
        Assert.Equal(BackupReplicaStatus.DeletePending, registered.Status);
        Assert.True(BackupReplicaStorage.Occupies(registered.Status));
    }

    [Fact]
    public void Scenario12_Stale_recovery_recovers_to_pending_when_attempts_remain()
    {
        var op = new ReplicaRepairOperation(Guid.NewGuid(), Tenant, Guid.NewGuid(), Guid.NewGuid(), "sftp", null, T0, maxAttempts: 3);
        var token = Guid.NewGuid();
        op.Claim(token, "crashed-worker", T0, TimeSpan.FromMinutes(2));
        Assert.Equal(1, op.AttemptCount);
        Assert.Equal(ReplicaRepairStatus.InProgress, op.Status);

        // Worker crashed, lease expired at T0 + 2m
        var now = T0.AddMinutes(3);
        op.RecoverStale("lease_expired", "Worker lost", now.AddSeconds(30), now);

        Assert.Equal(ReplicaRepairStatus.Pending, op.Status);
        Assert.Equal(now.AddSeconds(30), op.NextRetryAt);
        Assert.False(op.IsExhausted);

        // If max attempts reached, recovers to Failed
        var token2 = Guid.NewGuid();
        op.Claim(token2, "worker-2", now.AddSeconds(30), TimeSpan.FromMinutes(2));
        var token3 = Guid.NewGuid();
        op.ScheduleRetry(token2, "lease_expired", "fail", now.AddMinutes(2), now.AddMinutes(1));
        op.Claim(token3, "worker-3", now.AddMinutes(2), TimeSpan.FromMinutes(2));
        Assert.Equal(3, op.AttemptCount);

        op.RecoverStale("lease_expired", "Exhausted", now.AddMinutes(5), now.AddMinutes(5));
        Assert.Equal(ReplicaRepairStatus.Failed, op.Status);
        Assert.True(op.IsExhausted);
        Assert.Null(op.NextRetryAt);
    }
}
