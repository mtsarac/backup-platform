using System.Text.Json;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using Xunit;
using BackupCompletion = BackupPlatform.Domain.Backups.BackupCompletion;

namespace BackupPlatform.UnitTests.Backup;

/// <summary>Replication compliance is a projection of stored facts, independent of the run's status; completion validation is bounded by the run's snapshot.</summary>
public sealed class ReplicationComplianceUnitTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly string Hash = new('b', 64);
    private static readonly string Source = new('a', 64);
    private const string Key = "t/j/r/a/db_20261010T000000Z_aaaaaaaa_FULL.bak";

    private static readonly Guid Central = Guid.NewGuid();
    private static readonly Guid Sftp = Guid.NewGuid();
    private static readonly Guid Drive = Guid.NewGuid();
    private static readonly Guid Stranger = Guid.NewGuid();

    private static BackupConfiguration Snapshot(int required) => new("mssql", "Sales", "sql.local", 1433, true, null, true, false, "default", "", true, "Preferred", "Preferred")
    {
        Destinations =
        [
            new BackupDestination("central", Central, true),
            new BackupDestination("sftp", Sftp),
            new BackupDestination("google-drive", Drive)
        ],
        RequiredVerifiedReplicaCount = required
    };

    private sealed record Scenario(BackupRun Run, Guid Token);

    /// <summary>A run whose attempt succeeded with verified copies on <paramref name="verified"/> and a recorded failure on every other destination.</summary>
    private static BackupRun SucceededRun(int required, params string[] verified)
    {
        var snapshot = Snapshot(required);
        var run = new BackupRun(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), AgentId.New(), snapshot, "key", At);
        run.QueueFirstAttempt(Guid.NewGuid(), At);
        var token = Guid.NewGuid();
        run.Claim(token, At, TimeSpan.FromMinutes(2));
        run.Start(token, At, "1.0", "1.0", "Linux");
        var attempt = run.CurrentAttempt;
        var artifact = new BackupArtifact(Guid.NewGuid(), run.TenantId, attempt.Id, 100, Source, Hash, "sha256", At, "{}", "{}");
        var outcomes = new List<BackupDestinationOutcome>();
        foreach (var destination in snapshot.Destinations)
        {
            var connection = destination.StorageConnectionId!.Value;
            if (verified.Contains(destination.StorageProvider))
            {
                var replica = new BackupReplica(Guid.NewGuid(), run.TenantId, artifact.Id, destination.StorageProvider, destination.StorageProvider == "central" ? "central" : connection.ToString("D"),
                    Key, 100, Hash, "sha256", "Verified", "ReadBack", At, At, destination.StorageExecutionLocation, null, connection);
                artifact.AddReplica(replica);
                outcomes.Add(new BackupDestinationOutcome(Guid.NewGuid(), run.TenantId, run.Id, attempt.Id, destination.StorageProvider, connection, destination.IsPrimary,
                    destination.StorageExecutionLocation, "Verified", "Succeeded", "Verified", null, Key, false, At, At, replica.Id));
            }
            else
            {
                outcomes.Add(new BackupDestinationOutcome(Guid.NewGuid(), run.TenantId, run.Id, attempt.Id, destination.StorageProvider, connection, destination.IsPrimary,
                    destination.StorageExecutionLocation, "Failed", "Failed", "NotRun", "StorageUnavailable", null, false, At, At, null));
            }
        }
        run.Complete(token, At, new BackupCompletion(BackupStatus.Succeeded, null, artifact, outcomes));
        return run;
    }

    [Theory]
    // 3 destinations, 2 required: the four rows of the specification.
    [InlineData(2, new[] { "central", "sftp", "google-drive" }, "Healthy", true)]
    [InlineData(2, new[] { "central", "sftp" }, "Degraded", true)]
    [InlineData(2, new[] { "central" }, "InsufficientReplicas", false)]
    // One required: a lone copy satisfies the policy but is still Degraded, never Healthy.
    [InlineData(1, new[] { "sftp" }, "Degraded", true)]
    public void Compliance_follows_verified_copies_against_the_required_count(int required, string[] verified, string status, bool satisfied)
    {
        var run = SucceededRun(required, verified);

        var health = BackupReplicationEvaluator.ForAttempt(run.Configuration, run.CurrentAttempt)!;

        Assert.Equal(status, health.Status);
        Assert.Equal(satisfied, health.PolicySatisfied);
        Assert.Equal(verified.Length, health.VerifiedReplicas);
        Assert.Equal(3, health.DestinationCount);
        Assert.Equal(required, health.RequiredVerifiedReplicas);
        // The run itself is a success in every one of these rows: replication compliance is a separate answer.
        Assert.Equal(BackupStatus.Succeeded, run.Status);
        var artifactHealth = BackupReplicationEvaluator.ForArtifact(required, 3, run.CurrentAttempt.Artifacts.Single().Replicas);
        Assert.Equal(status, artifactHealth.Status);
    }

    [Fact]
    public void A_degraded_artifact_stays_available_and_names_which_destination_failed()
    {
        var run = SucceededRun(2, "central", "sftp");

        var health = BackupReplicationEvaluator.ForAttempt(run.Configuration, run.CurrentAttempt)!;

        Assert.Equal(BackupArtifactState.Available, run.CurrentAttempt.Artifacts.Single().State);
        var drive = Assert.Single(health.Destinations, d => d.StorageProvider == "google-drive");
        Assert.Equal("Failed", drive.State);
        Assert.Equal("StorageUnavailable", drive.FailureCode);
        Assert.Equal(["Verified", "Verified"], health.Destinations.Where(d => d.StorageProvider != "google-drive").Select(d => d.State));
    }

    [Fact]
    public void When_every_destination_failed_nothing_is_usable_and_the_attempt_is_unavailable()
    {
        var snapshot = Snapshot(2);
        var run = new BackupRun(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), AgentId.New(), snapshot, "key", At, BackupRunTestSupport.Policy(1));
        run.QueueFirstAttempt(Guid.NewGuid(), At);
        var token = Guid.NewGuid();
        run.Claim(token, At, TimeSpan.FromMinutes(2));
        run.Start(token, At, "1.0", "1.0", "Linux");
        var outcomes = snapshot.Destinations.Select(d => new BackupDestinationOutcome(Guid.NewGuid(), run.TenantId, run.Id, run.CurrentAttempt.Id, d.StorageProvider,
            d.StorageConnectionId!.Value, d.IsPrimary, d.StorageExecutionLocation, "Failed", "Failed", "NotRun", "StorageWriteFailed", null, false, At, At, null)).ToArray();
        run.Complete(token, At, new BackupCompletion(BackupStatus.Failed, "StorageWriteFailed", null, outcomes));

        var health = BackupReplicationEvaluator.ForAttempt(run.Configuration, run.CurrentAttempt)!;

        Assert.Equal(BackupStatus.Failed, run.Status);
        Assert.Empty(run.CurrentAttempt.Artifacts);
        Assert.Equal("Unavailable", health.Status);
        Assert.Equal(0, health.VerifiedReplicas);
        Assert.False(health.PolicySatisfied);
    }

    [Fact]
    public void A_failure_before_any_transfer_has_no_replication_to_judge()
    {
        var run = BackupRunTestSupport.NewRun(BackupRunTestSupport.Policy(1));
        BackupRunTestSupport.Fail(run, "SqlBackupFailed", At);

        Assert.Null(BackupReplicationEvaluator.ForAttempt(run.Configuration, run.CurrentAttempt));
    }

    [Fact]
    public void A_legacy_single_destination_run_without_outcomes_is_healthy()
    {
        var run = BackupRunTestSupport.NewRun();
        var token = BackupRunTestSupport.Begin(run, At);
        BackupRunTestSupport.Succeed(run, token, At);

        var health = BackupReplicationEvaluator.ForAttempt(run.Configuration, run.CurrentAttempt)!;

        Assert.Equal("Healthy", health.Status);
        Assert.Equal(1, health.DestinationCount);
        Assert.Equal(1, health.VerifiedReplicas);
    }

    [Fact]
    public void A_replica_that_was_deleted_no_longer_counts_as_verified()
    {
        var run = SucceededRun(2, "central", "sftp", "google-drive");
        var artifact = run.CurrentAttempt.Artifacts.Single();
        artifact.RequestDelete(At, null);

        // Soft-deleted copies are still usable inside their grace period: compliance does not flap on a pending deletion.
        Assert.Equal("Healthy", BackupReplicationEvaluator.ForArtifact(2, 3, artifact.Replicas).Status);
    }

    [Fact]
    public void An_artifact_cannot_hold_two_live_verified_copies_on_one_destination()
    {
        var artifact = new BackupArtifact(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), 100, Source, Hash, "sha256", At, "{}", "{}");
        BackupReplica Replica(string key) => new(Guid.NewGuid(), artifact.TenantId, artifact.Id, "sftp", Sftp.ToString("D"), key, 100, Hash, "sha256", "Verified", "ReadBack", At, At,
            BackupStorageExecutionLocation.Backend, null, Sftp);
        artifact.AddReplica(Replica("a/b/c/d/one_20261010T000000Z_aaaaaaaa_FULL.bak"));

        Assert.Throws<ArgumentException>(() => artifact.AddReplica(Replica("a/b/c/d/two_20261010T000000Z_aaaaaaaa_FULL.bak")));
    }

    [Fact]
    public void An_unverified_orphan_is_never_usable_and_never_keeps_an_artifact_available()
    {
        var artifact = new BackupArtifact(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), 100, Source, Hash, "sha256", At, "{}", "{}");
        artifact.AddReplica(new BackupReplica(Guid.NewGuid(), artifact.TenantId, artifact.Id, "sftp", Sftp.ToString("D"), Key, 100, Hash, "sha256", "Failed", "None", At, null,
            BackupStorageExecutionLocation.Backend, null, Sftp));

        var orphan = Assert.Single(artifact.Replicas);

        Assert.False(orphan.IsUsable);
        Assert.NotEqual(BackupArtifactState.Available, artifact.State);
        Assert.Equal("Unavailable", BackupReplicationEvaluator.ForArtifact(1, 1, artifact.Replicas).Status);
    }

    [Theory]
    [InlineData("Verified", "Succeeded", "Verified", null, false, false)]  // verified, no replica reference
    [InlineData("Failed", "Failed", "NotRun", null, false, false)]         // failed without a reason
    [InlineData("Failed", "Succeeded", "Verified", "X", false, false)]     // failed yet "verified"
    [InlineData("Failed", "Failed", "NotRun", "X", false, true)]           // may remain without an uploaded object
    [InlineData("Cancelled", "NotAttempted", "NotRun", "StorageUnavailable", false, false)] // cancelled must say Cancelled
    public void An_inconsistent_destination_outcome_is_rejected(string state, string upload, string verification, string? code, bool withReplica, bool mayRemain)
    {
        Assert.Throws<ArgumentException>(() => new BackupDestinationOutcome(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), Guid.NewGuid(), "sftp", Sftp, false,
            BackupStorageExecutionLocation.Backend, state, upload, verification, code, state == "Verified" ? Key : null, mayRemain, At, At, withReplica ? Guid.NewGuid() : null));
    }

    // ---- completion validation against the run's snapshot -------------------------------------------------------------------------------------

    private static string Metadata => JsonSerializer.Serialize(new
    {
        engine = "mssql", databaseName = "Sales", backupType = "Full", backupStartedAt = At.AddMinutes(-1), backupFinishedAt = At,
        backupVerification = new { state = "Passed", method = "RESTORE VERIFYONLY" }
    });

    private const string Options = "{\"copyOnly\":true,\"checksum\":true,\"compression\":false}";

    private static BackupTaskReplica ReplicaOf(string provider, Guid connection) => new()
    {
        StorageProvider = provider, StorageTarget = provider == "central" ? "central" : connection.ToString("D"), StorageConnectionId = connection, Key = Key, SizeBytes = 100,
        Checksum = Hash, VerificationState = "Verified", VerificationMethod = "ReadBack", CreatedAt = At.AddSeconds(-1), VerifiedAt = At
    };

    private static BackupTaskDestinationResult Verified(string provider, Guid connection) => new()
    {
        StorageProvider = provider, StorageConnectionId = connection, State = "Verified", Upload = "Succeeded", Verification = "Verified", Key = Key,
        StartedAt = At.AddSeconds(-1), CompletedAt = At
    };

    private static BackupTaskDestinationResult Failed(string provider, Guid connection, string code = "StorageUnavailable") => new()
    {
        StorageProvider = provider, StorageConnectionId = connection, State = "Failed", Upload = "Failed", Verification = "NotRun", FailureCode = code,
        StartedAt = At.AddSeconds(-1), CompletedAt = At
    };

    private static BackupTaskCompletionRequest Completion(IReadOnlyList<BackupTaskReplica> replicas, IReadOnlyList<BackupTaskDestinationResult> destinations) => new()
    {
        LeaseToken = Guid.NewGuid(), Status = "Succeeded",
        Artifact = new BackupTaskArtifact
        {
            SizeBytes = 100, SourceChecksum = Source, ArtifactChecksum = Hash, CreatedAt = At, SourceMetadataJson = Metadata, AppliedOptionsJson = Options, Replicas = replicas
        },
        Destinations = destinations
    };

    [Theory]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public void CompletionAcceptsLeaseSizedKeysWithoutRemovingTheKeyLengthCap(int length, bool accepted)
    {
        var key = string.Join('/', Enumerable.Repeat(new string('a', 128), 7).Append(new string('b', length - 903)));
        var request = Completion(
            [ReplicaOf("central", Central) with { Key = key }, ReplicaOf("sftp", Sftp)],
            [Verified("central", Central) with { Key = key }, Verified("sftp", Sftp), Failed("google-drive", Drive)]);

        Assert.Equal(accepted, BackupInputValidation.IsValidCompletion(request, Snapshot(2)));
    }

    [Fact]
    public void A_complete_partial_success_is_accepted()
    {
        var request = Completion([ReplicaOf("central", Central), ReplicaOf("sftp", Sftp)], [Verified("central", Central), Verified("sftp", Sftp), Failed("google-drive", Drive)]);

        Assert.True(BackupInputValidation.IsValidCompletion(request, Snapshot(2)));
    }

    public static TheoryData<string, BackupTaskCompletionRequest, int> RejectedCompletions => new()
    {
        { "replica on a destination outside the snapshot", Completion([ReplicaOf("central", Central), ReplicaOf("sftp", Stranger)], [Verified("central", Central), Verified("sftp", Stranger), Failed("google-drive", Drive)]), 2 },
        { "a destination left unaccounted for", Completion([ReplicaOf("central", Central)], [Verified("central", Central), Failed("sftp", Sftp)]), 1 },
        { "no destinations reported", Completion([ReplicaOf("central", Central)], []), 1 },
        { "a destination reported twice", Completion([ReplicaOf("central", Central)], [Verified("central", Central), Verified("central", Central), Failed("sftp", Sftp)]), 1 },
        { "verified without its replica", Completion([ReplicaOf("central", Central)], [Verified("central", Central), Verified("sftp", Sftp), Failed("google-drive", Drive)]), 1 },
        { "failure code that is not known", Completion([ReplicaOf("central", Central)], [Verified("central", Central), Failed("sftp", Sftp, "made-up"), Failed("google-drive", Drive)]), 1 },
    };

    [Theory]
    [MemberData(nameof(RejectedCompletions))]
    public void A_completion_that_misreports_the_snapshot_destinations_is_rejected(string reason, BackupTaskCompletionRequest request, int required)
    {
        Assert.False(BackupInputValidation.IsValidCompletion(request, Snapshot(required)), reason);
    }

    [Fact]
    public void A_failed_run_may_report_all_failed_destinations_or_none_but_never_a_verified_one()
    {
        BackupTaskCompletionRequest Failure(params BackupTaskDestinationResult[] destinations) =>
            new() { LeaseToken = Guid.NewGuid(), Status = "Failed", FailureCode = "StorageWriteFailed", Destinations = destinations };

        Assert.True(BackupInputValidation.IsValidCompletion(Failure(), Snapshot(1)));
        Assert.True(BackupInputValidation.IsValidCompletion(Failure(Failed("central", Central), Failed("sftp", Sftp), Failed("google-drive", Drive)), Snapshot(1)));
        Assert.False(BackupInputValidation.IsValidCompletion(Failure(Failed("central", Central), Verified("sftp", Sftp), Failed("google-drive", Drive)), Snapshot(1)));
        Assert.False(BackupInputValidation.IsValidCompletion(Failure(Failed("central", Central)), Snapshot(1)));
    }

    [Theory]
    [InlineData("Failed", "StorageWriteFailed")]
    [InlineData("Cancelled", "Cancelled")]
    public void Failed_or_cancelled_orphans_require_valid_artifact_metadata_and_never_publish_verified_copies(string status, string failure)
    {
        var orphan = Failed("sftp", Sftp) with { Upload = "Succeeded", Verification = "Failed", Key = Key, ObjectMayRemain = true };
        var request = Completion([], [Failed("central", Central), orphan, Failed("google-drive", Drive)]) with { Status = status, FailureCode = failure };
        Assert.True(BackupInputValidation.IsValidCompletion(request, Snapshot(2)));
        Assert.False(BackupInputValidation.IsValidCompletion(request with { Artifact = null }, Snapshot(2)));
        Assert.False(BackupInputValidation.IsValidCompletion(request with { Artifact = request.Artifact! with { SizeBytes = 0 } }, Snapshot(2)));
        Assert.False(BackupInputValidation.IsValidCompletion(request with { Artifact = request.Artifact! with { Replicas = [ReplicaOf("sftp", Sftp)] } }, Snapshot(2)));
        Assert.False(BackupInputValidation.IsValidCompletion(request with { Destinations = [Failed("central", Central), Failed("sftp", Sftp), Failed("google-drive", Drive)] }, Snapshot(2)));
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void Jobs_cannot_exceed_the_completion_destination_limit(int count, bool allowed)
    {
        var configuration = BackupOrchestrator.ToWire(Snapshot(1)) with
        {
            Destinations = [.. Enumerable.Range(0, count).Select(i => new BackupDestinationContract
                { StorageProvider = "sftp", StorageConnectionId = Guid.NewGuid(), IsPrimary = i == 0, StorageExecutionLocation = "Backend" })]
        };
        var request = new CreateBackupJobRequest("bounded", Guid.NewGuid(), configuration);
        if (allowed) BackupInputValidation.Validate(request);
        else Assert.Throws<InvalidBackupJobException>(() => BackupInputValidation.Validate(request));
    }

    [Fact]
    public void A_completion_with_an_artifact_must_report_every_snapshot_destination()
    {
        var single = Snapshot(1) with { Destinations = [new BackupDestination("central", Central, true)] };
        var reported = Completion([ReplicaOf("central", Central)], [Verified("central", Central)]);

        Assert.True(BackupInputValidation.IsValidCompletion(reported, single));
        Assert.False(BackupInputValidation.IsValidCompletion(reported with { Destinations = [] }, single));
    }

    [Fact]
    public void A_configuration_without_destinations_is_invalid_and_old_json_is_not_reinterpreted()
    {
        const string oldJson = "{\"Engine\":\"mssql\",\"Database\":\"Sales\",\"Host\":\"sql.local\",\"Port\":1433,\"IntegratedSecurity\":true,\"DatabaseCredentialReference\":null,\"Encrypt\":true,\"TrustServerCertificate\":false," +
            "\"StagingReference\":\"default\",\"StorageProvider\":\"central\",\"StoragePrefix\":\"\",\"CopyOnly\":true,\"ChecksumPolicy\":\"Preferred\",\"CompressionPolicy\":\"Preferred\",\"StorageConnectionId\":null,\"StorageExecutionLocation\":1}";

        var configuration = JsonSerializer.Deserialize<BackupConfiguration>(oldJson, (JsonSerializerOptions?)null)!;

        Assert.Empty(configuration.Destinations);
        Assert.Throws<ArgumentException>(() => configuration.Validate());
    }
}
