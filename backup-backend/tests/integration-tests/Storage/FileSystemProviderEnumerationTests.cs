using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.IntegrationTests.Storage;

/// <summary>Enumeration (the sweep's view of the storage) against a real file system, for every file-system backed provider.</summary>
public abstract partial class FileSystemProviderContractTests
{
    private static DateTimeOffset InTheFuture => DateTimeOffset.UtcNow.AddHours(1);

    [Fact]
    public void FileSystemProvidersAdvertiseEnumerateAndVerifyAbsence()
    {
        Assert.True(Provider.Capabilities.Supports(StorageCapabilityIds.Enumerate));
        Assert.True(Provider.Capabilities.Supports(StorageCapabilityIds.VerifyAbsence));
    }

    [Fact]
    public async Task EnumerateListsPublishedObjectsWithSizeKindAndRoundTrippableKeys()
    {
        await Put(new StorageObjectKey("top.bak"), PatternStream.Bytes(10));
        await Put(new StorageObjectKey("mssql/sales/full.bak"), PatternStream.Bytes(300));

        var entries = await EnumerateAllAsync();

        Assert.Equal(["mssql/sales/full.bak", "top.bak"], entries.Select(e => e.Key.Value).Order(StringComparer.Ordinal));
        Assert.All(entries, e =>
        {
            Assert.Equal(StorageEntryKind.Object, e.Kind);
            Assert.Null(e.InUse);
            Assert.Equal(e.Key, new StorageObjectKey(e.Key.Value));
            Assert.Equal(TimeSpan.Zero, e.LastModifiedUtc.Offset);
            Assert.InRange(e.LastModifiedUtc, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
        });
        Assert.Equal(300, entries.Single(e => e.Key.Value == "mssql/sales/full.bak").SizeBytes);
        Assert.Equal(10, entries.Single(e => e.Key.Value == "top.bak").SizeBytes);
    }

    [Fact]
    public async Task EnumerateReturnsOnlyFilesLastModifiedBeforeTheCutoff()
    {
        await Put(new StorageObjectKey("old.bak"), PatternStream.Bytes(5));
        await Put(new StorageObjectKey("young.bak"), PatternStream.Bytes(5));
        var cutoff = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(PhysicalPath("old.bak"), cutoff.AddMinutes(-1));
        File.SetLastWriteTimeUtc(PhysicalPath("young.bak"), cutoff.AddMinutes(1));

        var entries = await EnumerateAllAsync(before: new DateTimeOffset(cutoff, TimeSpan.Zero));

        Assert.Equal("old.bak", Assert.Single(entries).Key.Value);
        Assert.Equal(new DateTimeOffset(cutoff.AddMinutes(-1), TimeSpan.Zero), entries[0].LastModifiedUtc);
    }

    [Fact]
    public async Task EnumerateHonoursThePrefixAndKeepsKeysRelativeToTheProviderScope()
    {
        await Put(new StorageObjectKey("a/one.bak"), PatternStream.Bytes(1));
        await Put(new StorageObjectKey("a/b/two.bak"), PatternStream.Bytes(1));
        await Put(new StorageObjectKey("c/three.bak"), PatternStream.Bytes(1));

        var entries = await EnumerateAllAsync(new StorageObjectKey("a"));

        Assert.Equal(["a/b/two.bak", "a/one.bak"], entries.Select(e => e.Key.Value).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task EnumerateOfAMissingPrefixOrAnEmptyScopeIsEmpty()
    {
        Assert.Empty(await EnumerateAllAsync());
        await Put(new StorageObjectKey("a/one.bak"), PatternStream.Bytes(1));

        Assert.Empty(await EnumerateAllAsync(new StorageObjectKey("nothing/here")));
        Assert.Empty(await EnumerateAllAsync(new StorageObjectKey("a/one.bak")));
    }

    [Fact]
    public async Task UnpublishedUploadsAreRecognisedAndReportedInUseOnlyWhileTheWriterHoldsThem()
    {
        var directory = Path.Combine(ObjectsDirectory, "mssql");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".full.bak.{new string('a', 32)}.partial");
        var released = Path.Combine(directory, $".other.bak.{new string('0', 16)}{new string('f', 16)}.partial");
        await File.WriteAllBytesAsync(released, [1, 2, 3]);
        File.SetLastWriteTimeUtc(released, DateTime.UtcNow.AddDays(-2));

        StorageEntry held;
        await using (var writer = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await writer.WriteAsync(new byte[] { 1, 2, 3, 4 });
            await writer.FlushAsync();
            held = Assert.Single(await EnumerateAllAsync(), e => e.Key.Value.Contains(new string('a', 32), StringComparison.Ordinal));
        }

        Assert.Equal(StorageEntryKind.UnpublishedUpload, held.Kind);
        Assert.Equal(4, held.SizeBytes);
        Assert.True(held.InUse);

        var entries = await EnumerateAllAsync();
        Assert.All(entries, e => Assert.Equal(StorageEntryKind.UnpublishedUpload, e.Kind));
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.False(e.InUse));
    }

    [Fact]
    public async Task OnlyTheEnginesOwnTemporaryNamingIsAnUnpublishedUpload()
    {
        Directory.CreateDirectory(ObjectsDirectory);
        foreach (var name in new[] { "x.partial", ".x.partial", $".x.{new string('A', 32)}.partial", $".x.{new string('a', 31)}.partial", $"..{new string('a', 32)}.partial" })
        {
            await File.WriteAllBytesAsync(Path.Combine(ObjectsDirectory, name), [1]);
        }

        var entries = await EnumerateAllAsync();

        Assert.Equal(5, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Equal(StorageEntryKind.Object, e.Kind);
            Assert.Null(e.InUse);
        });
    }

    [Fact]
    public async Task FilesWhoseNamesAreNotStorageKeysAreSilentlySkipped()
    {
        await Put(new StorageObjectKey("ours.bak"), PatternStream.Bytes(1));
        Directory.CreateDirectory(Path.Combine(ObjectsDirectory, "with space"));
        await File.WriteAllBytesAsync(Path.Combine(ObjectsDirectory, "with space", "inner.bak"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(ObjectsDirectory, "user file.txt"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(ObjectsDirectory, "caf\u00e9.bak"), [1]);

        var entries = await EnumerateAllAsync();

        Assert.Equal("ours.bak", Assert.Single(entries).Key.Value);
    }

    [SymlinkFact]
    public async Task LinksBelowTheStartAreNeitherReturnedNorDescended()
    {
        var outside = NewOutsideDirectory();
        await File.WriteAllBytesAsync(Path.Combine(outside, "secret.bak"), [9, 9]);
        await Put(new StorageObjectKey("real/ours.bak"), PatternStream.Bytes(3));
        File.CreateSymbolicLink(Path.Combine(ObjectsDirectory, "real", "filelink.bak"), Path.Combine(outside, "secret.bak"));
        Directory.CreateSymbolicLink(Path.Combine(ObjectsDirectory, "dirlink"), outside);
        File.CreateSymbolicLink(Path.Combine(ObjectsDirectory, "dangling.bak"), Path.Combine(outside, "not-there"));

        var entries = await EnumerateAllAsync();

        Assert.Equal("real/ours.bak", Assert.Single(entries).Key.Value);
    }

    [SymlinkFact]
    public async Task ASymlinkedPrefixDirectoryIsRefused()
    {
        var outside = NewOutsideDirectory();
        await File.WriteAllBytesAsync(Path.Combine(outside, "secret.bak"), [9, 9]);
        Directory.CreateDirectory(ObjectsDirectory);
        Directory.CreateSymbolicLink(Path.Combine(ObjectsDirectory, "linked"), outside);

        await ExpectFailure(StorageFailureKind.AccessDenied, () => EnumerateAllAsync(new StorageObjectKey("linked")), outside);
        await ExpectFailure(StorageFailureKind.AccessDenied, () => EnumerateAllAsync(new StorageObjectKey("linked/deeper")), outside);
    }

    [Fact]
    public async Task EnumerateWithAMissingRootIsAnInvalidConfiguration()
    {
        var provider = CreateProvider(Path.Combine(Root, "not-mounted"));

        await ExpectFailure(StorageFailureKind.InvalidConfiguration, async () =>
        {
            await foreach (var _ in provider.EnumerateAsync(null, InTheFuture, CancellationToken.None))
            {
            }
        });
    }

    [Fact]
    public async Task EnumerateObservesCancellation()
    {
        await Put(new StorageObjectKey("a.bak"), PatternStream.Bytes(1));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Provider.EnumerateAsync(null, InTheFuture, cancelled.Token))
            {
            }
        });
    }

    [Fact]
    public async Task AnEnumeratedUnpublishedUploadCanBeDeletedByItsKey()
    {
        Directory.CreateDirectory(Path.Combine(ObjectsDirectory, "d"));
        var leftover = Path.Combine(ObjectsDirectory, "d", $".full.bak.{new string('c', 32)}.partial");
        await File.WriteAllBytesAsync(leftover, [1, 2]);

        var entry = Assert.Single(await EnumerateAllAsync());

        Assert.True(await Provider.DeleteAsync(entry.Key, CancellationToken.None));
        Assert.False(File.Exists(leftover));
        Assert.False(await Provider.ExistsAsync(entry.Key, CancellationToken.None));
        Assert.Empty(await EnumerateAllAsync());
    }

    [Fact]
    public async Task AfterDeleteExistsIsImmediatelyFalse()
    {
        var key = new StorageObjectKey("gone.bak");
        await Put(key, PatternStream.Bytes(10));

        Assert.True(await Provider.DeleteAsync(key, CancellationToken.None));

        Assert.True(Provider.Capabilities.Supports(StorageCapabilityIds.VerifyAbsence));
        Assert.False(await Provider.ExistsAsync(key, CancellationToken.None));
    }

    private async Task<List<StorageEntry>> EnumerateAllAsync(StorageObjectKey? prefix = null, DateTimeOffset? before = null)
    {
        var entries = new List<StorageEntry>();
        await foreach (var entry in Provider.EnumerateAsync(prefix, before ?? InTheFuture, CancellationToken.None))
        {
            entries.Add(entry);
        }

        return entries;
    }
}
