using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Completion = BackupPlatform.Application.Backups.BackupCompletion;

namespace BackupPlatform.IntegrationTests;

public sealed class BackupOrchestrationPersistenceTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly string SourceHash = new('a', 64);
    private static readonly string ArtifactHash = new('b', 64);
    private PostgreSqlBackupRunStore Store(PostgreSqlFixture? database = null) => new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql((database ?? fixture).ConnectionString).Options, AdmitAllBackupAdmission.Instance);

    [Fact]
    public async Task ConcurrentManualRequestsAndPollsCreateOneRunAndOneExclusiveClaim()
    {
        var seed = await SeedAsync();
        var requests = Enumerable.Range(0, 12).Select(_ => Store().StartRunAsync(seed.TenantId, seed.JobId, "same-request", Now, default));
        var runs = await Task.WhenAll(requests);
        Assert.Single(runs.Select(run => run.Run!.Id).Distinct());
        var polls = Enumerable.Range(0, 12).Select(_ => Store().ClaimNextAsync(seed.TenantId, seed.AgentId, Now, Lease, default));
        var claims = await Task.WhenAll(polls);
        var claim = Assert.Single(claims.OfType<BackupClaim>());
        Assert.Equal(runs[0].Run!.Id, claim.Run.Id);
        Assert.Null(await Store().ClaimNextAsync(seed.TenantId, seed.AgentId, Now.AddSeconds(1), Lease, default));
        Assert.Single((await Store().GetRunAsync(seed.TenantId, claim.Run.Id, default))!.Attempts);
    }
    [Fact]
    public async Task QueueWaitsForTerminalAttemptAndPendingCancellationDoesNotExecute()
    {
        var seed = await SeedAsync();
        var store = Store();
        var first = (await store.StartRunAsync(seed.TenantId, seed.JobId, "first", Now, default)).Run;
        var cancelled = (await store.StartRunAsync(seed.TenantId, seed.JobId, "cancelled", Now.AddSeconds(1), default)).Run;
        var next = (await store.StartRunAsync(seed.TenantId, seed.JobId, "next", Now.AddSeconds(2), default)).Run;
        var claim = (await store.ClaimNextAsync(seed.TenantId, seed.AgentId, Now, Lease, default))!;
        Assert.Equal(first!.Id, claim.Run.Id);
        Assert.Equal(BackupStatus.Cancelled, (await store.RequestCancellationAsync(seed.TenantId, cancelled!.Id, Now, default))!.Status);
        Assert.Null(await store.ClaimNextAsync(seed.TenantId, seed.AgentId, Now.AddSeconds(2), Lease, default));
        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteAttemptAsync(Command(claim, Now), new Completion("Failed", "PreCheckFailed", null), default));
        Assert.Equal(next!.Id, (await store.ClaimNextAsync(seed.TenantId, seed.AgentId, Now.AddSeconds(3), Lease, default))!.Run.Id);
    }
    [Fact]
    public async Task OwnershipAndLeaseTokenFenceCallbacksAndTenantRows()
    {
        var seed = await SeedAsync();
        var other = await SeedAsync();
        var store = Store();
        var run = (await store.StartRunAsync(seed.TenantId, seed.JobId, "isolation", Now, default)).Run;
        var claim = (await store.ClaimNextAsync(seed.TenantId, seed.AgentId, Now, Lease, default))!;
        var command = Command(claim, Now);
        Assert.Equal(BackupMutationResult.NotFound, await store.StartAttemptAsync(command with { TenantId = other.TenantId }, default));
        Assert.Equal(BackupMutationResult.NotFound, await store.StartAttemptAsync(command with { AgentId = other.AgentId }, default));
        Assert.Equal(BackupMutationResult.Conflict, await store.StartAttemptAsync(command with { LeaseToken = Guid.NewGuid() }, default));
        Assert.Null(await store.GetRunAsync(other.TenantId, run!.Id, default));
        Assert.Null(await store.RequestCancellationAsync(other.TenantId, run.Id, Now, default));
        Assert.Null(await store.GetAttemptAsync(other.TenantId, seed.AgentId, command.AttemptId, default));
        Assert.Empty(await store.ListRunsAsync(other.TenantId, seed.JobId, 20, default));
        await using var scoped = fixture.CreateContext(other.TenantId);
        Assert.False(await scoped.BackupJobs.AnyAsync(job => job.Id == seed.JobId));
        Assert.False(await scoped.BackupRuns.AnyAsync(row => row.Id == run.Id));
        Assert.False(await scoped.BackupRunAttempts.AnyAsync(row => row.Id == command.AttemptId));
        Assert.False(await scoped.BackupLifecycleFacts.AnyAsync(row => row.RunId == run.Id));
        scoped.BackupJobs.Add(new BackupJob(Guid.NewGuid(), seed.TenantId, "Forbidden", seed.AgentId, Configuration(), Now));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.SaveChangesAsync());
    }
    [Fact]
    public async Task CancellationRequestedWhileRunningOutranksAgentLostExpiryAndNeverRetries()
    {
        var isolated = new PostgreSqlFixture();
        await isolated.InitializeAsync();
        try
        {
            var seed = await SeedAsync(isolated);
            var store = Store(isolated);
            var run = (await store.StartRunAsync(seed.TenantId, seed.JobId, "expiry", Now, default)).Run!;
            var claim = (await store.ClaimNextAsync(seed.TenantId, seed.AgentId, Now, Lease, default))!;
            var command = Command(claim, Now);
            Assert.Equal(BackupMutationResult.Accepted, await store.StartAttemptAsync(command, default));
            Assert.Equal(BackupMutationResult.Accepted, await store.ReportProgressAsync(command with { Now = Now.AddSeconds(10) }, new BackupProgress("Uploading", 42.5), default));
            var cancelling = await store.RequestCancellationAsync(seed.TenantId, run.Id, Now.AddSeconds(15), default);
            Assert.Equal(BackupStatus.Running, cancelling!.Status);
            Assert.Equal(Now.AddSeconds(15), cancelling.CancellationRequestedAt);
            var renewed = await store.RenewLeaseAsync(command with { Now = Now.AddSeconds(20) }, Lease, default);
            Assert.True(renewed!.CancellationRequested);
            Assert.Equal(Now.AddSeconds(140), renewed.LeaseExpiresAt);
            Assert.Equal(0, await store.ExpireStaleAsync(Now.AddSeconds(120), default));
            Assert.Equal(BackupStatus.Running, (await store.GetRunAsync(seed.TenantId, run.Id, default))!.Status);
            Assert.Equal(1, await store.ExpireStaleAsync(Now.AddSeconds(140), default));
            Assert.Null(await store.RenewLeaseAsync(command with { Now = Now.AddSeconds(140) }, Lease, default));
            Assert.Equal(BackupMutationResult.Conflict, await store.CompleteAttemptAsync(command with { Now = Now.AddSeconds(140) }, new Completion("Failed", "InternalError", null), default));
            var persisted = (await store.GetRunAsync(seed.TenantId, run.Id, default))!;
            // The attempt keeps the truth (the agent vanished); the run keeps the operator's decision.
            Assert.Equal(BackupStatus.Cancelled, persisted.Status);
            Assert.Equal(BackupFailureCodes.Cancelled, persisted.TerminalReason);
            Assert.Equal(BackupRetryState.None, persisted.RetryState);
            Assert.Null(persisted.NextAttemptAt);
            Assert.Equal(Now.AddSeconds(140), persisted.CompletedAt);
            var attempt = Assert.Single(persisted.Attempts);
            Assert.Equal(BackupStatus.AgentLost, attempt.Status);
            Assert.Equal(BackupFailureCodes.AgentLost, attempt.TerminalReason);
            Assert.Equal(42.5, attempt.ProgressPercent);
            Assert.Contains(persisted.Facts, fact => fact.Kind == "BackupCancellationRequested");
            Assert.Single(persisted.Facts, fact => fact.Kind == "BackupRunCancelled");
            Assert.DoesNotContain(persisted.Facts, fact => fact.Kind is "BackupRetryScheduled" or "BackupRetryStarted" or "BackupRunFailed");
            // Nothing is scheduled, claimable or manually retryable afterwards, however much time passes.
            Assert.Empty(await store.StartDueRetriesAsync(Now.AddDays(1), 100, default));
            Assert.Equal(BackupRetryOutcome.Cancelled, (await store.RetryRunAsync(seed.TenantId, run.Id, Now.AddDays(1), default)).Outcome);
            Assert.Null(await store.ClaimNextAsync(seed.TenantId, seed.AgentId, Now.AddSeconds(141), Lease, default));
            Assert.Single((await store.GetRunAsync(seed.TenantId, run.Id, default))!.Attempts);
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }
    [Fact]
    public async Task CompletionIsAtomicImmutableAndSemanticallyIdempotentAcrossFreshContexts()
    {
        var seed = await SeedAsync();
        var other = await SeedAsync();
        var store = Store();
        var run = (await store.StartRunAsync(seed.TenantId, seed.JobId, "complete", Now, default)).Run!;
        var claim = (await store.ClaimNextAsync(seed.TenantId, seed.AgentId, Now, Lease, default))!;
        var command = Command(claim, Now);
        Assert.Equal(BackupMutationResult.Accepted, await store.StartAttemptAsync(command, default));
        var completion = Success(Now.AddTicks(7));
        var invalid = completion with { Artifact = completion.Artifact! with { Replicas = [completion.Artifact!.Replicas[0] with { Checksum = SourceHash }] } };
        Assert.Equal(BackupMutationResult.Conflict, await store.CompleteAttemptAsync(command, invalid, default));
        Assert.Equal(BackupStatus.Running, (await store.GetRunAsync(seed.TenantId, run.Id, default))!.Status);
        var completions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Store().CompleteAttemptAsync(command, completion, default)));
        Assert.All(completions, result => Assert.Equal(BackupMutationResult.Accepted, result));
        var equivalent = completion with { Artifact = completion.Artifact! with { SourceMetadataJson = "{ \"Nested\": {\"b\": 2, \"a\": 1}, \"DatabaseName\": \"Database\" }" } };
        Assert.Equal(BackupMutationResult.Accepted, await Store().CompleteAttemptAsync(command with { Now = Now.AddHours(1) }, equivalent, default));
        Assert.Equal(BackupMutationResult.Conflict, await Store().CompleteAttemptAsync(command, new Completion("Failed", "InternalError", null), default));
        var persisted = (await store.GetRunAsync(seed.TenantId, run.Id, default))!;
        Assert.Equal(BackupStatus.Succeeded, persisted.Status);
        Assert.Equal("Completed", persisted.CurrentAttempt.ProgressStage);
        Assert.Equal(100, persisted.CurrentAttempt.ProgressPercent);
        var artifact = Assert.Single(persisted.CurrentAttempt.Artifacts);
        Assert.Equal(SourceHash, artifact.SourceChecksum);
        Assert.Equal(ArtifactHash, artifact.ArtifactChecksum);
        var replica = Assert.Single(artifact.Replicas);
        Assert.Equal(Now, replica.CreatedAt);
        Assert.Throws<InvalidOperationException>(() => artifact.AddReplica(new BackupReplica(Guid.NewGuid(), seed.TenantId, artifact.Id,
            "local", "default", "another", 100, ArtifactHash, "sha256", "Verified", "ReadBack", Now, Now,
            executionLocation: BackupStorageExecutionLocation.Backend)));
        Assert.Single(persisted.Facts, fact => fact.Kind == "BackupRunSucceeded");
        await using var hidden = fixture.CreateContext(other.TenantId);
        Assert.False(await hidden.BackupArtifacts.AnyAsync(row => row.Id == artifact.Id));
        Assert.False(await hidden.BackupReplicas.AnyAsync(row => row.Id == replica.Id));
    }
    [Fact]
    public async Task OneLogicalArtifactCanPersistMultipleVerifiedPhysicalReplicas()
    {
        var seed = await SeedAsync();
        var store = Store();
        var run = (await store.StartRunAsync(seed.TenantId, seed.JobId, "multiple-replicas", Now, default)).Run!;
        var claim = (await store.ClaimNextAsync(seed.TenantId, seed.AgentId, Now, Lease, default))!;
        var command = Command(claim, Now);
        Assert.Equal(BackupMutationResult.Accepted, await store.StartAttemptAsync(command, default));
        var completion = Success(Now);
        var first = completion.Artifact!.Replicas[0];
        // One live verified copy per destination: the second physical copy is on another storage.
        var second = first with { StorageProvider = "central", StorageTarget = "secondary", Key = "tenant/job/run/attempt/second.bak" };
        completion = completion with { Artifact = completion.Artifact with { Replicas = [first, second] } };
        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteAttemptAsync(command, completion, default));
        var persisted = (await store.GetRunAsync(seed.TenantId, run.Id, default))!;
        var artifact = Assert.Single(persisted.CurrentAttempt.Artifacts);
        Assert.Equal(new[] { "default", "secondary" }, artifact.Replicas.Select(replica => replica.StorageTarget).Order(StringComparer.Ordinal).ToArray());
        Assert.All(artifact.Replicas, replica =>
        {
            Assert.Equal(artifact.Id, replica.ArtifactId);
            Assert.Equal(artifact.ArtifactChecksum, replica.Checksum);
            Assert.Equal(artifact.SizeBytes, replica.SizeBytes);
            Assert.Equal("Verified", replica.VerificationState);
        });
        Assert.Equal(BackupMutationResult.Accepted, await Store().CompleteAttemptAsync(command, completion, default));
    }
    [Fact]
    public async Task MultiDestinationJobPersistsConfigurationAndRunSnapshotIsImmuneToJobReconfiguration()
    {
        var seed = await SeedAsync();
        var store = Store();

        var multiConfig = Configuration() with
        {
            Destinations =
            [
                new BackupDestination("local", null, IsPrimary: true),
                new BackupDestination("central", null, IsPrimary: false)
            ],
            RequiredVerifiedReplicaCount = 2
        };

        var updateCommand = new BackupJobUpdate("MultiJob", seed.AgentId, multiConfig, BackupRetryPolicy.Default, BackupOverlapPolicy.Queue);
        var updatedJob = (await store.UpdateJobAsync(seed.TenantId, seed.JobId, updateCommand, default))!;

        Assert.Equal(2, updatedJob.Configuration.Destinations.Count);
        Assert.Equal(2, updatedJob.Configuration.RequiredVerifiedReplicaCount);

        // Start a run under single destination configuration
        var singleConfig = Configuration() with
        {
            Destinations =
            [
                new BackupDestination("local", null, IsPrimary: true)
            ],
            RequiredVerifiedReplicaCount = 1
        };
        await store.UpdateJobAsync(seed.TenantId, seed.JobId, new BackupJobUpdate("SingleJob", seed.AgentId, singleConfig, BackupRetryPolicy.Default, BackupOverlapPolicy.Queue), default);

        var startResult = await store.StartRunAsync(seed.TenantId, seed.JobId, "single-dest-run", Now, default);
        var run = startResult.Run!;

        Assert.Single(run.Configuration.Destinations);
        Assert.Equal(1, run.Configuration.RequiredVerifiedReplicaCount);

        // Now modify job configuration to multi destination
        await store.UpdateJobAsync(seed.TenantId, seed.JobId, updateCommand, default);

        // Verify that the run's persisted snapshot is unchanged
        var persistedRun = (await store.GetRunAsync(seed.TenantId, run.Id, default))!;
        Assert.Single(persistedRun.Configuration.Destinations);
        Assert.Equal(1, persistedRun.Configuration.RequiredVerifiedReplicaCount);
    }
    [Fact]
    public async Task MultiDestinationJobIsAdmittedOnlyForAnAgentThatDeclaresMultiDestinationExecution()
    {
        var seed = await SeedAsync();
        var store = Store();
        var multiConfig = Configuration() with
        {
            Destinations =
            [
                new BackupDestination("local", null, IsPrimary: true),
                new BackupDestination("central", null, IsPrimary: false)
            ],
            RequiredVerifiedReplicaCount = 2
        };
        await store.UpdateJobAsync(seed.TenantId, seed.JobId, new BackupJobUpdate("MultiJob", seed.AgentId, multiConfig, BackupRetryPolicy.Default, BackupOverlapPolicy.Queue), default);

        var refused = await store.StartRunAsync(seed.TenantId, seed.JobId, "guard-run", Now, default);

        Assert.Equal(BackupStartOutcome.AdmissionDenied, refused.Outcome);
        Assert.Equal(BackupAdmissionDenialCodes.AgentMultiDestinationUnsupported, refused.DenialCode);
        Assert.Null(refused.Run);
        await using (var context = fixture.CreatePlatformContext())
        {
            context.AgentCapabilities.Add(new BackupPlatform.Agents.Capabilities.AgentCapabilityRecord(seed.AgentId, seed.TenantId,
                BackupPlatform.Contracts.Backups.BackupTaskProtocol.MultiDestinationCapability, BackupPlatform.Domain.Capabilities.CapabilitySupport.Supported, null, "revision-1", Now));
            await context.SaveChangesAsync();
        }

        var admitted = await store.StartRunAsync(seed.TenantId, seed.JobId, "guard-run-2", Now, default);

        Assert.Equal(BackupStartOutcome.Started, admitted.Outcome);
        Assert.Equal(["local", "central"], admitted.Run!.Configuration.Destinations.Select(d => d.StorageProvider));
        Assert.All(admitted.Run.Configuration.Destinations, d => Assert.NotNull(d.StorageConnectionId));
        Assert.Equal(2, admitted.Run.Configuration.RequiredVerifiedReplicaCount);
    }
    [Theory]
    [InlineData("job-agent")]
    [InlineData("run-job")]
    [InlineData("run-agent")]
    [InlineData("attempt-run")]
    [InlineData("attempt-agent")]
    [InlineData("artifact-attempt")]
    [InlineData("replica-artifact")]
    public async Task CompositeForeignKeysRejectCrossTenantParents(string relationship)
    {
        var first = await CompletedAsync();
        var second = await CompletedAsync();
        var (table, column, id, foreignId) = relationship switch
        {
            "job-agent" => ("backup_jobs", "AgentId", first.JobId, second.AgentId.Value),
            "run-job" => ("backup_runs", "JobId", first.Run.Id, second.JobId),
            "run-agent" => ("backup_runs", "AgentId", first.Run.Id, second.AgentId.Value),
            "attempt-run" => ("backup_run_attempts", "RunId", first.Run.CurrentAttempt.Id, second.Run.Id),
            "attempt-agent" => ("backup_run_attempts", "AgentId", first.Run.CurrentAttempt.Id, second.AgentId.Value),
            "artifact-attempt" => ("backup_artifacts", "AttemptId", first.Run.CurrentAttempt.Artifacts[0].Id, second.Run.CurrentAttempt.Id),
            "replica-artifact" => ("backup_replicas", "ArtifactId", first.Run.CurrentAttempt.Artifacts[0].Replicas[0].Id, second.Run.CurrentAttempt.Artifacts[0].Id),
            _ => throw new ArgumentOutOfRangeException(nameof(relationship))
        };
        await using var context = fixture.CreatePlatformContext();
        // Identifiers are selected exclusively from the fixed cases above; values remain parameters.
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"UPDATE {table} SET \"{column}\" = $1 WHERE \"Id\" = $2", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = foreignId });
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        var error = await Assert.ThrowsAsync<PostgresException>(async () => await command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
    }
    [Fact]
    public async Task DatabaseProtectsReplicaBytesChecksumsAndRestrictsDeletingHistory()
    {
        var completed = await CompletedAsync();
        var artifact = completed.Run.CurrentAttempt.Artifacts[0];
        var replica = artifact.Replicas[0];
        await using var context = fixture.CreatePlatformContext();
        var sizeError = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE backup_replicas SET \"SizeBytes\" = 101 WHERE \"Id\" = {replica.Id}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, sizeError.SqlState);
        var checksumError = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE backup_replicas SET \"Checksum\" = {SourceHash} WHERE \"Id\" = {replica.Id}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, checksumError.SqlState);
        var deleteError = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM backup_runs WHERE \"Id\" = {completed.Run.Id}"));
        Assert.Equal(PostgresErrorCodes.RestrictViolation, deleteError.SqlState);
    }
    [Fact]
    public async Task DatabaseEnforcesAttemptNumberLeaseTokenAndLifecycleInvariants()
    {
        var seed = await SeedAsync();
        var store = Store();
        var first = (await store.StartRunAsync(seed.TenantId, seed.JobId, "a", Now, default)).Run!;
        var second = (await store.StartRunAsync(seed.TenantId, seed.JobId, "b", Now.AddSeconds(1), default)).Run!;
        var claim = (await store.ClaimNextAsync(seed.TenantId, seed.AgentId, Now, Lease, default))!;
        Assert.Equal(first.Id, claim.Run.Id);
        var pending = second.CurrentAttempt.Id;
        await using var context = fixture.CreatePlatformContext();
        // Both attempts are number 1 of their own run; moving one under the other run would duplicate (Tenant, Run, Number).
        await AssertViolationAsync(context, PostgresErrorCodes.UniqueViolation, "IX_backup_run_attempts_TenantId_RunId_Number",
            $"UPDATE backup_run_attempts SET \"RunId\" = {first.Id} WHERE \"Id\" = {pending}");
        // A lease token identifies exactly one attempt, so a stolen or duplicated token cannot fence two attempts at once.
        await AssertViolationAsync(context, PostgresErrorCodes.UniqueViolation, "IX_backup_run_attempts_LeaseToken",
            $"UPDATE backup_run_attempts SET \"Status\" = 2, \"LeaseToken\" = {claim.Attempt.LeaseToken}, \"LeaseExpiresAt\" = {Now.AddMinutes(2)} WHERE \"Id\" = {pending}");
        var token = Guid.NewGuid();
        await AssertViolationAsync(context, PostgresErrorCodes.CheckViolation, "ck_backup_attempt_lease",
            $"UPDATE backup_run_attempts SET \"Status\" = 2 WHERE \"Id\" = {pending}");
        await AssertViolationAsync(context, PostgresErrorCodes.CheckViolation, "ck_backup_attempt_started",
            $"UPDATE backup_run_attempts SET \"Status\" = 3, \"LeaseToken\" = {token}, \"LeaseExpiresAt\" = {Now.AddMinutes(2)} WHERE \"Id\" = {pending}");
        await AssertViolationAsync(context, PostgresErrorCodes.CheckViolation, "ck_backup_attempt_terminal",
            $"UPDATE backup_run_attempts SET \"Status\" = 5 WHERE \"Id\" = {pending}");
        // RetryPending describes a run waiting for its next attempt; an attempt itself is never in it.
        await AssertViolationAsync(context, PostgresErrorCodes.CheckViolation, "ck_backup_run_attempts_status",
            $"UPDATE backup_run_attempts SET \"Status\" = 8 WHERE \"Id\" = {pending}");
        Assert.Equal(BackupStatus.Pending, (await store.GetRunAsync(seed.TenantId, second.Id, default))!.CurrentAttempt.Status);
    }
    private static async Task AssertViolationAsync(PlatformDbContext context, string sqlState, string constraint, FormattableString sql)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(sql));
        Assert.Equal(sqlState, error.SqlState);
        Assert.Equal(constraint, error.ConstraintName);
    }
    private async Task<Seed> SeedAsync(PostgreSqlFixture? database = null)
    {
        var tenantId = TenantId.New();
        var agentId = AgentId.New();
        await using (var context = (database ?? fixture).CreatePlatformContext())
        {
            context.Tenants.Add(new Tenant(tenantId, "Backup tenant"));
            var agent = new BackupPlatform.Domain.Agents.Agent(agentId, tenantId, new AgentSoftware(AgentPlatform.Linux, new Version(1, 0), new AgentProtocolVersion(1, 0)));
            agent.RecordEnrollment();
            context.Agents.Add(agent);
            await context.SaveChangesAsync();
        }
        var job = (await Store(database).CreateJobAsync(new BackupJob(Guid.NewGuid(), tenantId, "Database", agentId, Configuration(), Now, overlapPolicy: BackupOverlapPolicy.Queue), default))!;
        return new Seed(tenantId, agentId, job.Id);
    }
    private async Task<CompletedSeed> CompletedAsync()
    {
        var seed = await SeedAsync();
        var store = Store();
        var run = (await store.StartRunAsync(seed.TenantId, seed.JobId, "complete", Now, default)).Run!;
        var claim = (await store.ClaimNextAsync(seed.TenantId, seed.AgentId, Now, Lease, default))!;
        var command = Command(claim, Now);
        Assert.Equal(BackupMutationResult.Accepted, await store.StartAttemptAsync(command, default));
        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteAttemptAsync(command, Success(Now), default));
        return new CompletedSeed(seed.TenantId, seed.AgentId, seed.JobId, (await store.GetRunAsync(seed.TenantId, run.Id, default))!);
    }
    private static BackupLeaseCommand Command(BackupClaim claim, DateTimeOffset now) => new(claim.Run.TenantId, claim.Run.AgentId,
        claim.Attempt.Id, claim.Attempt.LeaseToken!.Value, now) { SoftwareVersion = "1.0", ProtocolVersion = "1.0", Platform = "Linux" };
    private static Completion Success(DateTimeOffset createdAt) => new("Succeeded", null, new BackupArtifactResult(100, SourceHash, ArtifactHash,
        "sha256", createdAt, "{\"DatabaseName\":\"Database\",\"Nested\":{\"a\":1,\"b\":2}}", "{\"CopyOnly\":true}",
        [new BackupReplicaResult("local", "default", "tenant/job/run/attempt/backup.bak", 100, ArtifactHash, "sha256", "Verified", "ReadBack", createdAt, createdAt)]));
    private static BackupConfiguration Configuration() => new("mssql", "Database", "localhost", null, true, null,
        true, false, "default", "local", "", true, "Preferred", "Preferred")
        { StorageExecutionLocation = BackupStorageExecutionLocation.Backend };
    private sealed record Seed(TenantId TenantId, AgentId AgentId, Guid JobId);
    private sealed record CompletedSeed(TenantId TenantId, AgentId AgentId, Guid JobId, BackupRun Run);
}
