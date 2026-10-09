using System.Security.Cryptography;
using BackupPlatform.Api.Backups;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.GoogleDrive;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackupPlatform.IntegrationTests.Storage;

public sealed class GoogleDriveStagingReliabilityTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var database = fixture.CreatePlatformContext();
        await database.GoogleDriveStagingReservations.ExecuteDeleteAsync();
    }

    public async Task DisposeAsync()
    {
        await using var database = fixture.CreatePlatformContext();
        await database.GoogleDriveStagingReservations.ExecuteDeleteAsync();
    }
    private const string OwnedDirectory = "backup-platform-drive-staging";
    private static readonly byte[] Content = [1, 2, 3, 4, 5, 6, 7, 8];
    private static ContentChecksum Checksum => ContentChecksum.Sha256(SHA256.HashData(Content));
    private DbContextOptions<PlatformDbContext> Database => new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(fixture.ConnectionString).Options;

    [Fact]
    public async Task IndependentStoresCannotOversubscribeGlobalAdmission()
    {
        using var firstRoot = new TemporaryRoot();
        using var secondRoot = new TemporaryRoot();
        using var first = Store(firstRoot.Path, "first", capacity: 16);
        using var second = Store(secondRoot.Path, "second", capacity: 16);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(index => TryStageAsync(index % 2 == 0 ? first : second)));
        try
        {
            Assert.Equal(2, results.Count(handle => handle is not null));
            await using var database = fixture.CreatePlatformContext();
            Assert.Equal(16m, await database.GoogleDriveStagingReservations.SumAsync(row => (decimal)row.ExpectedBytes));
        }
        finally
        {
            foreach (var handle in results.OfType<GoogleDriveStagedFile>()) await handle.DisposeAsync();
        }
        await AssertEmptyAsync();
    }

    [Fact]
    public async Task NodeAdmissionCountsReservationsAcrossDifferentVolumes()
    {
        using var firstRoot = new TemporaryRoot();
        using var secondRoot = new TemporaryRoot();
        using var first = Store(firstRoot.Path, "same-node", capacity: 32, nodeCapacity: 8);
        using var second = Store(secondRoot.Path, "same-node", capacity: 32, nodeCapacity: 8);
        var handles = await Task.WhenAll(TryStageAsync(first), TryStageAsync(second));
        try { Assert.Single(handles.OfType<GoogleDriveStagedFile>()); }
        finally { foreach (var handle in handles.OfType<GoogleDriveStagedFile>()) await handle.DisposeAsync(); }
        await AssertEmptyAsync();
    }

    [Fact]
    public async Task PhysicalAdmissionIncludesOutstandingUnwrittenBytesOnSharedVolume()
    {
        using var root = new TemporaryRoot();
        using var first = Store(root.Path, "node-one", volume: "shared", probe: _ => 12);
        using var second = Store(root.Path, "node-two", volume: "shared", probe: _ => 12);
        using var source = new PausedStream(Content);
        var pending = first.StageAsync(source, Content.Length, Checksum, default);
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var error = await Assert.ThrowsAsync<StorageException>(() => second.StageAsync(new MemoryStream(Content), Content.Length, Checksum, default));
            Assert.Equal(StorageFailureKind.InsufficientSpace, error.Kind);
        }
        finally { source.Continue.TrySetResult(); }
        await using var staged = await pending;
        await using var database = fixture.CreatePlatformContext();
        Assert.Single(await database.GoogleDriveStagingReservations.ToListAsync());
    }

    [Fact]
    public async Task StaleFilesRecoverAtStartupWithoutDeletingUntrackedUserFiles()
    {
        using var root = new TemporaryRoot();
        var userFile = System.IO.Path.Combine(root.Path, "user.backup");
        await File.WriteAllTextAsync(userFile, "user data");
        var row = await SeedStaleAsync(root.Path, "old-node", "recoverable", writeFile: true);
        var untracked = System.IO.Path.Combine(row.DirectoryPath, "untracked.stage");
        await File.WriteAllTextAsync(untracked, "not owned by a reservation");
        using var store = Store(root.Path, "new-node", volume: "recoverable");
        await store.StartAsync(default);
        try
        {
            await EventuallyAsync(async () =>
            {
                await using var database = fixture.CreatePlatformContext();
                return !await database.GoogleDriveStagingReservations.AnyAsync(candidate => candidate.Id == row.Id);
            });
            Assert.False(File.Exists(System.IO.Path.Combine(row.DirectoryPath, row.FileName)));
            Assert.Equal("user data", await File.ReadAllTextAsync(userFile));
            Assert.Equal("not owned by a reservation", await File.ReadAllTextAsync(untracked));
        }
        finally { await store.StopAsync(default); }
    }

    [Fact]
    public async Task DefaultVolumeIdentitySurvivesNodeIdentityChange()
    {
        using var root = new TemporaryRoot();
        using (var first = Store(root.Path, "old-container")) await first.CleanupAsync(default);
        var identity = await File.ReadAllTextAsync(System.IO.Path.Combine(root.Path, OwnedDirectory, "volume.id"));
        var row = await SeedStaleAsync(root.Path, "old-container", $"local-{identity}", writeFile: true);
        using var replacement = Store(root.Path, "replacement-container");
        await replacement.CleanupAsync(default);
        Assert.Equal(identity, await File.ReadAllTextAsync(System.IO.Path.Combine(root.Path, OwnedDirectory, "volume.id")));
        await AssertEmptyAsync();
        Assert.False(File.Exists(System.IO.Path.Combine(row.DirectoryPath, row.FileName)));
    }

    [Fact]
    public async Task ExpiredActiveFileLockPreservesAccountingAndLeaseLossCancelsOwner()
    {
        using var root = new TemporaryRoot();
        using var owner = Store(root.Path, "node-one", volume: "shared", lease: TimeSpan.FromMilliseconds(600));
        using var cleaner = Store(root.Path, "node-two", volume: "shared");
        var handle = await owner.StageAsync(new MemoryStream(Content), Content.Length, Checksum, default);
        try
        {
            await using var database = fixture.CreatePlatformContext();
            var row = await database.GoogleDriveStagingReservations.SingleAsync();
            await database.GoogleDriveStagingReservations.ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.LeaseExpiresAt, DateTimeOffset.UtcNow.AddHours(-1)));
            await cleaner.CleanupAsync(default);
            Assert.True(await database.GoogleDriveStagingReservations.AnyAsync());
            Assert.True(File.Exists(System.IO.Path.Combine(row.DirectoryPath, row.FileName)));
            using var reader = new FileStream(handle.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Assert.Equal(Content.Length, reader.Length);
            await EventuallyAsync(() => Task.FromResult(handle.CancellationToken.IsCancellationRequested));
        }
        finally { await handle.DisposeAsync(); }
        await AssertEmptyAsync();
    }

    [Fact]
    public async Task StaleMissingFileReservationRemainsCountedUntilLocalCleanup()
    {
        using var root = new TemporaryRoot();
        using var anotherRoot = new TemporaryRoot();
        var row = await SeedStaleAsync(root.Path, "gone-node", "gone-volume", writeFile: false);
        using var other = Store(anotherRoot.Path, "other", capacity: 8);
        Assert.Null(await TryStageAsync(other));
        using var recovered = Store(root.Path, "replacement", volume: "gone-volume", capacity: 8);
        await recovered.CleanupAsync(default);
        await using var handle = await recovered.StageAsync(new MemoryStream(Content), Content.Length, Checksum, default);
        await using var database = fixture.CreatePlatformContext();
        Assert.False(await database.GoogleDriveStagingReservations.AnyAsync(candidate => candidate.Id == row.Id));
    }

    [Fact]
    public async Task DiskLossBetweenWritesDeletesBeforeReleasingAdmission()
    {
        using var root = new TemporaryRoot();
        long available = Content.Length;
        using var store = Store(root.Path, "node", probe: _ => Interlocked.Read(ref available));
        using var source = new ChunkedStream(Content, count => { if (count == 2) Interlocked.Exchange(ref available, 0); });
        var error = await Assert.ThrowsAsync<StorageException>(() => store.StageAsync(source, Content.Length, Checksum, default));
        Assert.Equal(StorageFailureKind.InsufficientSpace, error.Kind);
        await AssertEmptyAsync();
        Assert.Empty(Directory.EnumerateFiles(System.IO.Path.Combine(root.Path, OwnedDirectory), "*.stage"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public async Task UnknownOrNonPositiveLengthIsRejected(long length)
    {
        using var root = new TemporaryRoot();
        using var store = Store(root.Path, "node");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.StageAsync(new MemoryStream(Content), length, Checksum, default));
        await AssertEmptyAsync();
    }

    [Theory]
    [InlineData(4)]
    [InlineData(9)]
    public async Task LengthMismatchReleasesOnlyAfterDeletingFile(int actualLength)
    {
        using var root = new TemporaryRoot();
        using var store = Store(root.Path, "node");
        var error = await Assert.ThrowsAsync<StorageException>(() => store.StageAsync(new MemoryStream(new byte[actualLength]), Content.Length, Checksum, default));
        Assert.Equal(StorageFailureKind.IntegrityMismatch, error.Kind);
        await AssertEmptyAsync();
        Assert.Empty(Directory.EnumerateFiles(System.IO.Path.Combine(root.Path, OwnedDirectory), "*.stage"));
    }

    [Fact]
    public async Task ChecksumMismatchAndOversizedLengthsAreRejected()
    {
        using var root = new TemporaryRoot();
        using var store = Store(root.Path, "node");
        var error = await Assert.ThrowsAsync<StorageException>(() => store.StageAsync(new MemoryStream(new byte[Content.Length]), Content.Length, Checksum, default));
        Assert.Equal(StorageFailureKind.IntegrityMismatch, error.Kind);
        error = await Assert.ThrowsAsync<StorageException>(() => store.StageAsync(new MemoryStream(Content), long.MaxValue, Checksum, default));
        Assert.Equal(StorageFailureKind.InsufficientSpace, error.Kind);
        await AssertEmptyAsync();
    }

    [Fact]
    public async Task FreeSpaceArithmeticCannotOverflow()
    {
        using var root = new TemporaryRoot();
        var configuration = Configuration(root.Path, "node", capacity: long.MaxValue, nodeCapacity: long.MaxValue);
        configuration.MinStagingFreeBytes = long.MaxValue;
        using var store = new PostgreSqlGoogleDriveStagingStore(Database, Options.Create(configuration), NullLogger<PostgreSqlGoogleDriveStagingStore>.Instance);
        var error = await Assert.ThrowsAsync<StorageException>(() => store.StageAsync(new MemoryStream(Content), Content.Length, Checksum, default));
        Assert.Equal(StorageFailureKind.InsufficientSpace, error.Kind);
        await AssertEmptyAsync();
    }

    [Fact]
    public async Task CallerCancellationDuringCopyDeletesAndReleases()
    {
        using var root = new TemporaryRoot();
        using var cancellation = new CancellationTokenSource();
        using var store = Store(root.Path, "node");
        using var source = new ChunkedStream(Content, count => { if (count == 2) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.StageAsync(source, Content.Length, Checksum, cancellation.Token));
        await AssertEmptyAsync();
        Assert.Empty(Directory.EnumerateFiles(System.IO.Path.Combine(root.Path, OwnedDirectory), "*.stage"));
    }

    [Fact]
    public async Task PostgreSqlUnavailableRejectsBeforeCreatingStagedContent()
    {
        using var root = new TemporaryRoot();
        var unavailable = new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=unavailable;Username=none;Password=none;Timeout=1;Command Timeout=1;Pooling=false").Options;
        using var store = new PostgreSqlGoogleDriveStagingStore(unavailable, Options.Create(Configuration(root.Path, "node")), NullLogger<PostgreSqlGoogleDriveStagingStore>.Instance);
        var error = await Assert.ThrowsAsync<StorageException>(() => store.StageAsync(new MemoryStream(Content), Content.Length, Checksum, default));
        Assert.Equal(StorageFailureKind.Io, error.Kind);
        Assert.Empty(Directory.EnumerateFiles(System.IO.Path.Combine(root.Path, OwnedDirectory), "*.stage"));
        await AssertEmptyAsync();
    }

    [Fact]
    public async Task SymlinkAncestorsAndTrackedSymlinksAreNeverTraversed()
    {
        if (OperatingSystem.IsWindows()) return; // Windows reparse paths require an administrator-enabled fixture.
        using var root = new TemporaryRoot();
        using var outside = new TemporaryRoot();
        var link = System.IO.Path.Combine(root.Path, "linked");
        Directory.CreateSymbolicLink(link, outside.Path);
        using var unsafeStore = Store(link, "node");
        var rejected = await Assert.ThrowsAsync<StorageException>(() => unsafeStore.StageAsync(new MemoryStream(Content), Content.Length, Checksum, default));
        Assert.Equal(StorageFailureKind.Io, rejected.Kind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
        var victim = System.IO.Path.Combine(outside.Path, "user.backup");
        await File.WriteAllTextAsync(victim, "preserve");
        var row = await SeedStaleAsync(root.Path, "node", "secure", writeFile: false);
        File.CreateSymbolicLink(System.IO.Path.Combine(row.DirectoryPath, row.FileName), victim);
        using var cleaner = Store(root.Path, "node", volume: "secure");
        await cleaner.CleanupAsync(default);
        await using var database = fixture.CreatePlatformContext();
        Assert.True(await database.GoogleDriveStagingReservations.AnyAsync(candidate => candidate.Id == row.Id));
        Assert.Equal("preserve", await File.ReadAllTextAsync(victim));
        File.Delete(System.IO.Path.Combine(row.DirectoryPath, row.FileName));
        await cleaner.CleanupAsync(default);
        await AssertEmptyAsync();
    }

    [Fact]
    public async Task PrivateModesAndMalformedTrackedNamesProtectUserFiles()
    {
        using var root = new TemporaryRoot();
        var row = await SeedStaleAsync(root.Path, "node", "secure", writeFile: true);
        var userFile = System.IO.Path.Combine(root.Path, "user.backup");
        await File.WriteAllTextAsync(userFile, "preserve");
        await using var database = fixture.CreatePlatformContext();
        await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE google_drive_staging_reservations SET \"FileName\" = {"../user.backup"} WHERE \"Id\" = {row.Id}");
        using var cleaner = Store(root.Path, "node", volume: "secure");
        await cleaner.CleanupAsync(default);
        Assert.True(await database.GoogleDriveStagingReservations.AnyAsync());
        Assert.Equal("preserve", await File.ReadAllTextAsync(userFile));
        await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE google_drive_staging_reservations SET \"FileName\" = {row.FileName} WHERE \"Id\" = {row.Id}");
        await cleaner.CleanupAsync(default);
        await using var handle = await cleaner.StageAsync(new MemoryStream(Content), Content.Length, Checksum, default);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(System.IO.Path.Combine(root.Path, OwnedDirectory)));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(handle.Path));
        }
    }

    [Fact]
    public async Task MidStreamDatabaseFailureCleansPhysicalFileAndFailsClosed()
    {
        using var root = new TemporaryRoot();
        using var store = Store(root.Path, "node");
        using var source = new ChunkedStream(Content, count =>
        {
            if (count == 2)
            {
                // Drop the test database or corrupt the reservation row to simulate database outage mid-stream
                using var context = fixture.CreatePlatformContext();
                context.Database.ExecuteSqlRaw("UPDATE google_drive_staging_reservations SET \"LeaseExpiresAt\" = clock_timestamp() - interval '1 hour'");
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.StageAsync(source, Content.Length, Checksum, default));
        Assert.Empty(Directory.EnumerateFiles(System.IO.Path.Combine(root.Path, OwnedDirectory), "*.stage"));
        await AssertEmptyAsync();
    }

    [Fact]
    public void StagingAdmissionLockKey_DecimalValueMatchesHex()
    {
        Assert.Equal(4778394325016135425L, PostgreSqlGoogleDriveStagingStore.AdmissionLockKey);
        Assert.Equal(0x4250_4452_5354_4701L, PostgreSqlGoogleDriveStagingStore.AdmissionLockKey);
    }

    [Fact]
    public async Task AdministrativeRecovery_DeletesOnlyOrphanedStaleReservations_AndNeverActiveUploadsOrHealthyNodes()
    {
        var orphanedVolume = $"lost-vol-{Guid.NewGuid():N}";
        var orphanedNode = "decommissioned-node";
        var healthyVolume = $"healthy-vol-{Guid.NewGuid():N}";
        var healthyNode = "active-node-1";

        var now = DateTimeOffset.UtcNow;

        var orphanedStale = new GoogleDriveStagingReservation(
            Guid.NewGuid(), Guid.NewGuid(), orphanedVolume, orphanedNode, false,
            "/tmp/lost", 1024, now.AddHours(-1), now.AddMinutes(-20));

        var healthyActive = new GoogleDriveStagingReservation(
            Guid.NewGuid(), Guid.NewGuid(), healthyVolume, healthyNode, false,
            "/tmp/healthy", 2048, now, now.AddMinutes(10));

        var healthyRecentExpired = new GoogleDriveStagingReservation(
            Guid.NewGuid(), Guid.NewGuid(), healthyVolume, healthyNode, false,
            "/tmp/healthy", 4096, now.AddMinutes(-10), now.AddMinutes(-5));

        var healthyStale = new GoogleDriveStagingReservation(
            Guid.NewGuid(), Guid.NewGuid(), healthyVolume, healthyNode, false,
            "/tmp/healthy", 8192, now.AddHours(-1), now.AddMinutes(-30));

        var differentNodeStale = new GoogleDriveStagingReservation(
            Guid.NewGuid(), Guid.NewGuid(), orphanedVolume, "other-node", false,
            "/tmp/other", 16384, now.AddHours(-1), now.AddMinutes(-30));

        await using (var db = fixture.CreatePlatformContext())
        {
            db.GoogleDriveStagingReservations.AddRange(orphanedStale, healthyActive, healthyRecentExpired, healthyStale, differentNodeStale);
            await db.SaveChangesAsync();
        }

        await using var conn = new Npgsql.NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var lockCmd = new Npgsql.NpgsqlCommand($"SELECT pg_advisory_xact_lock({PostgreSqlGoogleDriveStagingStore.AdmissionLockKey});", conn, tx))
        {
            await lockCmd.ExecuteNonQueryAsync();
        }

        int deletedCount;
        await using (var deleteCmd = new Npgsql.NpgsqlCommand("""
            DELETE FROM google_drive_staging_reservations
            WHERE "VolumeId" = @volumeId
              AND "NodeId" = @nodeId
              AND "LeaseExpiresAt" < clock_timestamp() - interval '15 minutes';
            """, conn, tx))
        {
            deleteCmd.Parameters.AddWithValue("volumeId", orphanedVolume);
            deleteCmd.Parameters.AddWithValue("nodeId", orphanedNode);
            deletedCount = await deleteCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();

        Assert.Equal(1, deletedCount);

        await using (var db = fixture.CreatePlatformContext())
        {
            var remaining = await db.GoogleDriveStagingReservations.ToListAsync();
            Assert.DoesNotContain(remaining, r => r.Id == orphanedStale.Id);
            Assert.Contains(remaining, r => r.Id == healthyActive.Id);
            Assert.Contains(remaining, r => r.Id == healthyRecentExpired.Id);
            Assert.Contains(remaining, r => r.Id == healthyStale.Id);
            Assert.Contains(remaining, r => r.Id == differentNodeStale.Id);
        }
    }

    private PostgreSqlGoogleDriveStagingStore Store(string root, string node, string volume = "", long capacity = 32,
        long nodeCapacity = 32, Func<string, long>? probe = null, TimeSpan? lease = null) => new(Database,
        Options.Create(Configuration(root, node, volume, capacity, nodeCapacity, lease)), NullLogger<PostgreSqlGoogleDriveStagingStore>.Instance,
        availableBytesProbe: probe);

    private static GoogleDrivePlatformOptions Configuration(string root, string node, string volume = "", long capacity = 32,
        long nodeCapacity = 32, TimeSpan? lease = null) => new()
    {
        StagingDirectory = root, StagingNodeId = node, StagingVolumeId = volume,
        StagingCapacityBytes = capacity, NodeStagingCapacityBytes = nodeCapacity, MinStagingFreeBytes = 0,
        StagingLeaseDuration = lease ?? TimeSpan.FromMinutes(2), StagingCleanupAge = TimeSpan.Zero,
        StagingCleanupInterval = TimeSpan.FromMilliseconds(50)
    };

    private async Task<GoogleDriveStagingReservation> SeedStaleAsync(string root, string node, string volume, bool writeFile)
    {
        using (var initializer = Store(root, node, volume: volume)) await initializer.CleanupAsync(default);
        var path = System.IO.Path.Combine(root, OwnedDirectory);
        var row = new GoogleDriveStagingReservation(Guid.NewGuid(), Guid.NewGuid(), volume, node, true, path,
            Content.Length, DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow.AddHours(-1));
        await using var database = fixture.CreatePlatformContext();
        database.GoogleDriveStagingReservations.Add(row);
        await database.SaveChangesAsync();
        if (writeFile)
        {
            await File.WriteAllBytesAsync(System.IO.Path.Combine(path, row.FileName), Content);
            await database.GoogleDriveStagingReservations.Where(candidate => candidate.Id == row.Id).ExecuteUpdateAsync(set => set.SetProperty(candidate => candidate.WrittenBytes, (long)Content.Length));
        }
        return row;
    }

    private static async Task<GoogleDriveStagedFile?> TryStageAsync(PostgreSqlGoogleDriveStagingStore store)
    {
        try { return await store.StageAsync(new MemoryStream(Content), Content.Length, Checksum, default); }
        catch (StorageException exception) when (exception.Kind == StorageFailureKind.InsufficientSpace) { return null; }
    }

    private async Task AssertEmptyAsync()
    {
        await using var database = fixture.CreatePlatformContext();
        Assert.Empty(await database.GoogleDriveStagingReservations.ToListAsync());
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await condition()) await Task.Delay(20, deadline.Token);
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot() => Directory.CreateDirectory(Path);
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"drive-staging-tests-{Guid.NewGuid():N}");
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class PausedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Continue.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class ChunkedStream(byte[] bytes, Action<int> onRead) : MemoryStream(bytes)
    {
        private int _reads;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            onRead(++_reads);
            return await base.ReadAsync(buffer[..Math.Min(4, buffer.Length)], cancellationToken);
        }
    }
}
