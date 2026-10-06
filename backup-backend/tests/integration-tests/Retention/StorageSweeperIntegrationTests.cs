using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Central;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// The orphan and unpublished-upload sweep over real directories and the real database: a backup file a replica references, anything the
/// platform did not write, anything that may still be in use and anything outside the storage root is never deleted; only an old managed leftover of a
/// finished attempt is. File times are set explicitly against the clock the sweeper reads, so nothing sleeps.
/// </summary>
public sealed class StorageSweeperIntegrationTests
{
    private static readonly DateTimeOffset Now = RetentionServiceWorld.Accepted + TimeSpan.FromDays(1);
    private static readonly TimeSpan Old = TimeSpan.FromDays(2);
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(30);
    private const string OtherName = "Sales_20261001T000000Z_aaaaaaaa_FULL.bak";
    private const string ThirdName = "Sales_20261002T000000Z_bbbbbbbb_FULL.bak";
    private const string PartialName = ".Sales_20261005T053015Z_a1b2c3d4_FULL.bak.0123456789abcdef0123456789abcdef.partial";

    /// <summary>A backup that is never expired automatically, so only the sweeper is in play.</summary>
    private static Task<SeededWorld> SeedAsync(RetentionServiceWorld world) => world.SeedAsync(RetentionKit.Policy(enabled: false));

    private static async Task<SeededAttempt> RunningAttemptAsync(RetentionServiceWorld world, DateTimeOffset at)
    {
        var agent = await world.Harness.AddAgentAsync();
        var job = await RetentionKit.AddJobAsync(world.Harness, agent);
        var store = world.Harness.Store();
        var run = await world.Harness.StartRunAsync(store, job, Guid.NewGuid().ToString("N"), at);
        var claim = await BackupStoreHarness.ClaimAsync(store, agent, at.AddSeconds(1)) ?? throw new InvalidOperationException("The agent could not claim the run.");
        await BackupStoreHarness.StartAttemptAsync(store, claim, at.AddSeconds(2));
        return new SeededAttempt(agent.TenantId, job.Id, run.Id, claim.Attempt.Id);
    }

    private static async Task<SeededAttempt> EndedAttemptAsync(RetentionServiceWorld world, string code = "PreCheckFailed") =>
        await world.SeedFailedAttemptAsync(await world.Harness.AddAgentAsync(), code, RetentionServiceWorld.Accepted);

    private static string Fingerprint(string directory) => string.Join('\n', Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(path => $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{Convert.ToHexString(File.ReadAllBytes(path))}"));

    [Fact]
    public async Task AFileAReplicaReferencesIsNeverDeletedWhateverItsStatus()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var backup = (await SeedAsync(world)).Backup;
            var clock = new MutableClock(Now);
            world.Backdate(backup.Key, Now - TimeSpan.FromDays(30));
            var sweeper = world.Sweeper(clock);

            var available = await sweeper.SweepAsync(CancellationToken.None);

            Assert.True(world.Exists(backup.Key));
            Assert.Equal(1, available.KeptReferenced);
            Assert.Equal(0, available.OrphansDeleted);

            // Soft deleted: still referenced and still there.
            var requested = await world.Artifacts(clock).RequestDeleteAsync(backup.TenantId, backup.ArtifactId, Guid.NewGuid(), CancellationToken.None);
            Assert.Equal(BackupArtifactDeleteKind.Requested, requested.Kind);
            Assert.Equal(1, (await sweeper.SweepAsync(CancellationToken.None)).KeptReferenced);
            Assert.True(world.Exists(backup.Key));

            // Physically deleted by retention, then something puts a file under the same key again: the Deleted replica still names it.
            var later = new MutableClock(Now + TimeSpan.FromDays(2));
            var cycle = await world.Service("node-a", later).RunCycleAsync(CancellationToken.None);
            Assert.Equal(1, cycle.Deleted);
            Assert.False(world.Exists(backup.Key));
            world.Place(backup.Key, modified: later.GetUtcNow() - TimeSpan.FromDays(3));

            var deleted = await world.Sweeper(later).SweepAsync(CancellationToken.None);

            Assert.True(world.Exists(backup.Key));
            Assert.Equal(1, deleted.KeptReferenced);
            Assert.Equal(0, deleted.OrphansDeleted);
        });
    }

    [Fact]
    public async Task FilesThePlatformDidNotWriteAreNeverDeleted()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            await SeedAsync(world);
            var strangerAttempt = new SeededAttempt(TenantId.New(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            string[] files =
            [
                "notes.txt",
                "reports/q3.bak",
                strangerAttempt.Key(),
                strangerAttempt.Key(PartialName),
                strangerAttempt.Key(prefix: "vault")
            ];
            foreach (var key in files) world.Place(key, modified: Now - Old);
            var before = Fingerprint(world.Root);

            var report = await world.Sweeper(new MutableClock(Now)).SweepAsync(CancellationToken.None);

            Assert.Equal(before, Fingerprint(world.Root));
            Assert.All(files, key => Assert.True(world.Exists(key), key));
            // The seeded replica's own file is referenced and the strangers are not ours: nothing is an orphan.
            Assert.Equal(0, report.OrphansDeleted + report.PartialUploadsDeleted);
            Assert.Equal(files.Length, report.Unrecognized);
        });
    }

    [Fact]
    public async Task AnOldManagedLeftoverOfAFinishedAttemptIsDeletedAndAFreshOneIsKept()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seeded = await SeedAsync(world);
            var backup = seeded.Backup;
            var ended = await EndedAttemptAsync(world);
            world.Backdate(backup.Key, Now - Old);
            // An older sibling file of the SUCCEEDED attempt that no replica names, and a leftover of a failed attempt.
            var staleOfSucceeded = $"{backup.Key[..backup.Key.LastIndexOf('/')]}/{OtherName}";
            world.Place(staleOfSucceeded, modified: Now - Old);
            var staleOfFailed = ended.Key();
            world.Place(staleOfFailed, modified: Now - Old);
            var freshOfFailed = ended.Key(ThirdName);
            world.Place(freshOfFailed, modified: Now - Fresh);

            var report = await world.Sweeper(new MutableClock(Now)).SweepAsync(CancellationToken.None);

            Assert.False(world.Exists(staleOfSucceeded));
            Assert.False(world.Exists(staleOfFailed));
            Assert.True(world.Exists(freshOfFailed));
            Assert.True(world.Exists(backup.Key));
            Assert.Equal(2, report.OrphansDeleted);
            Assert.Equal(1, report.KeptReferenced);
            // The history behind the files is never touched by a sweep.
            Assert.Equal(BackupReplicaStatus.Available, (await world.ReplicaAsync(backup.ReplicaId)).Status);
            Assert.Equal(BackupArtifactState.Available, (await world.ArtifactAsync(backup.ArtifactId)).State);
        });
    }

    [Fact]
    public async Task AnOldFileOfARunThatWaitsForARetryOrIsRunningIsKept()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var retrying = await EndedAttemptAsync(world, code: "DatabaseUnavailable");
            var running = await RunningAttemptAsync(world, RetentionServiceWorld.Accepted);
            var store = world.Store();
            var activity = await store.GetAttemptActivityAsync([retrying.AttemptId, running.AttemptId], CancellationToken.None);
            // Guards the setup: the retrying attempt itself is over but its run is not.
            Assert.Equal(BackupStatus.RetryPending, activity[retrying.AttemptId].RunStatus);
            Assert.Equal(BackupStatus.Failed, activity[retrying.AttemptId].AttemptStatus);
            Assert.Equal(BackupStatus.Running, activity[running.AttemptId].RunStatus);
            var retryingFile = retrying.Key();
            var runningFile = running.Key();
            world.Place(retryingFile, modified: Now - Old);
            world.Place(runningFile, modified: Now - Old);

            var report = await world.Sweeper(new MutableClock(Now)).SweepAsync(CancellationToken.None);

            Assert.True(world.Exists(retryingFile));
            Assert.True(world.Exists(runningFile));
            Assert.Equal(2, report.KeptActive);
            Assert.Equal(0, report.OrphansDeleted);
        });
    }

    [Fact]
    public async Task AnOldIdleUnpublishedUploadIsDeletedButAFreshOneOrOneAWriterHoldsOpenOrOneOfALiveAttemptIsKept()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var ended = await EndedAttemptAsync(world);
            var live = await RunningAttemptAsync(world, RetentionServiceWorld.Accepted);
            var idle = ended.Key(PartialName);
            var young = ended.Key(".Sales_20261003T000000Z_cccccccc_FULL.bak.fedcba9876543210fedcba9876543210.partial");
            var held = ended.Key(".Sales_20261004T000000Z_dddddddd_FULL.bak.00000000000000000000000000000001.partial");
            var ofLive = live.Key(PartialName);
            world.Place(idle, modified: Now - Old);
            world.Place(young, modified: Now - Fresh);
            var heldPath = world.Place(held, modified: Now - Old);
            world.Place(ofLive, modified: Now - Old);

            StorageSweepReport report;
            await using (new FileStream(heldPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                report = await world.Sweeper(new MutableClock(Now)).SweepAsync(CancellationToken.None);
            }

            Assert.False(world.Exists(idle));
            Assert.True(world.Exists(young));
            Assert.True(world.Exists(held));
            Assert.True(world.Exists(ofLive));
            Assert.Equal(1, report.PartialUploadsDeleted);
            Assert.Equal(2, report.KeptActive);
            Assert.Equal(0, report.OrphansDeleted);
        });
    }

    [Fact]
    public async Task ASymlinkedDirectoryInsideTheStorageIsNotFollowedAndItsTargetStaysUntouched()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var ended = await EndedAttemptAsync(world);
            var outside = world.NewDirectory();
            // Were the link followed, this would be an old managed leftover of a finished attempt below the prefix "linked": exactly what is deleted.
            var inside = ended.Key();
            world.Place(inside, root: outside, modified: Now - Old);
            var linkPath = Path.Combine(world.Root, "linked");
            try
            {
                Directory.CreateSymbolicLink(linkPath, outside);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            var before = Fingerprint(outside);
            try
            {
                var report = await world.Sweeper(new MutableClock(Now)).SweepAsync(CancellationToken.None);

                Assert.Equal(before, Fingerprint(outside));
                Assert.True(world.Exists(inside, outside));
                Assert.Equal(0, report.OrphansDeleted);
                Assert.Equal(0, report.Failed);
            }
            finally
            {
                Directory.Delete(linkPath);
            }
        });
    }

    [Fact]
    public async Task NothingOutsideTheStorageRootIsEverTouched()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var ended = await EndedAttemptAsync(world);
            var sibling = world.NewDirectory();
            // Same layout, same finished attempt, old: a file in a directory BESIDE the root must survive a sweep that deletes the one inside it.
            world.Place(ended.Key(), root: sibling, modified: Now - Old);
            world.Place("neighbour.txt", root: sibling, modified: Now - Old);
            var inside = ended.Key(OtherName);
            world.Place(inside, modified: Now - Old);
            var before = Fingerprint(sibling);

            var report = await world.Sweeper(new MutableClock(Now)).SweepAsync(CancellationToken.None);

            Assert.False(world.Exists(inside));
            Assert.Equal(1, report.OrphansDeleted);
            Assert.Equal(before, Fingerprint(sibling));
        });
    }

    [Fact]
    public async Task ATenantScopedCentralStorageNeverTouchesAnotherTenantsFiles()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var ours = await EndedAttemptAsync(world);
            var theirs = await EndedAttemptAsync(world);
            var central = world.NewDirectory();
            var provider = new CentralStorageProvider(new CentralStorageOptions(central, ours.TenantId.Value.ToString("N")));
            world.Resolver.Add(StorageProviderId.Central, "central", ours.TenantId, provider);
            var ourScope = Path.Combine(central, "tenants", ours.TenantId.Value.ToString("N"));
            var theirScope = Path.Combine(central, "tenants", theirs.TenantId.Value.ToString("N"));
            var own = ours.Key();
            // Another tenant's managed-looking file in OUR area, and in THEIR own area.
            var impostor = theirs.Key();
            var theirOwn = theirs.Key(OtherName);
            world.Place(own, root: ourScope, modified: Now - Old);
            world.Place(impostor, root: ourScope, modified: Now - Old);
            world.Place(theirOwn, root: theirScope, modified: Now - Old);

            var report = await world.Sweeper(new MutableClock(Now)).SweepAsync(CancellationToken.None);

            Assert.False(world.Exists(own, ourScope));
            Assert.True(world.Exists(impostor, ourScope));
            Assert.True(world.Exists(theirOwn, theirScope));
            Assert.Equal(1, report.OrphansDeleted);
            Assert.Equal(1, report.Unrecognized);
        });
    }

    [Fact]
    public async Task ASweepCancelledAfterTheFirstDeleteStopsWithTheRestOfTheFilesIntact()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var ended = await EndedAttemptAsync(world);
            string[] names = [OtherName, ThirdName, "Sales_20261003T000000Z_cccccccc_FULL.bak", "Sales_20261004T000000Z_dddddddd_FULL.bak", "Sales_20261005T000000Z_eeeeeeee_FULL.bak"];
            foreach (var name in names) world.Place(ended.Key(name), modified: Now - Old);
            using var source = new CancellationTokenSource();
            world.Storage.AfterDelete = _ => source.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => world.Sweeper(new MutableClock(Now)).SweepAsync(source.Token));

            Assert.Equal(1, world.Storage.TotalDeleteCalls);
            Assert.Equal(names.Length - 1, names.Count(name => world.Exists(ended.Key(name))));
        });
    }

    [Fact]
    public async Task ATruncatedSweepContinuesWhereItStoppedOnTheNextRun()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var ended = await EndedAttemptAsync(world);
            string[] names = [OtherName, ThirdName, "Sales_20261003T000000Z_cccccccc_FULL.bak", "Sales_20261004T000000Z_dddddddd_FULL.bak"];
            foreach (var name in names) world.Place(ended.Key(name), modified: Now - Old);
            var sweeper = world.Sweeper(new MutableClock(Now), new BackupRetentionOptions { MaxSweepDeletionsPerRun = 2 });

            var first = await sweeper.SweepAsync(CancellationToken.None);
            Assert.Equal((2, true), (first.OrphansDeleted, first.Truncated));
            Assert.Equal(2, names.Count(name => world.Exists(ended.Key(name))));

            var second = await sweeper.SweepAsync(CancellationToken.None);
            Assert.Equal(2, second.OrphansDeleted);
            Assert.False(second.Truncated);
            Assert.Equal(0, names.Count(name => world.Exists(ended.Key(name))));
        });
    }

    [Fact]
    public async Task TwoSweepersRacingOverTheSameOrphansDeleteThemAllWithoutFailing()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var ended = await EndedAttemptAsync(world);
            var names = Enumerable.Range(0, 12).Select(i => $"Sales_2026100{i % 10}T0000{i:00}Z_aaaaaa{i:00}_FULL.bak").ToArray();
            foreach (var name in names) world.Place(ended.Key(name), modified: Now - Old);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sweeps = Enumerable.Range(0, 2).Select(_ =>
            {
                var sweeper = world.Sweeper(new MutableClock(Now));
                return Task.Run(async () =>
                {
                    await gate.Task;
                    return await sweeper.SweepAsync(CancellationToken.None);
                });
            }).ToArray();
            gate.SetResult();

            var reports = await Task.WhenAll(sweeps);

            Assert.All(names, name => Assert.False(world.Exists(ended.Key(name)), name));
            Assert.Equal(0, reports.Sum(report => report.Failed));
            Assert.True(reports.Sum(report => report.OrphansDeleted) >= names.Length);
        });
    }
}
