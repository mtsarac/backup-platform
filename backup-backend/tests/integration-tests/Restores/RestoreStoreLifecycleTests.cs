using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Restores;
using BackupPlatform.Domain.Restores;
using static BackupPlatform.IntegrationTests.RestoreWorld;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// The lifecycle of a restore through the real store: claim, start, lease renewal, progress, completion, cancellation and expiry. Every instant is explicit,
/// every expectation is read back from the database rows, and every terminal path must release the replica pin in the same change.
/// </summary>
public sealed class RestoreStoreLifecycleTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private readonly RestoreWorld _world = new(fixture);
    private static DateTimeOffset At(int seconds) => Now.AddSeconds(seconds);

    [Fact]
    public async Task ClaimHandsOutTheOldestPendingRestoreWithItsReplicaAndLeasesIt()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var oldest = await _world.CreateAsync(seed, store, At(0));
        var newer = await _world.CreateAsync(seed, store, At(1));

        var claim = await _world.Store().ClaimNextAsync(seed.TenantId, seed.AgentId, At(10), Lease, Unclaimed, default);

        Assert.NotNull(claim);
        Assert.Equal(oldest.Id, claim.Operation.Id);
        Assert.Equal(RestoreStatus.Leased, claim.Operation.Status);
        Assert.Equal(RestoreStatus.Leased, claim.Attempt.Status);
        Assert.Equal(At(10) + Lease, claim.Attempt.LeaseExpiresAt);
        Assert.NotEqual(Guid.Empty, claim.Attempt.LeaseToken);
        Assert.Equal(seed.ReplicaId, claim.Replica.Id);
        Assert.Equal(seed.Backup.Key, claim.Replica.Key);
        Assert.Equal("central", claim.Replica.StorageProvider);
        Assert.Equal(100, claim.Replica.SizeBytes);
        var persisted = await _world.OperationAsync(oldest.Id);
        Assert.Equal(RestoreStatus.Leased, persisted.Status);
        Assert.Equal(claim.Attempt.LeaseToken, persisted.CurrentAttempt.LeaseToken);
        Assert.Equal(RestoreStatus.Pending, (await _world.OperationAsync(newer.Id)).Status);
    }

    [Fact]
    public async Task ASecondClaimOfTheSameAgentGetsNothingWhileTheFirstRestoreHoldsTheAgent()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        await _world.CreateAsync(seed, store, At(0));
        await _world.CreateAsync(seed, store, At(1));
        await MustClaimAsync(store, seed.Agent, At(10));

        Assert.Null(await ClaimAsync(store, seed.Agent, At(11)));
        Assert.Null(await ClaimAsync(_world.Store(), seed.Agent, At(Lease.Seconds)));
    }

    [Fact]
    public async Task NothingPendingMeansNothingClaimed()
    {
        var seed = await _world.SeedCentralAsync();

        Assert.Null(await ClaimAsync(_world.Store(), seed.Agent, Now));
    }

    [Fact]
    public async Task OnlyTheTargetAgentOfTheSameTenantCanClaimARestore()
    {
        var seed = await _world.SeedCentralAsync();
        var sibling = await _world.AddAgentAsync(seed.TenantId);
        var foreign = await _world.SeedCentralAsync();
        var store = _world.Store();
        var operation = await _world.CreateAsync(seed, store);

        Assert.Null(await ClaimAsync(store, sibling, At(5)));
        Assert.Null(await ClaimAsync(store, foreign.Agent, At(5)));
        // The tenant is part of the key: the right agent id under another tenant finds nothing either.
        Assert.Null(await store.ClaimNextAsync(foreign.TenantId, seed.AgentId, At(5), Lease, Unclaimed, default));
        Assert.Equal(operation.Id, (await MustClaimAsync(store, seed.Agent, At(5))).Operation.Id);
    }

    [Fact]
    public async Task ManyNodesClaimingAtOnceHandOutExactlyOneRestoreToTheAgent()
    {
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var seed = await _world.SeedCentralAsync();
            var store = _world.Store();
            for (var index = 0; index < 3; index++) await _world.CreateAsync(seed, store, At(index));
            var gate = new RaceGate();
            var racers = Enumerable.Range(0, 5).Select(_ =>
            {
                var node = _world.Store();
                return gate.Run(() => ClaimAsync(node, seed.Agent, At(30)));
            }).ToArray();
            gate.Open();

            var claims = await Task.WhenAll(racers);

            var winner = Assert.Single(claims, claim => claim is not null)!;
            Assert.Equal(4, claims.Count(claim => claim is null));
            var page = await store.ListAsync(seed.TenantId, new RestoreQuery(Status: RestoreStatus.Leased), default);
            Assert.Equal(winner.Operation.Id, Assert.Single(page.Items).Id);
            Assert.Equal(2, (await store.ListAsync(seed.TenantId, new RestoreQuery(Status: RestoreStatus.Pending), default)).TotalCount);
        }
    }

    [Fact]
    public async Task StartRenewProgressAndSuccessfulCompletionMoveTheRestoreThroughItsLifecycleAndReleaseThePin()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var operation = await _world.CreateAsync(seed, store, At(0));
        var claim = await MustClaimAsync(store, seed.Agent, At(10));

        var started = await store.StartAsync(Command(claim, At(11)), Lease, default);
        var renewed = await store.RenewLeaseAsync(Command(claim, At(40)), Lease, default);
        var progress = await ProgressAsync(store, claim, At(41), RestoreStages.Restoring, 42.5);
        var running = await _world.OperationAsync(operation.Id);

        Assert.Equal(BackupMutationResult.Accepted, started.Result);
        Assert.Equal(At(11) + Lease, started.LeaseExpiresAt);
        Assert.False(started.CancellationRequested);
        Assert.Equal(At(40) + Lease, renewed.LeaseExpiresAt);
        Assert.Equal(BackupMutationResult.Accepted, progress);
        Assert.Equal(RestoreStatus.Running, running.Status);
        Assert.Equal(At(11), running.StartedAt);
        Assert.Equal("1.2.3", running.CurrentAttempt.SoftwareVersion);
        Assert.Equal(RestoreStages.Restoring, running.CurrentAttempt.ProgressStage);
        Assert.Equal(42.5, running.CurrentAttempt.ProgressPercent);
        Assert.Equal(1, await _world.ActivePinsAsync(seed.ReplicaId));

        Assert.Equal(BackupMutationResult.Accepted, await CompleteAsync(store, claim, At(90), Succeeded()));

        var done = await _world.OperationAsync(operation.Id);
        Assert.Equal(RestoreStatus.Succeeded, done.Status);
        Assert.Equal(RestoreTargetState.Online, done.TargetState);
        Assert.Null(done.FailureCode);
        Assert.Equal(At(90), done.CompletedAt);
        Assert.Contains("ONLINE", done.ResultJson);
        Assert.Equal(RestoreStatus.Succeeded, done.CurrentAttempt.Status);
        Assert.Equal(RestoreStages.Completed, done.CurrentAttempt.ProgressStage);
        Assert.False(done.Pin.IsActive);
        Assert.Equal(At(90), done.Pin.ReleasedAt);
        Assert.Equal(0, await _world.ActivePinsAsync(seed.ReplicaId));
    }

    [Fact]
    public async Task EveryTerminalOutcomeReleasesThePinAndRecordsTheTargetStateTheAgentReported()
    {
        var cases = new (RestoreCompletion Completion, RestoreStatus Status, string? Code, RestoreTargetState State)[]
        {
            (Failed(RestoreFailureCodes.ChecksumMismatch), RestoreStatus.Failed, RestoreFailureCodes.ChecksumMismatch, RestoreTargetState.NotModified),
            (Failed(RestoreFailureCodes.SqlRestoreFailed, RestoreTargetState.PartiallyRestored), RestoreStatus.Failed, RestoreFailureCodes.SqlRestoreFailed, RestoreTargetState.PartiallyRestored),
            (Failed(RestoreFailureCodes.DatabaseUnavailable, RestoreTargetState.Unknown), RestoreStatus.Failed, RestoreFailureCodes.DatabaseUnavailable, RestoreTargetState.Unknown),
            (Cancelled(RestoreTargetState.NotModified), RestoreStatus.Cancelled, RestoreFailureCodes.Cancelled, RestoreTargetState.NotModified),
            (Cancelled(RestoreTargetState.PartiallyRestored), RestoreStatus.Cancelled, RestoreFailureCodes.Cancelled, RestoreTargetState.PartiallyRestored)
        };
        foreach (var (completion, status, code, state) in cases)
        {
            var seed = await _world.SeedCentralAsync();
            var store = _world.Store();
            var operation = await _world.CreateAsync(seed, store, At(0));
            var claim = await RunToAsync(store, seed.Agent, At(10), RestoreStages.Preparing);

            Assert.Equal(BackupMutationResult.Accepted, await CompleteAsync(store, claim, At(20), completion));

            var done = await _world.OperationAsync(operation.Id);
            Assert.Equal((status, code, state), (done.Status, done.FailureCode, done.TargetState));
            Assert.Equal(At(20), done.CompletedAt);
            Assert.False(done.Pin.IsActive);
            Assert.Equal(0, await _world.ActivePinsAsync(seed.ReplicaId));
        }
    }

    [Fact]
    public async Task ACompletionWithAWrongTokenAnotherAgentOrAnUnknownAttemptIsRefusedAndChangesNothing()
    {
        var seed = await _world.SeedCentralAsync();
        var sibling = await _world.AddAgentAsync(seed.TenantId);
        var foreign = await _world.SeedCentralAsync();
        var store = _world.Store();
        var operation = await _world.CreateAsync(seed, store);
        var claim = await RunToAsync(store, seed.Agent, At(10));

        var wrongToken = await CompleteAsync(store, claim, At(20), Succeeded(), Guid.NewGuid());
        var otherAgent = await store.CompleteAsync(new RestoreLeaseCommand(seed.TenantId, sibling.AgentId, claim.Attempt.Id, claim.Attempt.LeaseToken!.Value, At(20)), Succeeded(), default);
        var otherTenant = await store.CompleteAsync(new RestoreLeaseCommand(foreign.TenantId, seed.AgentId, claim.Attempt.Id, claim.Attempt.LeaseToken!.Value, At(20)), Succeeded(), default);
        var unknown = await store.CompleteAsync(new RestoreLeaseCommand(seed.TenantId, seed.AgentId, Guid.NewGuid(), claim.Attempt.LeaseToken!.Value, At(20)), Succeeded(), default);

        Assert.Equal(BackupMutationResult.Conflict, wrongToken);
        Assert.Equal(BackupMutationResult.NotFound, otherAgent);
        Assert.Equal(BackupMutationResult.NotFound, otherTenant);
        Assert.Equal(BackupMutationResult.NotFound, unknown);
        Assert.Equal(RestoreStatus.Running, (await _world.OperationAsync(operation.Id)).Status);
        Assert.Equal(1, await _world.ActivePinsAsync(seed.ReplicaId));
    }

    [Fact]
    public async Task AnExpiredLeaseIsAStaleLeaseForEveryCallbackAndTheRestoreIsLostOnlyWhenTheSweepRuns()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var operation = await _world.CreateAsync(seed, store);
        var claim = await RunToAsync(store, seed.Agent, At(10), RestoreStages.DownloadingArtifact);
        var late = At(10) + Lease + TimeSpan.FromSeconds(1);

        Assert.Equal(BackupMutationResult.Conflict, await CompleteAsync(store, claim, late, Succeeded()));
        Assert.Equal(BackupMutationResult.Conflict, await ProgressAsync(store, claim, late, RestoreStages.Restoring));
        Assert.Equal(BackupMutationResult.Conflict, (await store.RenewLeaseAsync(Command(claim, late), Lease, default)).Result);
        Assert.Equal(RestoreStatus.Running, (await _world.OperationAsync(operation.Id)).Status);

        Assert.True(await store.ExpireStaleAsync(late, default) >= 1);
        Assert.Equal(RestoreStatus.AgentLost, (await _world.OperationAsync(operation.Id)).Status);
        Assert.Equal(BackupMutationResult.Conflict, await CompleteAsync(store, claim, late, Succeeded()));
    }

    [Fact]
    public async Task ProgressNeedsARunningAttemptAndAKnownNonTerminalStage()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        await _world.CreateAsync(seed, store);
        var claim = await MustClaimAsync(store, seed.Agent, At(10));

        Assert.Equal(BackupMutationResult.Conflict, await ProgressAsync(store, claim, At(11), RestoreStages.Preparing));
        await StartAsync(store, claim, At(12));
        Assert.Equal(BackupMutationResult.Accepted, await ProgressAsync(store, claim, At(13), RestoreStages.Preparing, 0));
        Assert.Equal(BackupMutationResult.Conflict, await ProgressAsync(store, claim, At(14), RestoreStages.Completed));
        Assert.Equal(BackupMutationResult.Conflict, await ProgressAsync(store, claim, At(14), "NoSuchStage"));
        Assert.Equal(BackupMutationResult.Conflict, await ProgressAsync(store, claim, At(14), RestoreStages.Restoring, 120));
    }

    [Fact]
    public async Task ARepeatedCompletionIsAcceptedOnceAndLeavesTheOutcomeUntouched()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var operation = await _world.CreateAsync(seed, store);
        var claim = await RunToAsync(store, seed.Agent, At(10), RestoreStages.Finalizing);
        Assert.Equal(BackupMutationResult.Accepted, await CompleteAsync(store, claim, At(20), Succeeded()));
        var first = await _world.OperationAsync(operation.Id);

        // The agent retries after a lost response, later and from another node: still the same outcome, still 'accepted', nothing rewritten.
        var again = await CompleteAsync(_world.Store(), claim, At(500), Succeeded());

        Assert.Equal(BackupMutationResult.Accepted, again);
        var second = await _world.OperationAsync(operation.Id);
        Assert.Equal(first.CompletedAt, second.CompletedAt);
        Assert.Equal(first.ResultJson, second.ResultJson);
        Assert.Equal(first.Pin.ReleasedAt, second.Pin.ReleasedAt);
    }

    [Fact]
    public async Task ARepeatedCompletionIsTheSameEvenWhenTheDatabaseNormalisedTheStoredJson()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var operation = await _world.CreateAsync(seed, store);
        var claim = await RunToAsync(store, seed.Agent, At(10));
        const string reported = "{\"warnings\":[],\"databaseState\":\"ONLINE\",\"fileMappings\":[{\"logicalName\":\"data\",\"sizeBytes\":1}]}";
        Assert.Equal(BackupMutationResult.Accepted, await CompleteAsync(store, claim, At(20), Succeeded(reported)));
        // jsonb keeps neither the key order nor the spacing of what was reported.
        Assert.NotEqual(reported, (await _world.OperationAsync(operation.Id)).ResultJson);

        Assert.Equal(BackupMutationResult.Accepted, await CompleteAsync(store, claim, At(30), Succeeded(reported)));
        Assert.Equal(BackupMutationResult.Accepted, await CompleteAsync(store, claim, At(31),
            Succeeded("{ \"fileMappings\": [ { \"sizeBytes\": 1, \"logicalName\": \"data\" } ], \"databaseState\": \"ONLINE\", \"warnings\": [] }")));
        Assert.Equal(BackupMutationResult.Conflict, await CompleteAsync(store, claim, At(32),
            Succeeded("{\"warnings\":[],\"databaseState\":\"ONLINE\",\"fileMappings\":[{\"logicalName\":\"data\",\"sizeBytes\":2}]}")));
    }

    [Fact]
    public async Task ADifferentCompletionAfterTheOutcomeWasRecordedIsAConflict()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var operation = await _world.CreateAsync(seed, store);
        var claim = await RunToAsync(store, seed.Agent, At(10));
        Assert.Equal(BackupMutationResult.Accepted, await CompleteAsync(store, claim, At(20), Succeeded()));

        Assert.Equal(BackupMutationResult.Conflict, await CompleteAsync(store, claim, At(21), Failed()));
        Assert.Equal(BackupMutationResult.Conflict, await CompleteAsync(store, claim, At(21), Cancelled()));
        Assert.Equal(BackupMutationResult.Conflict, await CompleteAsync(store, claim, At(21), Succeeded("{\"databaseState\":\"RESTORING\"}")));
        var done = await _world.OperationAsync(operation.Id);
        Assert.Equal((RestoreStatus.Succeeded, RestoreTargetState.Online), (done.Status, done.TargetState));
    }

    [Fact]
    public async Task ASuccessCompletionNeedsARunningAttempt()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var operation = await _world.CreateAsync(seed, store);
        var claim = await MustClaimAsync(store, seed.Agent, At(10));

        Assert.Equal(BackupMutationResult.Conflict, await CompleteAsync(store, claim, At(11), Succeeded()));
        Assert.Equal(BackupMutationResult.Accepted, await CompleteAsync(store, claim, At(12), Failed(RestoreFailureCodes.PreCheckFailed)));
        Assert.Equal(RestoreStatus.Failed, (await _world.OperationAsync(operation.Id)).Status);
    }

    // ---- cancellation ----

    [Fact]
    public async Task CancellingAPendingRestoreEndsItAtOnceUntouchedAndReleasesThePin()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var operation = await _world.CreateAsync(seed, store);

        var cancelled = await store.RequestCancellationAsync(seed.TenantId, operation.Id, At(5), default);

        Assert.NotNull(cancelled);
        Assert.Equal(RestoreStatus.Cancelled, cancelled.Status);
        var stored = await _world.OperationAsync(operation.Id);
        Assert.Equal((RestoreStatus.Cancelled, RestoreFailureCodes.Cancelled, RestoreTargetState.NotModified), (stored.Status, stored.FailureCode, stored.TargetState));
        Assert.Equal(At(5), stored.CompletedAt);
        Assert.Equal(At(5), stored.CancellationRequestedAt);
        Assert.Equal(RestoreStatus.Cancelled, stored.CurrentAttempt.Status);
        Assert.False(stored.Pin.IsActive);
        Assert.Null(await ClaimAsync(store, seed.Agent, At(6)));
    }

    [Fact]
    public async Task CancellingARunningRestoreOnlyFlagsItAndTheAgentLearnsOnItsNextLeaseRenewal()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var operation = await _world.CreateAsync(seed, store);
        var claim = await RunToAsync(store, seed.Agent, At(10), RestoreStages.Restoring);

        var flagged = await store.RequestCancellationAsync(seed.TenantId, operation.Id, At(20), default);
        var again = await store.RequestCancellationAsync(seed.TenantId, operation.Id, At(25), default);

        Assert.Equal(RestoreStatus.Running, flagged!.Status);
        Assert.Equal(At(20), again!.CancellationRequestedAt);
        var stored = await _world.OperationAsync(operation.Id);
        Assert.Equal(RestoreStatus.Running, stored.Status);
        Assert.True(stored.Pin.IsActive);
        var renewal = await store.RenewLeaseAsync(Command(claim, At(30)), Lease, default);
        Assert.Equal(BackupMutationResult.Accepted, renewal.Result);
        Assert.True(renewal.CancellationRequested);

        // The agent stops the RESTORE statement and says honestly what it left behind: the database is half restored and stays reported that way.
        Assert.Equal(BackupMutationResult.Accepted, await CompleteAsync(store, claim, At(35), Cancelled(RestoreTargetState.PartiallyRestored)));
        var done = await _world.OperationAsync(operation.Id);
        Assert.Equal((RestoreStatus.Cancelled, RestoreFailureCodes.Cancelled, RestoreTargetState.PartiallyRestored), (done.Status, done.FailureCode, done.TargetState));
        Assert.Equal(At(20), done.CancellationRequestedAt);
        Assert.False(done.Pin.IsActive);
    }

    [Fact]
    public async Task ARestoreCancelledWhileLeasedLearnsItWhenItStarts()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var operation = await _world.CreateAsync(seed, store);
        var claim = await MustClaimAsync(store, seed.Agent, At(10));

        await store.RequestCancellationAsync(seed.TenantId, operation.Id, At(11), default);
        var started = await store.StartAsync(Command(claim, At(12)), Lease, default);

        Assert.Equal(BackupMutationResult.Accepted, started.Result);
        Assert.True(started.CancellationRequested);
        Assert.Equal(1, await _world.ActivePinsAsync(seed.ReplicaId));
    }

    [Fact]
    public async Task CancellingATerminalRestoreChangesNothing()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var operation = await _world.CreateAsync(seed, store);
        var claim = await RunToAsync(store, seed.Agent, At(10));
        await CompleteAsync(store, claim, At(20), Succeeded());

        var result = await store.RequestCancellationAsync(seed.TenantId, operation.Id, At(30), default);

        Assert.Equal(RestoreStatus.Succeeded, result!.Status);
        var stored = await _world.OperationAsync(operation.Id);
        Assert.Null(stored.CancellationRequestedAt);
        Assert.Equal(RestoreTargetState.Online, stored.TargetState);
    }

    [Fact]
    public async Task ACancelRacingAClaimEndsConsistentlyWhicheverWins()
    {
        for (var iteration = 0; iteration < 15; iteration++)
        {
            var seed = await _world.SeedCentralAsync();
            var store = _world.Store();
            var operation = await _world.CreateAsync(seed, store);
            var gate = new RaceGate();
            var claiming = gate.Run(() => ClaimAsync(_world.Store(), seed.Agent, At(10)));
            var cancelling = gate.Run(() => _world.Store().RequestCancellationAsync(seed.TenantId, operation.Id, At(11), default));
            gate.Open();

            var claim = await claiming;
            await cancelling;

            var stored = await _world.OperationAsync(operation.Id);
            if (claim is null)
            {
                Assert.Equal((RestoreStatus.Cancelled, RestoreTargetState.NotModified), (stored.Status, stored.TargetState));
                Assert.False(stored.Pin.IsActive);
            }
            else
            {
                // The claim won: the agent holds the lease and is told to stop; the restore is not silently cancelled behind its back.
                Assert.Equal(RestoreStatus.Leased, stored.Status);
                Assert.Equal(At(11), stored.CancellationRequestedAt);
                Assert.True(stored.Pin.IsActive);
            }
        }
    }

    // ---- expiry ----

    [Fact]
    public async Task LostRunningLeaseReportsUnknownDespiteStaleDownloadProgressAndReleasesItsPin()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var operation = await _world.CreateAsync(seed, store);
        var claim = await RunToAsync(store, seed.Agent, At(10), RestoreStages.DownloadingArtifact);
        var justBefore = At(10) + Lease - TimeSpan.FromMilliseconds(1);

        await store.ExpireStaleAsync(justBefore, default);
        Assert.Equal(RestoreStatus.Running, (await _world.OperationAsync(operation.Id)).Status);
        Assert.True(await store.ExpireStaleAsync(At(10) + Lease, default) >= 1);

        var lost = await _world.OperationAsync(operation.Id);
        Assert.Equal((RestoreStatus.AgentLost, RestoreFailureCodes.AgentLost, RestoreTargetState.Unknown), (lost.Status, lost.FailureCode, lost.TargetState));
        Assert.Equal(At(10) + Lease, lost.CompletedAt);
        Assert.Equal((RestoreStatus.AgentLost, RestoreTargetState.Unknown), (lost.CurrentAttempt.Status, lost.CurrentAttempt.TargetState));
        Assert.False(lost.Pin.IsActive);
        await store.ExpireStaleAsync(At(10) + Lease + TimeSpan.FromHours(1), default);
        Assert.Equal(lost.CompletedAt, (await _world.OperationAsync(operation.Id)).CompletedAt);
        Assert.NotNull(claim);
    }

    [Fact]
    public async Task ALostLeasedRestoreThatNeverStartedLeftTheTargetUntouched()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var operation = await _world.CreateAsync(seed, store);
        await MustClaimAsync(store, seed.Agent, At(10));

        Assert.True(await store.ExpireStaleAsync(At(10) + Lease, default) >= 1);

        var lost = await _world.OperationAsync(operation.Id);
        Assert.Equal((RestoreStatus.AgentLost, RestoreTargetState.NotModified), (lost.Status, lost.TargetState));
        Assert.False(lost.Pin.IsActive);
    }

    [Fact]
    public async Task ARenewedLeaseIsNotExpired()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var operation = await _world.CreateAsync(seed, store);
        var claim = await RunToAsync(store, seed.Agent, At(10));
        await store.RenewLeaseAsync(Command(claim, At(100)), Lease, default);

        await store.ExpireStaleAsync(At(10) + Lease + TimeSpan.FromSeconds(30), default);
        Assert.Equal(RestoreStatus.Running, (await _world.OperationAsync(operation.Id)).Status);
    }

    [Fact]
    public async Task AClaimEndsTheAgentsExpiredLeaseFirstAndHandsOutTheNextRestore()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var lost = await _world.CreateAsync(seed, store, At(0));
        var next = await _world.CreateAsync(seed, store, At(1));
        await RunToAsync(store, seed.Agent, At(10), RestoreStages.Restoring);

        var claim = await ClaimAsync(store, seed.Agent, At(10) + Lease + TimeSpan.FromSeconds(1));

        Assert.Equal(next.Id, claim!.Operation.Id);
        var ended = await _world.OperationAsync(lost.Id);
        Assert.Equal((RestoreStatus.AgentLost, RestoreTargetState.Unknown), (ended.Status, ended.TargetState));
        Assert.False(ended.Pin.IsActive);
    }

    [Fact]
    public async Task ARestoreNobodyClaimedIsWithdrawnAfterTheMaximumWaitAndReleasesThePin()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var operation = await _world.CreateAsync(seed, store, At(0));

        // The sweeps are global (they also end other tests' leftovers), so what is asserted is this restore, not how many the sweep touched.
        await store.ExpireUnclaimedAsync(At(0) + Unclaimed - TimeSpan.FromMilliseconds(1), Unclaimed, default);
        Assert.Equal(RestoreStatus.Pending, (await _world.OperationAsync(operation.Id)).Status);
        Assert.True(await store.ExpireUnclaimedAsync(At(0) + Unclaimed, Unclaimed, default) >= 1);

        var withdrawn = await _world.OperationAsync(operation.Id);
        Assert.Equal((RestoreStatus.Cancelled, RestoreFailureCodes.NotClaimed, RestoreTargetState.NotModified), (withdrawn.Status, withdrawn.FailureCode, withdrawn.TargetState));
        Assert.Equal(RestoreStatus.Cancelled, withdrawn.CurrentAttempt.Status);
        Assert.Null(withdrawn.CancellationRequestedAt);
        Assert.False(withdrawn.Pin.IsActive);
        await store.ExpireUnclaimedAsync(At(0) + Unclaimed * 2, Unclaimed, default);
        Assert.Equal(withdrawn.CompletedAt, (await _world.OperationAsync(operation.Id)).CompletedAt);
    }

    [Fact]
    public async Task AClaimedRestoreIsNeverWithdrawnAsUnclaimed()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var operation = await _world.CreateAsync(seed, store, At(0));
        await RunToAsync(store, seed.Agent, At(10));

        // The sweep is global (it also ends other tests' leftovers), so what is asserted is this restore.
        await store.ExpireUnclaimedAsync(At(0) + Unclaimed * 5, Unclaimed, default);
        Assert.Equal(RestoreStatus.Running, (await _world.OperationAsync(operation.Id)).Status);
    }

    [Fact]
    public async Task AClaimTooLateWithdrawsTheRestoreInsteadOfStartingItHoursAfterTheRequest()
    {
        var seed = await _world.SeedCentralAsync();
        var store = _world.Store();
        var operation = await _world.CreateAsync(seed, store, At(0));

        Assert.Null(await ClaimAsync(store, seed.Agent, At(0) + Unclaimed + TimeSpan.FromMinutes(1)));

        var withdrawn = await _world.OperationAsync(operation.Id);
        Assert.Equal((RestoreStatus.Cancelled, RestoreFailureCodes.NotClaimed), (withdrawn.Status, withdrawn.FailureCode));
        Assert.False(withdrawn.Pin.IsActive);
    }
}
