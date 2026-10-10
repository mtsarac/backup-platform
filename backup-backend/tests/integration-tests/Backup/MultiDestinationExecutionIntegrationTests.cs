using Microsoft.EntityFrameworkCore;
using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Storage;
using BackupPlatform.IntegrationTests.Quota;
using BackupPlatform.IntegrationTests.Scheduling;
using BackupPlatform.IntegrationTests.Storage.Connections;
using BackupPlatform.ManagedService.Quota;
using Npgsql;
using Xunit;
using Completion = BackupPlatform.Application.Backups.BackupCompletion;
using static BackupPlatform.IntegrationTests.Quota.AdmissionTestKit;
using static BackupPlatform.IntegrationTests.Quota.UsageTestKit;

namespace BackupPlatform.IntegrationTests.Backup;

/// <summary>
/// One artifact, N replicas, against a real PostgreSQL: admission and claim fencing, per-destination persistence, the replication projection,
/// quota accounting under partial success, and the invariants the database itself enforces.
/// </summary>
public sealed class MultiDestinationExecutionIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTimeOffset T0 = BackupStoreHarness.T0;
    private readonly ConnectionWorld _connections = new(fixture);
    private readonly ScheduleKit _kit = new(fixture);
    private QuotaWorld World => _connections.Quota;

    private sealed record Multi(StoreAgent Agent, BackupJob Job, StorageConnection Central, StorageConnection Sftp, StorageConnection Drive)
    {
        public IReadOnlyList<(string Provider, StorageConnection Connection)> All => [("central", Central), ("sftp", Sftp), ("google-drive", Drive)];
    }

    private async Task<Multi> SetupAsync(bool capable = true, int required = 2, bool scheduled = false, QuotaAccountingMode? mode = null)
    {
        StoreAgent agent;
        if (mode is { } accounting)
        {
            (agent, _) = await World.TenantOnPolicyAsync(accounting, QuotaScopeRule.All);
        }
        else
        {
            agent = await World.AgentAsync(100 * QuotaWorld.Gb);
        }
        var (_, central) = await _connections.DefaultsAsync(agent.TenantId);
        var sftp = await _connections.AddAsync(agent.TenantId, "sftp-1", "sftp");
        var drive = await _connections.AddAsync(agent.TenantId, "drive-1", "google-drive");
        var configuration = BackupStoreHarness.Configuration() with
        {
            StorageProvider = "central", StorageConnectionId = central.Id, StorageExecutionLocation = BackupStorageExecutionLocation.Backend,
            Destinations = [new BackupDestination("central", central.Id, true), new BackupDestination("sftp", sftp.Id), new BackupDestination("google-drive", drive.Id)],
            RequiredVerifiedReplicaCount = required
        };
        var schedule = scheduled ? BackupSchedule.Create(true, "0 * * * *", "UTC", TimeSpan.FromMinutes(30), T0, false) : null;
        var job = new BackupJob(Guid.NewGuid(), agent.TenantId, "Replicated", agent.AgentId, configuration, T0, null, BackupOverlapPolicy.Queue, schedule);
        job = (await World.RunStore().CreateJobAsync(job, default))!;
        if (capable) await SetCapabilityAsync(agent, CapabilitySupport.Supported);
        return new Multi(agent, job, central, sftp, drive);
    }

    private async Task SetCapabilityAsync(StoreAgent agent, CapabilitySupport support)
    {
        await using var context = fixture.CreatePlatformContext();
        await context.AgentCapabilities.Where(row => row.AgentId == agent.AgentId).ExecuteDeleteAsync();
        context.AgentCapabilities.Add(new AgentCapabilityRecord(agent.AgentId, agent.TenantId, BackupTaskProtocol.MultiDestinationCapability, support, null, "revision-1", T0));
        await context.SaveChangesAsync();
    }

    private static string Key(string provider) => $"{provider}/tenant/job/run/attempt/db_20261005T000000Z_aaaaaaaa_FULL.bak";

    /// <summary>The completion an agent reports: a verified copy on each destination in <paramref name="verified"/>, a recorded failure on the rest.</summary>
    private static Completion Report(Multi multi, DateTimeOffset at, long size, ISet<string> verified, ISet<string>? mayRemain = null) => new("Succeeded", null,
        new BackupArtifactResult(size, BackupStoreHarness.SourceHash, BackupStoreHarness.ArtifactHash, "sha256", at, "{\"DatabaseName\":\"Database\"}", "{\"CopyOnly\":true}",
            [.. multi.All.Where(d => verified.Contains(d.Provider)).Select(d => new BackupReplicaResult(d.Provider, d.Provider == "central" ? "central" : d.Connection.Id.ToString("D"), Key(d.Provider),
                size, BackupStoreHarness.ArtifactHash, "sha256", "Verified", "ReadBack", at, at, d.Connection.Id))]),
        [.. multi.All.Select(d => verified.Contains(d.Provider)
            ? new BackupDestinationReport(d.Provider, d.Connection.Id, "Verified", "Succeeded", "Verified", null, Key(d.Provider), false, at, at)
            : mayRemain?.Contains(d.Provider) == true
                ? new BackupDestinationReport(d.Provider, d.Connection.Id, "Failed", "Succeeded", "Failed", "ReplicaVerificationFailed", Key(d.Provider), true, at, at)
                : new BackupDestinationReport(d.Provider, d.Connection.Id, "Failed", "Failed", "NotRun", "StorageUnavailable", null, false, at, at))]);

    private static Completion AllFailed(Multi multi, DateTimeOffset at) => new("Failed", "StorageWriteFailed", null,
        [.. multi.All.Select(d => new BackupDestinationReport(d.Provider, d.Connection.Id, "Failed", "Failed", "NotRun", "StorageUnavailable", null, false, at, at))]);

    private async Task<(BackupRun Run, BackupClaim Claim)> StartAndClaimAsync(Multi multi, DateTimeOffset at)
    {
        var store = World.RunStore();
        var started = await store.StartRunAsync(multi.Agent.TenantId, multi.Job.Id, $"key-{Guid.NewGuid():N}", at, default);
        Assert.Equal(BackupStartOutcome.Started, started.Outcome);
        var claim = await BackupStoreHarness.ClaimAsync(store, multi.Agent, at);
        Assert.NotNull(claim);
        await BackupStoreHarness.StartAttemptAsync(store, claim!, at);
        return (started.Run!, claim!);
    }

    private async Task<BackupRun> ReloadAsync(Multi multi, Guid runId) => (await World.RunStore().GetRunAsync(multi.Agent.TenantId, runId, default))!;

    // ---- admission and claim: manual, scheduled and retry are fenced alike ----

    [Fact]
    public async Task Manual_and_scheduled_runs_pass_the_same_gate_and_snapshot_the_same_resolved_destinations()
    {
        var multi = await SetupAsync(scheduled: true);
        var manual = await World.RunStore().StartRunAsync(multi.Agent.TenantId, multi.Job.Id, "manual-1", T0, default);
        await _kit.SeenAsync(multi.Agent, T0.AddHours(1).AddMinutes(19));
        var scheduled = await World.ScheduleStore().EvaluateAsync(multi.Job.TenantId, multi.Job.Id, T0.AddHours(1).AddMinutes(20), default);

        Assert.Equal(BackupStartOutcome.Started, manual.Outcome);
        Assert.NotNull(scheduled.CreatedRunId);
        var scheduledRun = await ReloadAsync(multi, scheduled.CreatedRunId!.Value);
        Assert.Equal(BackupTriggerTypes.Scheduled, scheduledRun.TriggerType);
        var expected = multi.All.Select(d => (d.Provider, (Guid?)d.Connection.Id)).ToArray();
        Assert.Equal(expected, manual.Run!.Configuration.Destinations.Select(d => (d.StorageProvider, d.StorageConnectionId)));
        Assert.Equal(expected, scheduledRun.Configuration.Destinations.Select(d => (d.StorageProvider, d.StorageConnectionId)));
        Assert.Equal(2, manual.Run.Configuration.RequiredVerifiedReplicaCount);
        Assert.Equal(2, scheduledRun.Configuration.RequiredVerifiedReplicaCount);
    }

    [Fact]
    public async Task Both_entry_points_refuse_when_the_agent_does_not_declare_multi_destination_execution()
    {
        var multi = await SetupAsync(capable: false, scheduled: true);

        var manual = await World.RunStore().StartRunAsync(multi.Agent.TenantId, multi.Job.Id, "manual-1", T0, default);
        await _kit.SeenAsync(multi.Agent, T0.AddHours(1).AddMinutes(19));
        var scheduled = await World.ScheduleStore().EvaluateAsync(multi.Job.TenantId, multi.Job.Id, T0.AddHours(1).AddMinutes(20), default);

        Assert.Equal(BackupStartOutcome.AdmissionDenied, manual.Outcome);
        Assert.Equal(BackupAdmissionDenialCodes.AgentMultiDestinationUnsupported, manual.DenialCode);
        Assert.Null(scheduled.CreatedRunId);
        var occurrence = Assert.Single(await _kit.OccurrencesAsync(multi.Job));
        Assert.Equal(BackupOccurrenceOutcome.AdmissionDenied, occurrence.Outcome);
        Assert.Equal(BackupAdmissionDenialCodes.AgentMultiDestinationUnsupported, occurrence.Reason);
        Assert.Equal(0, await World.Harness.CountRunsAsync(multi.Job.Id));
    }

    [Theory]
    [InlineData(CapabilitySupport.Unsupported)]
    [InlineData(CapabilitySupport.Unknown)]
    public async Task A_capability_that_is_not_Supported_is_not_a_capability(CapabilitySupport support)
    {
        var multi = await SetupAsync(capable: false);
        await SetCapabilityAsync(multi.Agent, support);

        var result = await World.RunStore().StartRunAsync(multi.Agent.TenantId, multi.Job.Id, "manual-1", T0, default);

        Assert.Equal(BackupAdmissionDenialCodes.AgentMultiDestinationUnsupported, result.DenialCode);
    }

    [Fact]
    public async Task A_denied_occurrence_is_never_re_run_when_the_capability_appears_later()
    {
        var multi = await SetupAsync(capable: false, scheduled: true);
        await _kit.SeenAsync(multi.Agent, T0.AddHours(1).AddMinutes(19));
        await World.ScheduleStore().EvaluateAsync(multi.Job.TenantId, multi.Job.Id, T0.AddHours(1).AddMinutes(20), default);
        await SetCapabilityAsync(multi.Agent, CapabilitySupport.Supported);

        var again = await World.ScheduleStore().EvaluateAsync(multi.Job.TenantId, multi.Job.Id, T0.AddHours(1).AddMinutes(25), default);

        Assert.Null(again.CreatedRunId);
        var occurrence = Assert.Single(await _kit.OccurrencesAsync(multi.Job));
        Assert.Equal(BackupOccurrenceOutcome.AdmissionDenied, occurrence.Outcome);
        Assert.Equal(0, await World.Harness.CountRunsAsync(multi.Job.Id));
    }

    [Fact]
    public async Task A_queued_multi_destination_run_waits_for_an_agent_that_declares_the_capability()
    {
        var multi = await SetupAsync();
        var store = World.RunStore();
        await store.StartRunAsync(multi.Agent.TenantId, multi.Job.Id, "manual-1", T0, default);
        await SetCapabilityAsync(multi.Agent, CapabilitySupport.Unsupported);

        Assert.Null(await BackupStoreHarness.ClaimAsync(store, multi.Agent, T0));
        await SetCapabilityAsync(multi.Agent, CapabilitySupport.Supported);
        Assert.NotNull(await BackupStoreHarness.ClaimAsync(store, multi.Agent, T0));
    }

    // ---- completion: independent per-destination results ----

    [Fact]
    public async Task A_partial_success_publishes_the_verified_replicas_records_every_destination_and_projects_Degraded()
    {
        var multi = await SetupAsync(required: 2);
        var (started, claim) = await StartAndClaimAsync(multi, T0);

        await BackupStoreHarness.CompleteAsync(World.RunStore(), claim, Report(multi, T0, 100, new HashSet<string> { "central", "sftp" }), T0);

        var run = await ReloadAsync(multi, started.Id);
        Assert.Equal(BackupStatus.Succeeded, run.Status);
        var attempt = run.CurrentAttempt;
        var artifact = Assert.Single(attempt.Artifacts);
        Assert.Equal(["central", "sftp"], artifact.Replicas.Select(r => r.StorageProvider).Order());
        Assert.All(artifact.Replicas, replica => Assert.Equal(BackupReplicaStatus.Available, replica.Status));
        Assert.Equal(3, attempt.DestinationOutcomes.Count);
        var failed = Assert.Single(attempt.DestinationOutcomes, o => !o.IsVerified);
        Assert.Equal(("google-drive", "Failed", "StorageUnavailable", false), (failed.StorageProvider, failed.State, failed.FailureCode, failed.ObjectMayRemain));
        Assert.All(attempt.DestinationOutcomes.Where(o => o.IsVerified), o => Assert.Contains(artifact.Replicas, r => r.Id == o.ReplicaId));

        var view = BackupOrchestrator.ToView(run);
        Assert.Equal("Succeeded", view.Status);
        var replication = view.Replication!;
        Assert.Equal(("Degraded", true, 2, 2, 3), (replication.Status, replication.PolicySatisfied, replication.VerifiedReplicas, replication.RequiredVerifiedReplicas, replication.DestinationCount));
        Assert.Equal(3, replication.Destinations.Count);
        Assert.Equal("Failed", replication.Destinations.Single(d => d.StorageProvider == "google-drive").State);
        Assert.Equal("Degraded", Assert.Single(view.Attempts).Artifacts.Single().Replication!.Status);
        // The one artifact is counted once logically and once per physical copy, whichever the policy; the reservation did not leak.
        await World.AssertOracleAsync(multi.Agent.TenantId, 100, 200);
        await World.AssertUsedAsync(multi.Agent.TenantId, 100);
        await World.AssertNoDriftAsync(multi.Agent.TenantId);
        Assert.Empty(await UnreleasedAsync(fixture, multi.Agent.TenantId));
    }

    [Fact]
    public async Task Physical_accounting_counts_exactly_the_copies_that_exist()
    {
        var multi = await SetupAsync(required: 2, mode: QuotaAccountingMode.PhysicalReplicaBytes);
        var (_, claim) = await StartAndClaimAsync(multi, T0);

        await BackupStoreHarness.CompleteAsync(World.RunStore(), claim, Report(multi, T0, 100, new HashSet<string> { "central", "sftp" }), T0);

        await World.AssertUsedAsync(multi.Agent.TenantId, 200);
        await World.AssertOracleAsync(multi.Agent.TenantId, 100, 200);
    }

    [Fact]
    public async Task An_object_that_may_remain_stays_accounted_as_an_unverified_unusable_replica()
    {
        var multi = await SetupAsync(required: 2, mode: QuotaAccountingMode.PhysicalReplicaBytes);
        var (started, claim) = await StartAndClaimAsync(multi, T0);

        await BackupStoreHarness.CompleteAsync(World.RunStore(), claim, Report(multi, T0, 100, new HashSet<string> { "central", "sftp" }, new HashSet<string> { "google-drive" }), T0);

        var run = await ReloadAsync(multi, started.Id);
        var artifact = run.CurrentAttempt.Artifacts.Single();
        var orphan = Assert.Single(artifact.Replicas, r => r.VerificationState != "Verified");
        Assert.Equal(("google-drive", BackupReplicaStatus.Available, false), (orphan.StorageProvider, orphan.Status, orphan.IsUsable));
        Assert.Equal(BackupArtifactState.Available, artifact.State);
        // The orphan's bytes are counted (it IS on the disk), but it is not a verified copy: the projection still says two.
        await World.AssertUsedAsync(multi.Agent.TenantId, 300);
        Assert.Equal(2, BackupOrchestrator.ToView(run).Replication!.VerifiedReplicas);
        Assert.True(BackupOrchestrator.ToView(run).Replication!.Destinations.Single(d => d.StorageProvider == "google-drive").ObjectMayRemain);
    }

    [Fact]
    public async Task Fewer_verified_copies_than_required_is_still_a_usable_run_and_is_reported_as_InsufficientReplicas()
    {
        var multi = await SetupAsync(required: 2);
        var (started, claim) = await StartAndClaimAsync(multi, T0);

        await BackupStoreHarness.CompleteAsync(World.RunStore(), claim, Report(multi, T0, 100, new HashSet<string> { "sftp" }), T0);

        var run = await ReloadAsync(multi, started.Id);
        var view = BackupOrchestrator.ToView(run);
        Assert.Equal(BackupStatus.Succeeded, run.Status);
        Assert.Equal(BackupArtifactState.Available, run.CurrentAttempt.Artifacts.Single().State);
        Assert.Equal(("InsufficientReplicas", false, 1), (view.Replication!.Status, view.Replication.PolicySatisfied, view.Replication.VerifiedReplicas));
    }

    [Fact]
    public async Task Every_destination_failing_fails_the_run_without_an_artifact_keeps_the_outcomes_and_leaks_no_reservation()
    {
        var multi = await SetupAsync(required: 2);
        var (started, claim) = await StartAndClaimAsync(multi, T0);

        await World.RunStore().CompleteAttemptAsync(BackupStoreHarness.Command(claim, T0), AllFailed(multi, T0), default);

        var run = await ReloadAsync(multi, started.Id);
        var attempt = run.Attempts.Single();
        Assert.Empty(attempt.Artifacts);
        Assert.Equal(3, attempt.DestinationOutcomes.Count);
        Assert.All(attempt.DestinationOutcomes, outcome => Assert.Equal("Failed", outcome.State));
        var replication = BackupOrchestrator.ToView(run).Replication!;
        Assert.Equal(("Unavailable", false, 0), (replication.Status, replication.PolicySatisfied, replication.VerifiedReplicas));
        await World.AssertOracleAsync(multi.Agent.TenantId, 0, 0);
        Assert.Empty(await UnreleasedAsync(fixture, multi.Agent.TenantId));
    }

    [Fact]
    public async Task A_repeated_completion_is_idempotent_and_never_doubles_a_destination()
    {
        var multi = await SetupAsync();
        var (started, claim) = await StartAndClaimAsync(multi, T0);
        var completion = Report(multi, T0, 100, new HashSet<string> { "central", "sftp" });
        var store = World.RunStore();

        await BackupStoreHarness.CompleteAsync(store, claim, completion, T0);
        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteAttemptAsync(BackupStoreHarness.Command(claim, T0), completion, default));

        var run = await ReloadAsync(multi, started.Id);
        Assert.Equal(2, run.CurrentAttempt.Artifacts.Single().Replicas.Count);
        Assert.Equal(3, run.CurrentAttempt.DestinationOutcomes.Count);
        await World.AssertOracleAsync(multi.Agent.TenantId, 100, 200);
    }

    [Fact]
    public async Task The_store_refuses_a_destination_that_is_not_in_the_run_snapshot()
    {
        var multi = await SetupAsync();
        var (started, claim) = await StartAndClaimAsync(multi, T0);
        var stranger = await _connections.AddAsync(multi.Agent.TenantId, "other-sftp", "sftp");
        var forged = Report(multi, T0, 100, new HashSet<string> { "central" });
        forged = forged with { Artifact = forged.Artifact! with { Replicas = [forged.Artifact.Replicas[0], forged.Artifact.Replicas[0] with { StorageProvider = "sftp", StorageTarget = stranger.Id.ToString("D"), StorageConnectionId = stranger.Id, Key = Key("other") }] } };

        var result = await World.RunStore().CompleteAttemptAsync(BackupStoreHarness.Command(claim, T0), forged, default);

        Assert.Equal(BackupMutationResult.Conflict, result);
        var run = await ReloadAsync(multi, started.Id);
        Assert.Equal(BackupStatus.Running, run.Status);
        Assert.Empty(run.CurrentAttempt.Artifacts);
        await World.AssertOracleAsync(multi.Agent.TenantId, 0, 0);
    }

    [Fact]
    public async Task The_database_itself_refuses_a_second_live_verified_replica_on_one_destination()
    {
        var multi = await SetupAsync();
        var (started, claim) = await StartAndClaimAsync(multi, T0);
        await BackupStoreHarness.CompleteAsync(World.RunStore(), claim, Report(multi, T0, 100, new HashSet<string> { "central", "sftp" }), T0);
        var artifactId = (await ReloadAsync(multi, started.Id)).CurrentAttempt.Artifacts.Single().Id;

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var duplicate = new NpgsqlCommand("""
            INSERT INTO backup_replicas ("Id","TenantId","ArtifactId","StorageProvider","StorageTarget","StorageConnectionId","ExecutionLocation","Key","SizeBytes","Checksum","ChecksumAlgorithm",
                "VerificationState","VerificationMethod","CreatedAt","VerifiedAt","Status","DeleteAttempts")
            SELECT gen_random_uuid(), "TenantId", "ArtifactId", "StorageProvider", "StorageTarget", "StorageConnectionId", "ExecutionLocation", 'another/key.bak', "SizeBytes", "Checksum", "ChecksumAlgorithm",
                "VerificationState","VerificationMethod","CreatedAt","VerifiedAt",'Available',0
            FROM backup_replicas WHERE "ArtifactId" = @artifact AND "StorageProvider" = 'sftp'
            """, connection);
        duplicate.Parameters.AddWithValue("artifact", artifactId);

        var failure = await Assert.ThrowsAsync<PostgresException>(() => duplicate.ExecuteNonQueryAsync());

        Assert.Equal(PostgresErrorCodes.UniqueViolation, failure.SqlState);
        Assert.Equal("ux_backup_replicas_live_verified_destination", failure.ConstraintName);
    }

    // ---- retention ----

    [Fact]
    public async Task Retention_touches_nothing_before_expiry_then_moves_every_replica_including_an_orphan()
    {
        var multi = await SetupAsync(required: 2);
        var (started, claim) = await StartAndClaimAsync(multi, T0);
        await BackupStoreHarness.CompleteAsync(World.RunStore(), claim, Report(multi, T0, 100, new HashSet<string> { "central", "sftp" }, new HashSet<string> { "google-drive" }), T0);
        var artifactId = (await ReloadAsync(multi, started.Id)).CurrentAttempt.Artifacts.Single().Id;
        var expiry = (await ReloadAsync(multi, started.Id)).CurrentAttempt.Artifacts.Single().RetentionExpiresAt;

        int before = 0, after = 0;
        await World.ChangeArtifactAsync(artifactId, artifact => before = artifact.ApplyRetention(T0.AddMinutes(1)));
        await World.ChangeArtifactAsync(artifactId, artifact => after = artifact.ApplyRetention(expiry!.Value.AddMinutes(1)));

        Assert.Equal(0, before);
        Assert.Equal(3, after);
        await using var context = fixture.CreatePlatformContext();
        var replicas = await context.BackupReplicas.AsNoTracking().Where(r => r.ArtifactId == artifactId).ToListAsync();
        Assert.All(replicas, replica => Assert.Equal(BackupReplicaStatus.DeletePending, replica.Status));
    }

    [Fact]
    public async Task A_legacy_single_destination_job_still_completes_without_destination_reports_and_projects_Healthy()
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
