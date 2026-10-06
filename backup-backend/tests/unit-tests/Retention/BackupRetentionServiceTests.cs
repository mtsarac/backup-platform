using BackupPlatform.Application.Backups;
using BackupPlatform.StorageProviders.Abstractions;
using static BackupPlatform.UnitTests.Retention.RetentionFixtures;

namespace BackupPlatform.UnitTests.Retention;

/// <summary>The physical-delete side of retention: ordering, failure recording, idempotence, recovery and what is logged. No database, no file system.</summary>
public sealed class BackupRetentionServiceTests
{
    private const string SecretPath = "/srv/secret-share/finance";

    private sealed class Rig
    {
        public Rig(BackupRetentionOptions? options = null)
        {
            Options = options ?? new BackupRetentionOptions();
            Store = new FakeRetentionStore(Journal);
            Storage = new FakeStorage(Journal);
            Resolver = FakeResolver.Serving(Storage);
            Clock = new SettableClock(Now);
            Log = new CapturingLog<BackupRetentionService>();
            Service = new BackupRetentionService(Store, Resolver, Options, new BackupNodeIdentity("node-a"), Clock, Log);
        }

        public CallJournal Journal { get; } = new();
        public BackupRetentionOptions Options { get; }
        public FakeRetentionStore Store { get; }
        public FakeStorage Storage { get; }
        public FakeResolver Resolver { get; }
        public SettableClock Clock { get; }
        public CapturingLog<BackupRetentionService> Log { get; }
        public BackupRetentionService Service { get; }

        /// <summary>A due replica whose file exists in the fake storage.</summary>
        public ClaimedReplicaDeletion Due(int attempt = 1)
        {
            var key = KeyOf(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            Storage.AddObject(key, Now.AddDays(-40));
            var claim = Claim(key, attempt);
            Store.ClaimBatches.Enqueue([claim]);
            return claim;
        }
    }

    [Fact]
    public async Task DeletesTheObjectThenVerifiesAbsenceThenRecordsItDeleted()
    {
        var rig = new Rig();
        var claim = rig.Due();

        var result = await rig.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(["apply", "claim", $"delete:{claim.Key}", $"exists:{claim.Key}", $"complete:{claim.ReplicaId}"], rig.Journal.Entries);
        Assert.False(rig.Storage.Has(claim.Key));
        var completion = Assert.Single(rig.Store.Completions);
        Assert.Equal(claim.LeaseToken, completion.Token);
        Assert.Equal(Now, completion.Now);
        Assert.Empty(rig.Store.Failures);
        Assert.Equal(new RetentionCycleResult(0, 0, 1, 1, 0, 0), result);
    }

    [Fact]
    public async Task ClaimsAsThisNodeWithTheConfiguredLeaseAndBatchSize()
    {
        var rig = new Rig(new BackupRetentionOptions { BatchSize = 7, DeleteLeaseDuration = TimeSpan.FromMinutes(3) });

        await rig.Service.RunCycleAsync(CancellationToken.None);

        var call = Assert.Single(rig.Store.ClaimCalls);
        Assert.Equal(("node-a", Now, TimeSpan.FromMinutes(3), 7), call);
        Assert.Equal((Now, 7), Assert.Single(rig.Store.ApplyCalls));
    }

    [Fact]
    public async Task AFileThatIsAlreadyGoneCountsAsDeleted()
    {
        var rig = new Rig();
        var claim = Claim(KeyOf(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        rig.Store.ClaimBatches.Enqueue([claim]);

        var result = await rig.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(claim.ReplicaId, Assert.Single(rig.Store.Completions).ReplicaId);
        Assert.Empty(rig.Store.Failures);
        Assert.Equal(1, result.Deleted);
    }

    [Fact]
    public async Task AFailedDeleteIsRecordedWithItsCodeAndTheBackoffOfItsAttemptAndNeverCompleted()
    {
        var rig = new Rig();
        var claim = rig.Due(attempt: 3);
        rig.Storage.DeleteFault = _ => new StorageException(StorageFailureKind.AccessDenied, "denied");

        var result = await rig.Service.RunCycleAsync(CancellationToken.None);

        var failure = Assert.Single(rig.Store.Failures);
        Assert.Equal((claim.ReplicaId, claim.LeaseToken, "access_denied"), (failure.ReplicaId, failure.Token, failure.Code));
        Assert.Equal(Now, failure.Now);
        // Attempt 3 waits initial * 2^2: one minute by default.
        Assert.Equal(Now.AddMinutes(4), failure.NextAttemptAt);
        Assert.Equal(Now + rig.Options.DeleteRetryDelay(3), failure.NextAttemptAt);
        Assert.Empty(rig.Store.Completions);
        Assert.True(rig.Storage.Has(claim.Key));
        Assert.Equal(new RetentionCycleResult(0, 0, 1, 0, 1, 0), result);
    }

    [Theory]
    [InlineData(StorageFailureKind.AccessDenied, "access_denied")]
    [InlineData(StorageFailureKind.InvalidConfiguration, "storage_unavailable")]
    [InlineData(StorageFailureKind.NotFound, "storage_unavailable")]
    [InlineData(StorageFailureKind.Io, "io_error")]
    [InlineData(StorageFailureKind.InsufficientSpace, "io_error")]
    [InlineData(StorageFailureKind.NotSupported, "delete_not_supported")]
    [InlineData(StorageFailureKind.Unexpected, "unexpected")]
    [InlineData(StorageFailureKind.IntegrityMismatch, "unexpected")]
    public async Task StorageFailureKindsMapToStableCodes(StorageFailureKind kind, string code)
    {
        var rig = new Rig();
        rig.Due();
        rig.Storage.DeleteFault = _ => new StorageException(kind, "provider message");

        await rig.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(code, Assert.Single(rig.Store.Failures).Code);
    }

    [Fact]
    public async Task AnyOtherProviderExceptionIsRecordedAsUnexpectedInsteadOfLeavingTheReplicaClaimed()
    {
        var rig = new Rig();
        rig.Due();
        rig.Storage.DeleteFault = _ => new IOException($"cannot open {SecretPath}");

        await rig.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal("unexpected", Assert.Single(rig.Store.Failures).Code);
    }

    [Fact]
    public async Task AFailureOnOneReplicaNeverStopsTheNextOne()
    {
        var rig = new Rig();
        var failing = Claim(KeyOf(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        var healthy = Claim(KeyOf(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        rig.Storage.AddObject(failing.Key, Now.AddDays(-40)).AddObject(healthy.Key, Now.AddDays(-40));
        rig.Storage.DeleteFault = key => key.Value == failing.Key ? new StorageException(StorageFailureKind.Io, "io") : null;
        rig.Store.ClaimBatches.Enqueue([failing, healthy]);

        var result = await rig.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(new RetentionCycleResult(0, 0, 2, 1, 1, 0), result);
        Assert.False(rig.Storage.Has(healthy.Key));
        Assert.True(rig.Storage.Has(failing.Key));
    }

    [Fact]
    public async Task AnUnconfiguredStorageFailsWithoutTouchingAnyProvider()
    {
        var rig = new Rig();
        var claim = rig.Due();
        rig.Resolver.OnResolve = (_, _, _) => null;

        await rig.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal("storage_not_configured", Assert.Single(rig.Store.Failures).Code);
        Assert.DoesNotContain(rig.Journal.Entries, entry => entry.StartsWith("delete:", StringComparison.Ordinal));
        Assert.True(rig.Storage.Has(claim.Key));
    }

    [Fact]
    public async Task ResolvesThePersistedLocationOfTheReplicaForItsTenant()
    {
        var rig = new Rig();
        var claim = Claim(KeyOf(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), provider: "central", target: "vault");
        rig.Store.ClaimBatches.Enqueue([claim]);

        await rig.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal((claim.TenantId, "central", "vault"), Assert.Single(rig.Resolver.Resolutions));
    }

    [Fact]
    public async Task AProviderThatCannotDeleteFailsWithoutBeingAsked()
    {
        var rig = new Rig();
        rig.Due();
        rig.Storage.Capabilities = new StorageCapabilities([StorageCapabilityIds.Enumerate]);

        await rig.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal("delete_not_supported", Assert.Single(rig.Store.Failures).Code);
        Assert.DoesNotContain(rig.Journal.Entries, entry => entry.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../outside.bak")]
    [InlineData("/etc/passwd")]
    [InlineData("a\\b.bak")]
    [InlineData("")]
    public async Task APersistedKeyThatIsNotAValidStorageKeyIsNeverPassedToTheProvider(string key)
    {
        var rig = new Rig();
        rig.Store.ClaimBatches.Enqueue([Claim(key)]);

        await rig.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal("invalid_location", Assert.Single(rig.Store.Failures).Code);
        Assert.DoesNotContain(rig.Journal.Entries, entry => entry.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnObjectThatIsStillThereAfterTheDeleteIsNeverRecordedDeleted()
    {
        var rig = new Rig();
        var claim = rig.Due();
        rig.Storage.KeepAfterDelete.Add(claim.Key);

        var result = await rig.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal("delete_not_verified", Assert.Single(rig.Store.Failures).Code);
        Assert.Empty(rig.Store.Completions);
        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public async Task AbsenceIsOnlyCheckedOnProvidersThatCanProveIt()
    {
        var rig = new Rig();
        var claim = rig.Due();
        rig.Storage.Capabilities = new StorageCapabilities([StorageCapabilityIds.Delete]);

        await rig.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(["apply", "claim", $"delete:{claim.Key}", $"complete:{claim.ReplicaId}"], rig.Journal.Entries);
    }

    [Fact]
    public async Task AFailedDatabaseCommitNeitherFailsTheReplicaNorStopsTheCycle()
    {
        var rig = new Rig();
        var first = Claim(KeyOf(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        var second = Claim(KeyOf(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        rig.Storage.AddObject(first.Key, Now.AddDays(-40)).AddObject(second.Key, Now.AddDays(-40));
        rig.Store.CompleteFault = id => id == first.ReplicaId ? new InvalidOperationException($"connection to {SecretPath} lost") : null;
        rig.Store.ClaimBatches.Enqueue([first, second]);

        var result = await rig.Service.RunCycleAsync(CancellationToken.None);

        // The file of the first replica is gone but the row is not Deleted: its lease expires and it is claimed again.
        Assert.False(rig.Storage.Has(first.Key));
        Assert.Empty(rig.Store.Failures);
        Assert.Equal(second.ReplicaId, Assert.Single(rig.Store.Completions).ReplicaId);
        Assert.Equal(new RetentionCycleResult(0, 0, 2, 1, 0, 0), result);
        Assert.DoesNotContain(SecretPath, rig.Log.AllText);
    }

    [Fact]
    public async Task ADeleteThatHappenedButWasNotRecordedCompletesOnTheNextClaimBecauseTheFileIsAlreadyAbsent()
    {
        var rig = new Rig();
        var claim = rig.Due();
        var failOnce = true;
        rig.Store.CompleteFault = _ =>
        {
            if (!failOnce) return null;
            failOnce = false;
            return new InvalidOperationException("database unavailable");
        };

        var first = await rig.Service.RunCycleAsync(CancellationToken.None);
        Assert.Equal(0, first.Deleted);
        Assert.False(rig.Storage.Has(claim.Key));

        // The lease ran out; the same replica is handed out again with its next attempt number.
        rig.Store.ClaimBatches.Enqueue([claim with { LeaseToken = Guid.NewGuid(), Attempt = 2 }]);
        var second = await rig.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, second.Deleted);
        Assert.Equal(claim.ReplicaId, Assert.Single(rig.Store.Completions).ReplicaId);
        Assert.Empty(rig.Store.Failures);
    }

    [Fact]
    public async Task ALostLeaseIsCountedAndNotAFailure()
    {
        var rig = new Rig();
        rig.Due();
        rig.Store.CompleteResult = _ => false;

        var result = await rig.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(new RetentionCycleResult(0, 0, 1, 0, 0, 1), result);
        Assert.Empty(rig.Store.Failures);
    }

    [Fact]
    public async Task AFailureThatCannotBeRecordedBecauseTheLeaseWasTakenOverCountsAsLeaseLost()
    {
        var rig = new Rig();
        rig.Due();
        rig.Storage.DeleteFault = _ => new StorageException(StorageFailureKind.Io, "io");
        rig.Store.FailResult = _ => false;

        var result = await rig.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(new RetentionCycleResult(0, 0, 1, 0, 0, 1), result);
    }

    [Fact]
    public async Task CancellationStopsBetweenReplicasAndLeavesTheRestToTheirLeases()
    {
        var rig = new Rig();
        using var source = new CancellationTokenSource();
        var first = Claim(KeyOf(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        var second = Claim(KeyOf(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        rig.Storage.AddObject(first.Key, Now.AddDays(-40)).AddObject(second.Key, Now.AddDays(-40));
        rig.Storage.OnDelete = _ => source.Cancel();
        rig.Store.ClaimBatches.Enqueue([first, second]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Service.RunCycleAsync(source.Token));

        Assert.Equal(first.ReplicaId, Assert.Single(rig.Store.Completions).ReplicaId);
        Assert.True(rig.Storage.Has(second.Key));
        Assert.Empty(rig.Store.Failures);
    }

    [Fact]
    public async Task ACancelledProviderCallIsNotRecordedAsAFailedDelete()
    {
        var rig = new Rig();
        rig.Due();
        rig.Storage.DeleteFault = _ => new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Service.RunCycleAsync(CancellationToken.None));

        Assert.Empty(rig.Store.Failures);
        Assert.Empty(rig.Store.Completions);
    }

    [Fact]
    public async Task ACycleCancelledBeforeItStartsDoesNothing()
    {
        var rig = new Rig();
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Service.RunCycleAsync(source.Token));

        Assert.Empty(rig.Journal.Entries);
    }

    [Fact]
    public async Task TimeIsReadAgainForEveryStepSoARetryIsCountedFromWhenTheFailureHappened()
    {
        var rig = new Rig();
        rig.Due();
        rig.Storage.OnDelete = _ => rig.Clock.Advance(TimeSpan.FromMinutes(10));
        rig.Storage.DeleteFault = _ => new StorageException(StorageFailureKind.Io, "io");

        await rig.Service.RunCycleAsync(CancellationToken.None);

        var failure = Assert.Single(rig.Store.Failures);
        Assert.Equal(Now.AddMinutes(10), failure.Now);
        Assert.Equal(failure.Now + rig.Options.DeleteRetryDelay(1), failure.NextAttemptAt);
    }

    [Fact]
    public async Task TheDeleteIsRecordedAtTheTimeItFinished()
    {
        var rig = new Rig();
        rig.Due();
        rig.Storage.OnDelete = _ => rig.Clock.Advance(TimeSpan.FromMinutes(10));

        await rig.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(Now.AddMinutes(10), Assert.Single(rig.Store.Completions).Now);
    }

    [Fact]
    public async Task LogsNameTheReplicaAttemptAndCodeButNeverTheKeyTargetOrProviderMessage()
    {
        var rig = new Rig();
        var key = KeyOf(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), prefix: "vault-prefix");
        var claim = Claim(key, attempt: 2, target: "finance-share");
        rig.Storage.AddObject(key, Now.AddDays(-40));
        rig.Store.ClaimBatches.Enqueue([claim]);
        rig.Storage.DeleteFault = _ => new StorageException(StorageFailureKind.AccessDenied, $"denied for {SecretPath}", new UnauthorizedAccessException(SecretPath));

        await rig.Service.RunCycleAsync(CancellationToken.None);

        var text = rig.Log.AllText;
        Assert.Contains(claim.ReplicaId.ToString(), text);
        Assert.Contains("access_denied", text);
        Assert.Contains("attempt 2", text);
        Assert.DoesNotContain(key, text);
        Assert.DoesNotContain(".bak", text);
        Assert.DoesNotContain("vault-prefix", text);
        Assert.DoesNotContain("finance-share", text);
        Assert.DoesNotContain(SecretPath, text);
        Assert.DoesNotContain("denied for", text);
    }

    [Fact]
    public async Task LogsOfASuccessfulDeleteAndALostLeaseNeverContainTheKeyEither()
    {
        var rig = new Rig();
        var claim = rig.Due();
        rig.Store.CompleteResult = _ => false;

        await rig.Service.RunCycleAsync(CancellationToken.None);

        Assert.DoesNotContain(claim.Key, rig.Log.AllText);
        Assert.DoesNotContain(".bak", rig.Log.AllText);
    }

    [Fact]
    public async Task ExpiryRepeatsWhileBatchesAreFullButNeverBeyondTheCycleBound()
    {
        var rig = new Rig(new BackupRetentionOptions { BatchSize = 2, MaxBatchesPerCycle = 3 });
        for (var i = 0; i < 5; i++) rig.Store.Applications.Enqueue(new BackupRetentionApplication(2, i == 1 ? 1 : 2));

        var result = await rig.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(3, rig.Store.ApplyCalls.Count);
        Assert.Equal(6, result.ArtifactsExamined);
        Assert.Equal(5, result.ReplicasMarkedForDeletion);
    }

    [Fact]
    public async Task ExpiryStopsAtTheFirstBatchThatIsNotFull()
    {
        var rig = new Rig(new BackupRetentionOptions { BatchSize = 2, MaxBatchesPerCycle = 10 });
        rig.Store.Applications.Enqueue(new BackupRetentionApplication(2, 2));
        rig.Store.Applications.Enqueue(new BackupRetentionApplication(1, 1));
        rig.Store.Applications.Enqueue(new BackupRetentionApplication(2, 2));

        var result = await rig.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(2, rig.Store.ApplyCalls.Count);
        Assert.Equal(new RetentionCycleResult(3, 3, 0, 0, 0, 0), result);
    }

    [Fact]
    public async Task ClaimsRepeatWhileBatchesAreFullButNeverBeyondTheCycleBound()
    {
        var rig = new Rig(new BackupRetentionOptions { BatchSize = 2, MaxBatchesPerCycle = 3 });
        for (var batch = 0; batch < 4; batch++) rig.Store.ClaimBatches.Enqueue([Claim("a.bak"), Claim("b.bak")]);

        var result = await rig.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(3, rig.Store.ClaimCalls.Count);
        Assert.Equal(6, result.DeletionsClaimed);
    }

    [Fact]
    public async Task ClaimsStopAtTheFirstBatchThatIsNotFull()
    {
        var rig = new Rig(new BackupRetentionOptions { BatchSize = 2, MaxBatchesPerCycle = 10 });
        rig.Store.ClaimBatches.Enqueue([Claim("a.bak"), Claim("b.bak")]);
        rig.Store.ClaimBatches.Enqueue([Claim("c.bak")]);
        rig.Store.ClaimBatches.Enqueue([Claim("d.bak"), Claim("e.bak")]);

        var result = await rig.Service.RunCycleAsync(CancellationToken.None);

        Assert.Equal(2, rig.Store.ClaimCalls.Count);
        Assert.Equal(3, result.DeletionsClaimed);
    }

    [Fact]
    public async Task OccurrencePruningUsesTheConfiguredHistoryAndRepeatsWhileBatchesAreFull()
    {
        var rig = new Rig(new BackupRetentionOptions
        {
            ScheduleOccurrenceHistoryRetention = TimeSpan.FromDays(10), ScheduleOccurrencePruneBatchSize = 3, MaxBatchesPerCycle = 3
        });
        for (var i = 0; i < 5; i++) rig.Store.PruneResults.Enqueue(3);

        var removed = await rig.Service.PruneScheduleOccurrencesAsync(CancellationToken.None);

        Assert.Equal(9, removed);
        Assert.Equal(3, rig.Store.PruneCalls.Count);
        Assert.All(rig.Store.PruneCalls, call => Assert.Equal((Now.AddDays(-10), 3), call));
    }

    [Fact]
    public async Task OccurrencePruningStopsAtThePartialBatch()
    {
        var rig = new Rig(new BackupRetentionOptions { ScheduleOccurrencePruneBatchSize = 3, MaxBatchesPerCycle = 10 });
        rig.Store.PruneResults.Enqueue(3);
        rig.Store.PruneResults.Enqueue(1);
        rig.Store.PruneResults.Enqueue(3);

        var removed = await rig.Service.PruneScheduleOccurrencesAsync(CancellationToken.None);

        Assert.Equal(4, removed);
        Assert.Equal(2, rig.Store.PruneCalls.Count);
    }

    [Fact]
    public async Task OccurrencePruningKeepsOneCutoffForTheWholePassEvenAsTimePasses()
    {
        var rig = new Rig(new BackupRetentionOptions { ScheduleOccurrencePruneBatchSize = 1, MaxBatchesPerCycle = 3 });
        rig.Store.PruneResults.Enqueue(1);
        rig.Store.PruneResults.Enqueue(1);
        rig.Store.PruneResults.Enqueue(0);

        await rig.Service.PruneScheduleOccurrencesAsync(CancellationToken.None);

        Assert.Single(rig.Store.PruneCalls.Select(call => call.DecidedBefore).Distinct());
    }

    [Fact]
    public async Task NodeIdentityIsWhatTheLeaseIsTakenUnder()
    {
        var rig = new Rig();
        var service = new BackupRetentionService(rig.Store, rig.Resolver, rig.Options, new BackupNodeIdentity("node-b"), rig.Clock, rig.Log);

        await service.RunCycleAsync(CancellationToken.None);

        Assert.Equal("node-b", Assert.Single(rig.Store.ClaimCalls).Owner);
    }
}
