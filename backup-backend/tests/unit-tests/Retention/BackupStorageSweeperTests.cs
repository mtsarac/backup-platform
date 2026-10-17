using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.StorageProviders.Abstractions;
using static BackupPlatform.UnitTests.Retention.RetentionFixtures;

namespace BackupPlatform.UnitTests.Retention;

/// <summary>
/// The orphan and unpublished-upload sweep against an in-memory storage: every rule that keeps a file, and the few that let one go. Nothing here
/// deletes a file unless every condition holds, so most tests assert that a file is still there.
/// </summary>
public sealed class BackupStorageSweeperTests
{
    private static readonly TimeSpan Old = TimeSpan.FromDays(2);
    private const string SecretPath = "/srv/secret-share/finance";

    private sealed record Attempt(Guid Tenant, Guid Job, Guid Run, Guid Id);

    private sealed class Rig
    {
        public Rig(BackupRetentionOptions? options = null, TenantId? scope = null, StorageProviderId? provider = null)
        {
            Options = options ?? new BackupRetentionOptions();
            Store = new FakeRetentionStore(Journal);
            Storage = new FakeStorage(Journal) { Id = provider ?? StorageProviderId.Local };
            Resolver.Storages.Add(new BackupManagedStorage("main", Storage.Id, scope, Storage));
            Clock = new SettableClock(Now);
            Log = new CapturingLog<BackupStorageSweeper>();
            Sweeper = new BackupStorageSweeper(Store, Resolver, Options, Clock, Log);
        }

        public CallJournal Journal { get; } = new();
        public BackupRetentionOptions Options { get; }
        public FakeRetentionStore Store { get; }
        public FakeStorage Storage { get; }
        public FakeResolver Resolver { get; } = new();
        public SettableClock Clock { get; }
        public CapturingLog<BackupStorageSweeper> Log { get; }
        public BackupStorageSweeper Sweeper { get; }

        public Task<StorageSweepReport> SweepAsync(CancellationToken cancellationToken = default) => Sweeper.SweepAsync(cancellationToken);

        /// <summary>An attempt the database knows, whose run has <paramref name="run"/> status.</summary>
        public Attempt Register(BackupStatus run, BackupStatus? attempt = null, Guid? tenant = null)
        {
            var created = new Attempt(tenant ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            Store.Attempts[created.Id] = Activity(created.Tenant, created.Job, created.Run, created.Id, run, attempt);
            return created;
        }

        public static string KeyFor(Attempt attempt, string? name = null, string? prefix = null) =>
            KeyOf(attempt.Tenant, attempt.Job, attempt.Run, attempt.Id, name, prefix);

        public string Object(Attempt attempt, TimeSpan age, string? name = null, string? prefix = null)
        {
            var key = KeyFor(attempt, name, prefix);
            Storage.AddObject(key, Now - age);
            return key;
        }

        public string Partial(Attempt attempt, TimeSpan age, bool? inUse = false)
        {
            var key = KeyFor(attempt, PartialName);
            Storage.AddPartial(key, Now - age, inUse);
            return key;
        }
    }

    // ---- what is deleted ----

    [Theory]
    [InlineData(BackupStatus.Succeeded)]
    [InlineData(BackupStatus.Failed)]
    [InlineData(BackupStatus.Cancelled)]
    [InlineData(BackupStatus.AgentLost)]
    public async Task AnOldUnreferencedFileOfAFinishedRunIsDeleted(BackupStatus run)
    {
        var rig = new Rig();
        var key = rig.Object(rig.Register(run), Old);

        var report = await rig.SweepAsync();

        Assert.False(rig.Storage.Has(key));
        Assert.Equal(new StorageSweepReport(1, 0, 0, 0, 0, 1, 0, 0, 0, false), report);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyVerifiedAbsenceCanPublishALeftoverRemoval(bool cannotVerify)
    {
        var rig = new Rig();
        var key = rig.Object(rig.Register(BackupStatus.Failed), Old);
        if (cannotVerify)
            rig.Storage.Capabilities = new StorageCapabilities([StorageCapabilityIds.Delete, StorageCapabilityIds.Enumerate]);
        else
            rig.Storage.KeepAfterDelete.Add(key);

        var report = await rig.SweepAsync();

        Assert.True(rig.Storage.Has(key));
        Assert.Equal(1, report.Failed);
        Assert.Equal(0, report.OrphansDeleted);
        Assert.Empty(rig.Store.CleanupCompletions);
    }

    [Fact]
    public async Task AVerifiedRemovalCarriesTheRealAttemptAndIsNotRepeatedAfterTheFileIsGone()
    {
        var rig = new Rig();
        var attempt = rig.Register(BackupStatus.Failed);
        var key = rig.Object(attempt, Old);

        await rig.SweepAsync();
        await rig.SweepAsync();

        var removal = Assert.Single(rig.Store.SweptDeletions);
        Assert.Equal(attempt.Tenant, removal.Key.TenantId);
        Assert.Equal(attempt.Id, removal.Key.AttemptId);
        Assert.Equal(key, removal.ObjectKey);
        Assert.Equal(Now, removal.Now);
    }

    [Fact]
    public async Task AKeyBelowAConfiguredPrefixIsRecognisedToo()
    {
        var rig = new Rig();
        var key = rig.Object(rig.Register(BackupStatus.Failed), Old, prefix: "vault/finance");

        var report = await rig.SweepAsync();

        Assert.False(rig.Storage.Has(key));
        Assert.Equal(1, report.OrphansDeleted);
    }

    [Fact]
    public async Task OnlyTheOrphanIsDeletedWhenItsNeighboursAreKept()
    {
        var rig = new Rig();
        var finished = rig.Register(BackupStatus.Failed);
        var orphan = rig.Object(finished, Old, name: "Sales_20261001T000000Z_aaaaaaaa_FULL.bak");
        var referenced = rig.Object(finished, Old, name: "Sales_20261002T000000Z_bbbbbbbb_FULL.bak");
        var young = rig.Object(finished, TimeSpan.FromMinutes(5), name: "Sales_20261003T000000Z_cccccccc_FULL.bak");
        rig.Storage.AddObject("user/notes.txt", Now - Old);
        rig.Store.References.Add(("local", referenced));
        rig.Storage.HonourModifiedBefore = false;

        var report = await rig.SweepAsync();

        Assert.False(rig.Storage.Has(orphan));
        Assert.True(rig.Storage.Has(referenced));
        Assert.True(rig.Storage.Has(young));
        Assert.True(rig.Storage.Has("user/notes.txt"));
        Assert.Equal(new StorageSweepReport(4, 1, 1, 1, 0, 1, 0, 0, 0, false), report);
    }

    // ---- what is kept ----

    [Fact]
    public async Task AFreshFileIsKeptEvenIfItLooksLikeAnOrphan()
    {
        var rig = new Rig { Storage = { HonourModifiedBefore = false } };
        var key = rig.Object(rig.Register(BackupStatus.Failed), TimeSpan.FromHours(1));

        var report = await rig.SweepAsync();

        Assert.True(rig.Storage.Has(key));
        Assert.Equal(1, report.KeptYoung);
        Assert.Equal(0, report.OrphansDeleted);
        Assert.Empty(rig.Store.ReferenceQueries);
    }

    [Fact]
    public async Task TheAgeBoundaryIsExclusiveOnTheYoungSide()
    {
        var rig = new Rig(new BackupRetentionOptions { OrphanMinimumAge = TimeSpan.FromHours(6) }) { Storage = { HonourModifiedBefore = false } };
        var attempt = rig.Register(BackupStatus.Failed);
        var exactly = rig.Object(attempt, TimeSpan.FromHours(6), name: "Sales_20261001T000000Z_aaaaaaaa_FULL.bak");
        var older = rig.Object(attempt, TimeSpan.FromHours(6) + TimeSpan.FromSeconds(1), name: "Sales_20261002T000000Z_bbbbbbbb_FULL.bak");

        await rig.SweepAsync();

        Assert.True(rig.Storage.Has(exactly));
        Assert.False(rig.Storage.Has(older));
    }

    [Fact]
    public async Task TheListingIsAskedOnlyForFilesOlderThanTheSmallerOfTheTwoMinimumAges()
    {
        var rig = new Rig(new BackupRetentionOptions { OrphanMinimumAge = TimeSpan.FromHours(6), PartialUploadMinimumAge = TimeSpan.FromHours(2) });

        await rig.SweepAsync();

        Assert.Equal(Now.AddHours(-2), Assert.Single(rig.Storage.ModifiedBeforeValues));
    }

    [Fact]
    public async Task AFileAReplicaReferencesIsNeverDeleted()
    {
        var rig = new Rig();
        var key = rig.Object(rig.Register(BackupStatus.Succeeded), Old);
        rig.Store.References.Add(("local", key));

        var report = await rig.SweepAsync();

        Assert.True(rig.Storage.Has(key));
        Assert.Equal(1, report.KeptReferenced);
        Assert.Equal(0, report.OrphansDeleted);
    }

    [Fact]
    public async Task RepairFilesOfATerminalBackupStayPinnedUntilRepairEnds()
    {
        var rig = new Rig();
        var attempt = rig.Register(BackupStatus.Succeeded);
        var name = ManagedBackupLayout.RepairFileName(Guid.NewGuid(), Guid.NewGuid());
        var target = rig.Object(attempt, Old, name);
        var partial = Rig.KeyFor(attempt, $".{name}.partial");
        rig.Storage.AddPartial(partial, Now - Old, false);
        rig.Store.Attempts[attempt.Id] = rig.Store.Attempts[attempt.Id] with { RepairIsActive = true };

        await rig.SweepAsync();
        Assert.True(rig.Storage.Has(target));
        Assert.True(rig.Storage.Has(partial));

        rig.Store.Attempts[attempt.Id] = rig.Store.Attempts[attempt.Id] with { RepairIsActive = false };
        await rig.SweepAsync();
        Assert.False(rig.Storage.Has(target));
        Assert.False(rig.Storage.Has(partial));
    }

    [Fact]
    public async Task ReferencesAreLookedUpUnderTheProviderIdOfTheSweptStorage()
    {
        var rig = new Rig(provider: StorageProviderId.Central);
        var key = rig.Object(rig.Register(BackupStatus.Succeeded), Old);
        rig.Store.References.Add(("central", key));

        var report = await rig.SweepAsync();

        Assert.True(rig.Storage.Has(key));
        Assert.Equal(1, report.KeptReferenced);
        Assert.All(rig.Store.ReferenceQueries, query => Assert.Equal("central", query.Provider));
    }

    [Fact]
    public async Task AReferenceThatAppearsAfterTheBatchCheckStillProtectsTheFile()
    {
        var rig = new Rig();
        var key = rig.Object(rig.Register(BackupStatus.Failed), Old);
        // A completion is accepted between the batch read and the delete: the re-check right before the delete must see it.
        rig.Store.AfterReferenceQuery = number =>
        {
            if (number == 1) rig.Store.References.Add(("local", key));
        };

        var report = await rig.SweepAsync();

        Assert.True(rig.Storage.Has(key));
        Assert.Equal(new StorageSweepReport(1, 0, 1, 0, 0, 0, 0, 0, 0, false), report);
        Assert.Equal([key], rig.Store.ReferenceQueries[^1].Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFileOrUploadOfAnAttemptTheDatabaseDoesNotKnowIsNotOurs(bool upload)
    {
        var rig = new Rig();
        var stranger = new Attempt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var key = upload ? rig.Partial(stranger, Old) : rig.Object(stranger, Old);

        var report = await rig.SweepAsync();

        Assert.True(rig.Storage.Has(key));
        Assert.Equal(1, report.Unrecognized);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("job")]
    [InlineData("run")]
    public async Task AKeyThatDisagreesWithTheDatabaseAboutItsAttemptIsNotOurs(string wrong)
    {
        var rig = new Rig();
        var real = rig.Register(BackupStatus.Failed);
        // Same attempt id, but the key claims another tenant, job or run than the row says.
        var claimed = wrong switch
        {
            "tenant" => real with { Tenant = Guid.NewGuid() },
            "job" => real with { Job = Guid.NewGuid() },
            _ => real with { Run = Guid.NewGuid() }
        };
        var key = rig.Object(claimed, Old);

        var report = await rig.SweepAsync();

        Assert.True(rig.Storage.Has(key));
        Assert.Equal(1, report.Unrecognized);
        Assert.Equal(0, report.OrphansDeleted);
    }

    [Theory]
    [InlineData(BackupStatus.Pending, BackupStatus.Pending)]
    [InlineData(BackupStatus.Leased, BackupStatus.Leased)]
    [InlineData(BackupStatus.Running, BackupStatus.Running)]
    [InlineData(BackupStatus.RetryPending, BackupStatus.Failed)]
    [InlineData(BackupStatus.RetryPending, BackupStatus.AgentLost)]
    public async Task AFileOfARunThatIsStillInFlightIsKeptEvenWhenItsAttemptHasEnded(BackupStatus run, BackupStatus attempt)
    {
        var rig = new Rig();
        var key = rig.Object(rig.Register(run, attempt), Old);

        var report = await rig.SweepAsync();

        Assert.True(rig.Storage.Has(key));
        Assert.Equal(1, report.KeptActive);
        Assert.Equal(0, report.OrphansDeleted);
    }

    [Fact]
    public async Task ATenantScopedStorageNeverTouchesAnotherTenantsKey()
    {
        var mine = TenantId.New();
        var rig = new Rig(scope: mine);
        var own = rig.Object(rig.Register(BackupStatus.Failed, tenant: mine.Value), Old);
        var foreign = rig.Object(rig.Register(BackupStatus.Failed), Old);

        var report = await rig.SweepAsync();

        Assert.False(rig.Storage.Has(own));
        Assert.True(rig.Storage.Has(foreign));
        Assert.Equal(1, report.Unrecognized);
        Assert.Equal(1, report.OrphansDeleted);
        Assert.DoesNotContain(rig.Store.ReferenceQueries, query => query.Keys.Contains(foreign));
    }

    [Fact]
    public async Task ASharedStorageSweepsEveryTenantsOrphans()
    {
        var rig = new Rig();
        var first = rig.Object(rig.Register(BackupStatus.Failed), Old);
        var second = rig.Object(rig.Register(BackupStatus.Cancelled), Old);

        var report = await rig.SweepAsync();

        Assert.False(rig.Storage.Has(first));
        Assert.False(rig.Storage.Has(second));
        Assert.Equal(2, report.OrphansDeleted);
    }

    // ---- unpublished uploads ----

    [Theory]
    [InlineData(false)]
    [InlineData(null)] // nobody can tell whether a writer holds it: still deleted once old enough
    public async Task AnOldUnpublishedUploadOfADeadAttemptNoWriterIsKnownToHoldIsDeleted(bool? inUse)
    {
        var rig = new Rig();
        var key = rig.Partial(rig.Register(BackupStatus.Failed), Old, inUse);

        var report = await rig.SweepAsync();

        Assert.False(rig.Storage.Has(key));
        Assert.Equal(new StorageSweepReport(1, 0, 0, 0, 0, 0, 1, 0, 0, false), report);
        // A temporary file is never a replica: nothing is asked about references for it.
        Assert.Empty(rig.Store.ReferenceQueries);
    }

    [Fact]
    public async Task AnUploadAWriterStillHoldsOpenIsKeptHoweverOldItLooks()
    {
        var rig = new Rig();
        var key = rig.Partial(rig.Register(BackupStatus.Failed), Old, inUse: true);

        var report = await rig.SweepAsync();

        Assert.True(rig.Storage.Has(key));
        Assert.Equal(1, report.KeptActive);
        Assert.Equal(0, report.PartialUploadsDeleted);
    }

    [Fact]
    public async Task AFreshUploadIsKept()
    {
        var rig = new Rig { Storage = { HonourModifiedBefore = false } };
        var key = rig.Partial(rig.Register(BackupStatus.Failed), TimeSpan.FromMinutes(30), inUse: false);

        var report = await rig.SweepAsync();

        Assert.True(rig.Storage.Has(key));
        Assert.Equal(1, report.KeptYoung);
    }

    [Theory]
    [InlineData(BackupStatus.Pending, BackupStatus.Pending)]
    [InlineData(BackupStatus.Leased, BackupStatus.Leased)]
    [InlineData(BackupStatus.Running, BackupStatus.Running)]
    // The run is still in flight (RetryPending), so nothing of it is cleaned up until it ends, even a leftover of the attempt that already failed.
    [InlineData(BackupStatus.RetryPending, BackupStatus.Failed)]
    public async Task AnUploadOfALiveRunIsKept(BackupStatus run, BackupStatus attempt)
    {
        var rig = new Rig();
        var key = rig.Partial(rig.Register(run, attempt), Old, inUse: false);

        var report = await rig.SweepAsync();

        Assert.True(rig.Storage.Has(key));
        Assert.Equal(0, report.PartialUploadsDeleted);
        Assert.Equal(1, report.KeptActive);
    }

    [Fact]
    public async Task EachKindUsesItsOwnMinimumAge()
    {
        var rig = new Rig(new BackupRetentionOptions { OrphanMinimumAge = TimeSpan.FromHours(6), PartialUploadMinimumAge = TimeSpan.FromHours(1) });
        var attempt = rig.Register(BackupStatus.Failed);
        var upload = rig.Partial(attempt, TimeSpan.FromHours(2), inUse: false);
        var backup = rig.Object(attempt, TimeSpan.FromHours(2));

        var report = await rig.SweepAsync();

        Assert.False(rig.Storage.Has(upload));
        Assert.True(rig.Storage.Has(backup));
        Assert.Equal(1, report.PartialUploadsDeleted);
        Assert.Equal(0, report.OrphansDeleted);
    }

    // ---- what is not ours ----

    [Fact]
    public async Task NamesOutsideTheManagedLayoutAreNeverTouchedNorQueried()
    {
        var rig = new Rig();
        var attempt = rig.Register(BackupStatus.Failed);
        var scope = ManagedBackupLayout.AttemptScope(attempt.Tenant, attempt.Job, attempt.Run, attempt.Id);
        string[] foreign =
        [
            "notes.txt",
            "backups/sales.bak",
            $"{attempt.Tenant:N}/{attempt.Job:N}/{attempt.Run:N}/{ManagedName}",
            $"{scope}/sales.bak",
            $"{scope}/{ManagedName}.old",
            $"{scope}/.hidden",
            $"{scope.ToUpperInvariant()}/{ManagedName}",
            $"{attempt.Tenant:D}/{attempt.Job:N}/{attempt.Run:N}/{attempt.Id:N}/{ManagedName}",
            $"{scope}/.{ManagedName}.not32hex.partial"
        ];
        foreach (var key in foreign) rig.Storage.AddObject(key, Now - Old);

        var report = await rig.SweepAsync();

        Assert.All(foreign, key => Assert.True(rig.Storage.Has(key), key));
        Assert.Equal(new StorageSweepReport(foreign.Length, foreign.Length, 0, 0, 0, 0, 0, 0, 0, false), report);
        Assert.Empty(rig.Store.ReferenceQueries);
        Assert.Empty(rig.Store.ActivityQueries);
        Assert.DoesNotContain(rig.Journal.Entries, entry => entry.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEntryWhoseKindContradictsItsNameIsLeftAlone()
    {
        var rig = new Rig();
        var attempt = rig.Register(BackupStatus.Failed);
        var objectWithPartialName = KeyOf(attempt.Tenant, attempt.Job, attempt.Run, attempt.Id, PartialName);
        var uploadWithObjectName = KeyOf(attempt.Tenant, attempt.Job, attempt.Run, attempt.Id);
        rig.Storage.AddObject(objectWithPartialName, Now - Old).AddPartial(uploadWithObjectName, Now - Old, inUse: false);

        var report = await rig.SweepAsync();

        Assert.True(rig.Storage.Has(objectWithPartialName));
        Assert.True(rig.Storage.Has(uploadWithObjectName));
        Assert.Equal(2, report.Unrecognized);
    }

    // ---- bounds and isolation ----

    [Fact]
    public async Task ASweepStopsAtTheDeletionLimitAndSaysSo()
    {
        var rig = new Rig(new BackupRetentionOptions { MaxSweepDeletionsPerRun = 2 });
        var attempt = rig.Register(BackupStatus.Failed);
        var keys = Enumerable.Range(0, 5).Select(i => rig.Object(attempt, Old, name: $"Sales_2026100{i}T000000Z_aaaaaaa{i}_FULL.bak")).ToArray();

        var report = await rig.SweepAsync();

        Assert.Equal(2, report.OrphansDeleted);
        Assert.True(report.Truncated);
        Assert.Equal(3, keys.Count(rig.Storage.Has));
    }

    [Fact]
    public async Task UploadsAndOrphansShareOneDeletionBudget()
    {
        var rig = new Rig(new BackupRetentionOptions { MaxSweepDeletionsPerRun = 2 });
        var attempt = rig.Register(BackupStatus.Failed);
        rig.Partial(attempt, Old);
        rig.Object(attempt, Old, name: "Sales_20261001T000000Z_aaaaaaaa_FULL.bak");
        rig.Object(attempt, Old, name: "Sales_20261002T000000Z_bbbbbbbb_FULL.bak");

        var report = await rig.SweepAsync();

        Assert.Equal(2, report.OrphansDeleted + report.PartialUploadsDeleted);
        Assert.True(report.Truncated);
    }

    [Fact]
    public async Task ASweepThatNeedsExactlyTheLimitIsNotTruncated()
    {
        var rig = new Rig(new BackupRetentionOptions { MaxSweepDeletionsPerRun = 2 });
        var attempt = rig.Register(BackupStatus.Failed);
        rig.Object(attempt, Old, name: "Sales_20261001T000000Z_aaaaaaaa_FULL.bak");
        rig.Object(attempt, Old, name: "Sales_20261002T000000Z_bbbbbbbb_FULL.bak");

        var report = await rig.SweepAsync();

        Assert.Equal(2, report.OrphansDeleted);
        Assert.False(report.Truncated);
    }

    [Fact]
    public async Task ATruncatedSweepDoesNotMoveOnToTheNextStorage()
    {
        var rig = new Rig(new BackupRetentionOptions { MaxSweepDeletionsPerRun = 1 });
        var attempt = rig.Register(BackupStatus.Failed);
        rig.Object(attempt, Old, name: "Sales_20261001T000000Z_aaaaaaaa_FULL.bak");
        rig.Object(attempt, Old, name: "Sales_20261002T000000Z_bbbbbbbb_FULL.bak");
        var second = new FakeStorage();
        rig.Resolver.Storages.Add(new BackupManagedStorage("second", StorageProviderId.Central, null, second));

        var report = await rig.SweepAsync();

        Assert.True(report.Truncated);
        Assert.Equal(0, second.EnumerateCalls);
    }

    [Fact]
    public async Task AStorageThatCannotBeListedIsCountedAndTheNextOneIsStillSwept()
    {
        var rig = new Rig();
        rig.Storage.EnumerateFault = new StorageException(StorageFailureKind.AccessDenied, "denied");
        var second = new FakeStorage { Id = StorageProviderId.Central };
        var attempt = rig.Register(BackupStatus.Failed);
        var key = Rig.KeyFor(attempt);
        second.AddObject(key, Now - Old);
        rig.Resolver.Storages.Add(new BackupManagedStorage("second", StorageProviderId.Central, null, second));

        var report = await rig.SweepAsync();

        Assert.Equal(1, report.StoragesUnavailable);
        Assert.False(second.Has(key));
        Assert.Equal(1, report.OrphansDeleted);
    }

    [Fact]
    public async Task AListingThatBreaksHalfwayStillProcessesWhatItYielded()
    {
        var rig = new Rig();
        var attempt = rig.Register(BackupStatus.Failed);
        var first = rig.Object(attempt, Old, name: "Sales_20261001T000000Z_aaaaaaaa_FULL.bak");
        var second = rig.Object(attempt, Old, name: "Sales_20261002T000000Z_bbbbbbbb_FULL.bak");
        rig.Storage.EnumerateFault = new StorageException(StorageFailureKind.Io, "io");
        rig.Storage.FaultAfterEntries = 1;

        var report = await rig.SweepAsync();

        Assert.Equal(1, report.StoragesUnavailable);
        Assert.Equal(1, report.OrphansDeleted);
        Assert.Equal(1, new[] { first, second }.Count(rig.Storage.Has));
    }

    [Fact]
    public async Task AnyExceptionOfTheListingIsIsolatedToThatStorage()
    {
        var rig = new Rig();
        rig.Storage.EnumerateFault = new InvalidOperationException("boom");

        var report = await rig.SweepAsync();

        Assert.Equal(1, report.StoragesUnavailable);
    }

    [Fact]
    public async Task AStorageWithoutTheEnumerateCapabilityIsCountedAndNeverListed()
    {
        var rig = new Rig();
        rig.Storage.Capabilities = new StorageCapabilities([StorageCapabilityIds.Delete]);

        var report = await rig.SweepAsync();

        Assert.Equal(1, report.StoragesUnavailable);
        Assert.Equal(0, rig.Storage.EnumerateCalls);
    }

    [Fact]
    public async Task AFailedDeleteIsCountedAndTheRestOfTheSweepGoesOn()
    {
        var rig = new Rig();
        var attempt = rig.Register(BackupStatus.Failed);
        var failing = rig.Object(attempt, Old, name: "Sales_20261001T000000Z_aaaaaaaa_FULL.bak");
        var other = rig.Object(attempt, Old, name: "Sales_20261002T000000Z_bbbbbbbb_FULL.bak");
        rig.Storage.DeleteFault = key => key.Value == failing ? new StorageException(StorageFailureKind.AccessDenied, "denied") : null;

        var report = await rig.SweepAsync();

        Assert.True(rig.Storage.Has(failing));
        Assert.False(rig.Storage.Has(other));
        Assert.Equal(1, report.Failed);
        Assert.Equal(1, report.OrphansDeleted);
    }

    [Fact]
    public async Task AFileSomebodyElseDeletedFirstCompletesDurableCleanupWhenAbsenceIsVerified()
    {
        var rig = new Rig();
        var key = rig.Object(rig.Register(BackupStatus.Failed), Old);
        rig.Storage.OnDelete = k => rig.Storage.Remove(k.Value);

        var report = await rig.SweepAsync();

        Assert.False(rig.Storage.Has(key));
        Assert.Equal(1, report.OrphansDeleted);
        Assert.Equal(0, report.Failed);
        Assert.Single(rig.Store.CleanupCompletions);
    }

    [Fact]
    public async Task APreCrashPersistedCleanupRecoversAndCompletesEvenIfEnumerationNoLongerYieldsIt()
    {
        var rig = new Rig();
        var attempt = rig.Register(BackupStatus.Failed);
        var key = Rig.KeyFor(attempt);
        // Physical file was deleted before crash or vanished, never returned by enumeration:
        var cleanupId = Guid.NewGuid();
        var pending = new PendingStorageCleanup(
            cleanupId, new TenantId(attempt.Tenant), attempt.Job, attempt.Run, attempt.Id,
            "local", "main", Guid.NewGuid().ToString("N"), key, false);
        rig.Store.PendingCleanups.Add(pending);

        var report = await rig.SweepAsync();

        Assert.Equal(1, report.OrphansDeleted);
        Assert.Equal(0, report.Failed);
        Assert.Contains(rig.Journal.Entries, e => e.StartsWith("exists:", StringComparison.Ordinal));
        Assert.Contains(rig.Journal.Entries, e => e == $"complete-cleanup:{cleanupId}");
        Assert.Single(rig.Store.CleanupCompletions);
    }

    [Fact]
    public async Task AuditPersistenceFailureRollsBackCleanupCompletionAndLeavesItRetryable()
    {
        var rig = new Rig();
        var attempt = rig.Register(BackupStatus.Failed);
        var key = rig.Object(attempt, Old);
        rig.Store.CompleteCleanupResult = _ => false;

        var report = await rig.SweepAsync();

        Assert.Equal(1, report.Failed);
        Assert.Equal(0, report.OrphansDeleted);
        Assert.Empty(rig.Store.CleanupCompletions);
    }

    [Fact]
    public async Task ConcurrentOrDuplicateSweepExecutionIsLeaseFenced()
    {
        var rig = new Rig();
        var attempt = rig.Register(BackupStatus.Failed);
        var key = rig.Object(attempt, Old);
        // Simulate another node currently holding active lease on this cleanup
        rig.Store.OnBeginSweptDeletion = (_, _, _, _, _, _) => null;

        var report = await rig.SweepAsync();

        Assert.True(rig.Storage.Has(key));
        Assert.Equal(0, report.OrphansDeleted);
        Assert.Equal(0, report.Failed);
        Assert.Empty(rig.Store.CleanupCompletions);
    }

    [Fact]
    public async Task AFailureOfTheDatabaseAbortsTheSweepInsteadOfBeingMistakenForAStorageProblem()
    {
        var rig = new Rig();
        var key = rig.Object(rig.Register(BackupStatus.Failed), Old);
        rig.Store.AfterReferenceQuery = _ => throw new InvalidOperationException("database unavailable");

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.SweepAsync());

        Assert.True(rig.Storage.Has(key));
    }

    [Fact]
    public async Task LargeListingsAreProcessedInBoundedChunks()
    {
        var rig = new Rig(new BackupRetentionOptions { MaxSweepDeletionsPerRun = 1000 });
        for (var i = 0; i < 450; i++) rig.Object(rig.Register(BackupStatus.Failed), Old);

        var report = await rig.SweepAsync();

        Assert.Equal(450, report.Examined);
        Assert.Equal(450, report.OrphansDeleted);
        Assert.Equal([200, 200, 50], rig.Store.ActivityQueries.Select(query => query.Length));
        Assert.Equal([200, 200, 50], rig.Store.ReferenceQueries.Where(query => query.Keys.Length > 1).Select(query => query.Keys.Length));
    }

    [Fact]
    public async Task AnAttemptsFilesAreLookedUpOnceNotOncePerFile()
    {
        var rig = new Rig();
        var attempt = rig.Register(BackupStatus.Failed);
        rig.Object(attempt, Old, name: "Sales_20261001T000000Z_aaaaaaaa_FULL.bak");
        rig.Object(attempt, Old, name: "Sales_20261002T000000Z_bbbbbbbb_FULL.bak");
        rig.Partial(attempt, Old);

        await rig.SweepAsync();

        Assert.Equal([attempt.Id], Assert.Single(rig.Store.ActivityQueries));
    }

    // ---- cancellation ----

    [Fact]
    public async Task ACancelledSweepDeletesNothing()
    {
        var rig = new Rig();
        var key = rig.Object(rig.Register(BackupStatus.Failed), Old);
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.SweepAsync(source.Token));

        Assert.True(rig.Storage.Has(key));
    }

    [Fact]
    public async Task CancellationWhileListingPropagatesAndIsNotCountedAsAnUnavailableStorage()
    {
        var rig = new Rig();
        var attempt = rig.Register(BackupStatus.Failed);
        var first = rig.Object(attempt, Old, name: "Sales_20261001T000000Z_aaaaaaaa_FULL.bak");
        var second = rig.Object(attempt, Old, name: "Sales_20261002T000000Z_bbbbbbbb_FULL.bak");
        using var source = new CancellationTokenSource();
        rig.Storage.AfterYield = _ => source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.SweepAsync(source.Token));

        Assert.True(rig.Storage.Has(first));
        Assert.True(rig.Storage.Has(second));
    }

    [Fact]
    public async Task CancellationBetweenDeletesStopsBeforeTheNextFile()
    {
        var rig = new Rig();
        var attempt = rig.Register(BackupStatus.Failed);
        var first = rig.Object(attempt, Old, name: "Sales_20261001T000000Z_aaaaaaaa_FULL.bak");
        var second = rig.Object(attempt, Old, name: "Sales_20261002T000000Z_bbbbbbbb_FULL.bak");
        using var source = new CancellationTokenSource();
        rig.Storage.OnDelete = _ => source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.SweepAsync(source.Token));

        Assert.Equal(1, new[] { first, second }.Count(rig.Storage.Has));
    }

    // ---- logs ----

    [Fact]
    public async Task LogsNeverContainKeysPathsOrProviderMessages()
    {
        var rig = new Rig();
        var attempt = rig.Register(BackupStatus.Failed);
        var deleted = rig.Object(attempt, Old, name: "Sales_20261001T000000Z_aaaaaaaa_FULL.bak", prefix: "vault-prefix");
        var failed = rig.Object(attempt, Old, name: "Sales_20261002T000000Z_bbbbbbbb_FULL.bak", prefix: "vault-prefix");
        rig.Storage.DeleteFault = key => key.Value == failed ? new StorageException(StorageFailureKind.Io, $"cannot open {SecretPath}", new IOException(SecretPath)) : null;
        var second = new FakeStorage { EnumerateFault = new StorageException(StorageFailureKind.Io, $"cannot list {SecretPath}") };
        rig.Resolver.Storages.Add(new BackupManagedStorage("unreachable", StorageProviderId.Central, null, second));

        await rig.SweepAsync();

        var text = rig.Log.AllText;
        Assert.NotEmpty(text);
        Assert.DoesNotContain(deleted, text);
        Assert.DoesNotContain(failed, text);
        Assert.DoesNotContain(".bak", text);
        Assert.DoesNotContain("vault-prefix", text);
        Assert.DoesNotContain(SecretPath, text);
        Assert.DoesNotContain("cannot open", text);
        Assert.DoesNotContain("cannot list", text);
    }
}
