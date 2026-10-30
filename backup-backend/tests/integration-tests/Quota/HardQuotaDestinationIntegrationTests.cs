using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.IntegrationTests.Storage.Connections;
using BackupPlatform.ManagedService.Quota;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.Quota.AdmissionTestKit;
using static BackupPlatform.IntegrationTests.Quota.UsageTestKit;

namespace BackupPlatform.IntegrationTests.Quota;

public sealed class HardQuotaDestinationIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private readonly ConnectionWorld _connections = new(fixture);
    private QuotaWorld World => _connections.Quota;

    private async Task<(StoreAgent Agent, BackupJob Job)> SetupAsync(QuotaAccountingMode mode, QuotaScopeRule scope, long limit = 1000)
    {
        var policy = await World.CreatePolicyAsync(mode, scope);
        var agent = await World.AgentAsync(limit, policy.Key);
        var job = await World.JobAsync(agent);
        return (agent, job);
    }

    private async Task<BackupJob> AddDestinationAsync(StoreAgent agent, BackupJob job)
    {
        var (_, central) = await _connections.DefaultsAsync(agent.TenantId);
        var sftp = await _connections.AddAsync(agent.TenantId, "quota-sftp", "sftp");
        var configuration = job.Configuration with
        {
            Destinations = [new BackupDestination("central", central.Id, true), new BackupDestination("sftp", sftp.Id)]
        };
        return (await World.RunStore().UpdateJobAsync(agent.TenantId, job.Id,
            new BackupJobUpdate(job.Name, agent.AgentId, configuration, null, null), default))!;
    }

    [Theory]
    [InlineData(QuotaAccountingMode.LogicalArtifactBytes, 100)]
    [InlineData(QuotaAccountingMode.PhysicalReplicaBytes, 200)]
    public async Task CurrentDestinationsNotHistoricalReplicaCountDetermineReservation(QuotaAccountingMode mode, long expected)
    {
        var (agent, job) = await SetupAsync(mode, QuotaScopeRule.All);
        await World.PublishAsync(World.RunStore(), agent, job, 100, [ReplicaSpec.Central], T0);
        job = await AddDestinationAsync(agent, job);
        var run = await StartAsync(World.RunStore(), job, "current-destinations", T0.AddMinutes(1));
        Assert.Equal(expected, (await ReservationOfAsync(fixture, agent.TenantId, run.Id)).Bytes);
        var claim = await ClaimStartedAsync(World.RunStore(), agent, T0.AddMinutes(1));
        Assert.Equal(100L, claim.MaxArtifactBytes);
    }
    [Theory]
    [InlineData(QuotaAccountingMode.LogicalArtifactBytes, 201, 101)]
    [InlineData(QuotaAccountingMode.PhysicalReplicaBytes, 302, 202)]
    public async Task ActualGrowthReservesEveryCountedCopyAndKeepsTheHoldOnReplay(QuotaAccountingMode mode, long limit, long expected)
    {
        var (agent, job) = await SetupAsync(mode, QuotaScopeRule.All, limit);
        var store = World.RunStore();
        await World.PublishAsync(store, agent, job, 100, [ReplicaSpec.Central], T0);
        job = await AddDestinationAsync(agent, job);
        var run = await StartAsync(store, job, "growth", T0.AddMinutes(1));
        var claim = await ClaimStartedAsync(store, agent, T0.AddMinutes(1));
        var command = new BackupLeaseCommand(agent.TenantId, agent.AgentId, claim.Attempt.Id, claim.Attempt.LeaseToken!.Value, T0.AddMinutes(1));

        Assert.True((await store.AdjustQuotaReservationAsync(command, 101, default)).Allowed);
        Assert.True((await store.AdjustQuotaReservationAsync(command, 101, default)).Allowed);
        Assert.True((await store.AdjustQuotaReservationAsync(command, 100, default)).Allowed);
        Assert.Equal(expected, (await ReservationOfAsync(fixture, agent.TenantId, run.Id)).Bytes);
        Assert.Equal(expected, (await World.StoredUsageAsync(agent.TenantId))!.ReservedBytes);
        Assert.Equal(101, (await store.GetAttemptAsync(agent.TenantId, agent.AgentId, claim.Attempt.Id, default))!.MaxArtifactBytes);
        Assert.False((await store.AdjustQuotaReservationAsync(command, 102, default)).Allowed);
        Assert.False((await store.AdjustQuotaReservationAsync(command, long.MaxValue, default)).Allowed);
        Assert.Equal(expected, (await ReservationOfAsync(fixture, agent.TenantId, run.Id)).Bytes);
    }

    [Fact]
    public async Task ConcurrentAgentsCannotReserveTheSameLastByte()
    {
        var (first, firstJob) = await SetupAsync(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All, 401);
        var second = await AddAgentAsync(World, first.TenantId);
        var secondJob = await World.JobAsync(second);
        var store = World.RunStore();
        await World.PublishAsync(store, first, firstJob, 100, [ReplicaSpec.Central], T0);
        await World.PublishAsync(store, second, secondJob, 100, [ReplicaSpec.Central], T0);
        await StartAsync(store, firstJob, "first", T0.AddMinutes(1));
        await StartAsync(store, secondJob, "second", T0.AddMinutes(1));
        var firstClaim = await ClaimStartedAsync(store, first, T0.AddMinutes(1));
        var secondClaim = await ClaimStartedAsync(store, second, T0.AddMinutes(1));
        BackupLeaseCommand Command(StoreAgent agent, BackupClaim claim) =>
            new(agent.TenantId, agent.AgentId, claim.Attempt.Id, claim.Attempt.LeaseToken!.Value, T0.AddMinutes(1));

        var results = await Task.WhenAll(
            World.RunStore().AdjustQuotaReservationAsync(Command(first, firstClaim), 101, default),
            World.RunStore().AdjustQuotaReservationAsync(Command(second, secondClaim), 101, default));
        Assert.All(results, result => Assert.Equal(BackupMutationResult.Accepted, result.Mutation));
        Assert.Single(results, result => result.Allowed);
        Assert.Equal(201, (await World.StoredUsageAsync(first.TenantId))!.ReservedBytes);
        Assert.Equal(401, (await World.StoredUsageAsync(first.TenantId))!.UsedBytes + (await World.StoredUsageAsync(first.TenantId))!.ReservedBytes);
    }

    [Fact]
    public async Task AdjustmentRejectsStaleLeasesAndMissingReservations()
    {
        var (agent, job) = await SetupAsync(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All);
        var store = World.RunStore();
        await World.PublishAsync(store, agent, job, 100, [ReplicaSpec.Central], T0);
        var run = await StartAsync(store, job, "fence", T0.AddMinutes(1));
        var claim = await ClaimStartedAsync(store, agent, T0.AddMinutes(1));
        var command = new BackupLeaseCommand(agent.TenantId, agent.AgentId, claim.Attempt.Id, claim.Attempt.LeaseToken!.Value, T0.AddMinutes(1));
        Assert.Equal(BackupMutationResult.Conflict, (await store.AdjustQuotaReservationAsync(command with { LeaseToken = Guid.NewGuid() }, 101, default)).Mutation);
        Assert.Equal(BackupMutationResult.Conflict, (await store.AdjustQuotaReservationAsync(command with { Now = claim.Attempt.LeaseExpiresAt!.Value }, 101, default)).Mutation);
        await World.Admission().ReleaseAsync(agent.TenantId, run.Id, default);
        var missing = await store.AdjustQuotaReservationAsync(command, 101, default);
        Assert.Equal(BackupMutationResult.Accepted, missing.Mutation);
        Assert.False(missing.Allowed);
    }

    [Fact]
    public async Task ResumeDoesNotCountAnExistingIntentTwiceOrReleaseItsHold()
    {
        var (agent, job) = await SetupAsync(QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeRule.All, 300);
        var store = World.RunStore();
        await World.PublishAsync(store, agent, job, 100, [ReplicaSpec.Central], T0);
        job = await AddDestinationAsync(agent, job);
        var run = await StartAsync(store, job, "resume", T0.AddMinutes(1));
        var claim = await ClaimStartedAsync(store, agent, T0.AddMinutes(1));
        var command = new BackupLeaseCommand(agent.TenantId, agent.AgentId, claim.Attempt.Id, claim.Attempt.LeaseToken!.Value, T0.AddMinutes(1));
        var sftp = job.Configuration.Destinations.Single(destination => destination.StorageProvider == "sftp");
        var key = $"{ManagedBackupLayout.AttemptScope(agent.TenantId.Value, job.Id, run.Id, claim.Attempt.Id)}/Sales_20261005T000000Z_aaaaaaaa_FULL.bak";
        Assert.Equal(BackupMutationResult.Accepted, await store.RecordTransferAsync(command,
            new BackupTransferIntent(sftp.StorageConnectionId!.Value, key, 100, BackupStoreHarness.ArtifactHash), default));
        Assert.True((await store.AdjustQuotaReservationAsync(command, 100, default)).Allowed);
        Assert.Equal(200, (await ReservationOfAsync(fixture, agent.TenantId, run.Id)).Bytes);
        var usage = (await World.StoredUsageAsync(agent.TenantId))!;
        Assert.Equal(200, usage.UsedBytes);
        Assert.Equal(100, usage.ReservedBytes);
    }


    [Fact]
    public async Task CurrentScopeCountsNewDestinationEvenWhenHistoryWasEntirelyOutsideScope()
    {
        var (agent, job) = await SetupAsync(QuotaAccountingMode.PhysicalReplicaBytes, Providers("sftp"));
        await World.PublishAsync(World.RunStore(), agent, job, 100, [ReplicaSpec.Central], T0);
        job = await AddDestinationAsync(agent, job);
        var run = await StartAsync(World.RunStore(), job, "new-counted-target", T0.AddMinutes(1));
        Assert.Equal(100L, (await ReservationOfAsync(fixture, agent.TenantId, run.Id)).Bytes);
        Assert.Equal(0L, (await World.StoredUsageAsync(agent.TenantId))!.UsedBytes);
    }

    [Fact]
    public async Task FirstRunOutsideScopeHasNoReservationOrHardArtifactBound()
    {
        var (agent, job) = await SetupAsync(QuotaAccountingMode.PhysicalReplicaBytes, Providers("sftp"));
        await StartAsync(World.RunStore(), job, "outside", T0);
        var claim = await ClaimStartedAsync(World.RunStore(), agent, T0);
        Assert.Null(claim.MaxArtifactBytes);
        Assert.Empty(await ReservationsAsync(fixture, agent.TenantId));
        var command = new BackupLeaseCommand(agent.TenantId, agent.AgentId, claim.Attempt.Id, claim.Attempt.LeaseToken!.Value, T0);
        Assert.True((await World.RunStore().AdjustQuotaReservationAsync(command, long.MaxValue, default)).Allowed);
    }

    [Theory]
    [InlineData(299, false)]
    [InlineData(300, true)]
    public async Task PhysicalReservationHonorsExactCapacityBoundary(long limit, bool allowed)
    {
        var (agent, job) = await SetupAsync(QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeRule.All, limit);
        await World.PublishAsync(World.RunStore(), agent, job, 100, [ReplicaSpec.Central], T0);
        job = await AddDestinationAsync(agent, job);
        var result = await World.RunStore().StartRunAsync(agent.TenantId, job.Id, "boundary", T0.AddMinutes(1), default);
        Assert.Equal(allowed ? BackupStartOutcome.Started : BackupStartOutcome.AdmissionDenied, result.Outcome);
        Assert.Equal(allowed ? null : BackupAdmissionDenialCodes.QuotaExceeded, result.DenialCode);
        Assert.Equal(allowed ? 200L : 0L, (await World.StoredUsageAsync(agent.TenantId))!.ReservedBytes);
    }

    [Fact]
    public async Task ConcurrentPhysicalAdmissionsCannotSpendTheSameLastTwoCopies()
    {
        var (agent, job) = await SetupAsync(QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeRule.All, 300);
        await World.PublishAsync(World.RunStore(), agent, job, 100, [ReplicaSpec.Central], T0);
        job = await AddDestinationAsync(agent, job);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(index =>
            World.RunStore().StartRunAsync(agent.TenantId, job.Id, $"race-{index}", T0.AddMinutes(1), default)));
        Assert.Single(results, result => result.Outcome == BackupStartOutcome.Started);
        Assert.Equal(5, results.Count(result => result.Outcome == BackupStartOutcome.AdmissionDenied && result.DenialCode == BackupAdmissionDenialCodes.QuotaExceeded));
        Assert.Equal(200L, Assert.Single(await UnreleasedAsync(fixture, agent.TenantId)).Bytes);
    }

    [Fact]
    public async Task RepeatingAdmissionAtAnExactFullReservationIsIdempotent()
    {
        var (agent, job) = await SetupAsync(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All, 200);
        await World.PublishAsync(World.RunStore(), agent, job, 100, [ReplicaSpec.Central], T0);
        var request = new BackupAdmissionRequest(agent.TenantId, job.Id, Guid.NewGuid(), BackupTriggerTypes.Manual, T0.AddMinutes(1));
        Assert.True((await World.Admission().AdmitNewRunAsync(request, default)).Allowed);
        Assert.True((await World.Admission().AdmitNewRunAsync(request, default)).Allowed);
        Assert.Equal(100L, Assert.Single(await UnreleasedAsync(fixture, agent.TenantId)).Bytes);
    }

    [Fact]
    public async Task MissingOrReleasedHardReservationFailsClosedAtClaim()
    {
        var (agent, job) = await SetupAsync(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All);
        await World.PublishAsync(World.RunStore(), agent, job, 100, [ReplicaSpec.Central], T0);
        var run = await StartAsync(World.RunStore(), job, "released", T0.AddMinutes(1));
        await World.Admission().ReleaseAsync(agent.TenantId, run.Id, default);
        var claim = await ClaimStartedAsync(World.RunStore(), agent, T0.AddMinutes(1));
        Assert.Equal(0L, claim.MaxArtifactBytes);
    }

    [Fact]
    public async Task PriorAttemptOrphansReduceTheRetryArtifactBudget()
    {
        var (agent, job) = await SetupAsync(QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeRule.All);
        await World.PublishAsync(World.RunStore(), agent, job, 100, [ReplicaSpec.Central], T0);
        job = await AddDestinationAsync(agent, job);
        var store = World.RunStore();
        var run = await StartAsync(store, job, "orphan-budget", T0.AddMinutes(1));
        var claim = await ClaimStartedAsync(store, agent, T0.AddMinutes(1));
        var sftp = job.Configuration.Destinations.Single(destination => destination.StorageProvider == "sftp");
        var at = T0.AddMinutes(2);
        var artifact = QuotaWorld.Success(100, [], at).Artifact!;
        var completion = new BackupPlatform.Application.Backups.BackupCompletion("Failed", BackupFailureCodes.ReplicaVerificationFailed, artifact,
            [new BackupDestinationReport("central", job.Configuration.Destinations.Single(destination => destination.IsPrimary).StorageConnectionId, "Failed", "Failed", "NotRun",
                BackupFailureCodes.StorageWriteFailed, null, false, at, at),
             new BackupDestinationReport("sftp", sftp.StorageConnectionId, "Failed", "Succeeded", "Failed",
                BackupFailureCodes.ReplicaVerificationFailed,
                $"{ManagedBackupLayout.AttemptScope(agent.TenantId.Value, job.Id, run.Id, claim.Attempt.Id)}/Sales_20261005T000000Z_aaaaaaaa_FULL.bak", true, at, at)]);
        await BackupStoreHarness.CompleteAsync(store, claim, completion, at);
        Assert.Equal(BackupStatus.RetryPending, (await store.GetRunAsync(agent.TenantId, run.Id, default))!.Status);
        Assert.Equal(200L, (await World.StoredUsageAsync(agent.TenantId))!.UsedBytes);
        Assert.Equal(100L, (await World.StoredUsageAsync(agent.TenantId))!.ReservedBytes);
        Assert.False((await World.Usage().ReconcileAsync(agent.TenantId, null, at, default))!.Drifted);
        Assert.Null((await ReservationOfAsync(fixture, agent.TenantId, run.Id)).ReleasedAt);
        await store.StartDueRetriesAsync(at.AddMinutes(1), 100, default);
        var retry = await ClaimStartedAsync(store, agent, at.AddMinutes(1));
        Assert.Equal(50L, retry.MaxArtifactBytes);
        var command = new BackupLeaseCommand(agent.TenantId, agent.AgentId, retry.Attempt.Id, retry.Attempt.LeaseToken!.Value, at.AddMinutes(1));
        Assert.True((await store.AdjustQuotaReservationAsync(command, 101, default)).Allowed);
        Assert.Equal(302, (await ReservationOfAsync(fixture, agent.TenantId, run.Id)).Bytes);
        Assert.Equal(202, (await World.StoredUsageAsync(agent.TenantId))!.ReservedBytes);
        Assert.Equal(101, (await store.GetAttemptAsync(agent.TenantId, agent.AgentId, retry.Attempt.Id, default))!.MaxArtifactBytes);
        Assert.Equal(BackupMutationResult.Conflict, (await store.AdjustQuotaReservationAsync(
            command with { AttemptId = claim.Attempt.Id, LeaseToken = claim.Attempt.LeaseToken!.Value }, 1, default)).Mutation);
    }

    [Theory]
    [InlineData(QuotaEnforcementMode.BlockNewBackups, false)]
    [InlineData(QuotaEnforcementMode.WarnOnly, true)]
    [InlineData(QuotaEnforcementMode.None, true)]
    public async Task OldAgentsAreRefusedOnlyForCountedHardQuota(QuotaEnforcementMode enforcement, bool allowed)
    {
        var policy = await World.CreatePolicyAsync(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All, enforcement);
        var agent = await World.AgentAsync(1000, policy.Key);
        var job = await World.JobAsync(agent);
        await World.PublishAsync(World.RunStore(), agent, job, 100, [ReplicaSpec.Central], T0);
        await using (var context = fixture.CreatePlatformContext())
        {
            await context.AgentCapabilities.Where(row => row.AgentId == agent.AgentId && row.Identifier == BackupTaskProtocol.QuotaBoundedUploadCapability).ExecuteDeleteAsync();
        }
        var result = await World.RunStore().StartRunAsync(agent.TenantId, job.Id, "old-agent", T0.AddMinutes(1), default);
        Assert.Equal(allowed ? BackupStartOutcome.Started : BackupStartOutcome.AdmissionDenied, result.Outcome);
        Assert.Equal(allowed ? null : BackupAdmissionDenialCodes.QuotaBoundedUploadUnsupported, result.DenialCode);
    }

    [Fact]
    public async Task UnknownDestinationIdentityCannotBeMistakenForUncountedStorage()
    {
        var (agent, job) = await SetupAsync(QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeRule.All);
        await World.PublishAsync(World.RunStore(), agent, job, 100, [ReplicaSpec.Central], T0);
        var decision = await World.Admission().AdmitNewRunAsync(
            new BackupAdmissionRequest(agent.TenantId, job.Id, Guid.NewGuid(), BackupTriggerTypes.Manual, T0.AddMinutes(1))
            {
                Configuration = job.Configuration with { Destinations = [new BackupDestination("central", Guid.NewGuid(), true) { StorageExecutionLocation = BackupStorageExecutionLocation.Backend }] }
            }, default);
        Assert.False(decision.Allowed);
        Assert.Equal(BackupAdmissionDenialCodes.QuotaSizeUnknown, decision.DenialCode);
        Assert.Empty(await UnreleasedAsync(fixture, agent.TenantId));
    }

    [Fact]
    public async Task QueuedHardRunWaitsWhenAgentLosesBoundedUploadCapability()
    {
        var (agent, job) = await SetupAsync(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All);
        await World.PublishAsync(World.RunStore(), agent, job, 100, [ReplicaSpec.Central], T0);
        await StartAsync(World.RunStore(), job, "queued", T0.AddMinutes(1));
        await using (var context = fixture.CreatePlatformContext())
        {
            await context.AgentCapabilities.Where(row => row.AgentId == agent.AgentId && row.Identifier == BackupTaskProtocol.QuotaBoundedUploadCapability).ExecuteDeleteAsync();
        }
        Assert.Null(await BackupStoreHarness.ClaimAsync(World.RunStore(), agent, T0.AddMinutes(1)));
        Assert.Equal(100L, Assert.Single(await UnreleasedAsync(fixture, agent.TenantId)).Bytes);
    }

    [Fact]
    public async Task UnlimitedFirstRunRemainsCompatibleWithOldAgents()
    {
        var agent = await World.AgentAsync(limitBytes: null);
        var job = await World.JobAsync(agent);
        await using (var context = fixture.CreatePlatformContext())
        {
            await context.AgentCapabilities.Where(row => row.AgentId == agent.AgentId && row.Identifier == BackupTaskProtocol.QuotaBoundedUploadCapability).ExecuteDeleteAsync();
        }
        await StartAsync(World.RunStore(), job, "old-unlimited", T0);
        var claim = await ClaimStartedAsync(World.RunStore(), agent, T0);
        Assert.Null(claim.MaxArtifactBytes);
        var command = new BackupLeaseCommand(agent.TenantId, agent.AgentId, claim.Attempt.Id, claim.Attempt.LeaseToken!.Value, T0);
        Assert.True((await World.RunStore().AdjustQuotaReservationAsync(command, long.MaxValue, default)).Allowed);
        Assert.Empty(await ReservationsAsync(fixture, agent.TenantId));
    }
}
