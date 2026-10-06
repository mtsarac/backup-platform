using System.Security.Cryptography;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.IntegrationTests.Storage;

/// <summary>
/// The behaviour every file-system backed provider must show against a real file system. The Local and Central providers run the
/// same tests through thin subclasses; each test works in its own <c>bp_test_*</c> temp directory and touches nothing else.
/// </summary>
public abstract partial class FileSystemProviderContractTests : IDisposable
{
    private const int MiB = 1024 * 1024;

    protected FileSystemProviderContractTests()
    {
        Root = NewTempPath();
        Directory.CreateDirectory(Root);
    }

    /// <summary>The configured storage root (exists).</summary>
    protected string Root { get; }

    /// <summary>The directory below which the provider physically places key paths.</summary>
    protected abstract string ObjectsDirectory { get; }

    protected abstract IStorageProvider CreateProvider(string root);

    private IStorageProvider Provider => field ??= CreateProvider(Root);

    public void Dispose()
    {
        foreach (var path in Enumerable.Reverse(_tempPaths))
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task PutExistsMetadataVerifyDeleteRoundTrip()
    {
        var key = new StorageObjectKey("sales_full.bak");
        var bytes = PatternStream.Bytes(70_000);
        var checksum = ContentChecksum.Sha256(SHA256.HashData(bytes));
        var progress = new RecordingProgress();

        var stored = await Provider.PutAsync(key, new MemoryStream(bytes), new StoragePutOptions(StorageOverwritePolicy.Fail), progress, CancellationToken.None);

        Assert.Equal(new StorageLocation(Provider.Id, key), stored.Location);
        Assert.Equal(bytes.Length, stored.SizeBytes);
        Assert.Equal(checksum, stored.Checksum);
        Assert.InRange(stored.LastModifiedUtc, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal([(long)bytes.Length], progress.Values);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(PhysicalPath(key.Value)));

        Assert.True(await Provider.ExistsAsync(key, CancellationToken.None));

        var metadata = await Provider.GetMetadataAsync(key, CancellationToken.None);
        Assert.NotNull(metadata);
        Assert.Equal(stored.Location, metadata.Location);
        Assert.Equal(bytes.Length, metadata.SizeBytes);
        Assert.Null(metadata.Checksum);
        Assert.Equal(stored.LastModifiedUtc, metadata.LastModifiedUtc);

        var verified = await Provider.VerifyAsync(key, new StorageVerificationExpectation(bytes.Length, checksum), CancellationToken.None);
        Assert.Equal(StorageVerificationStatus.Verified, verified.Status);
        Assert.Equal(StorageVerificationMethod.ReadBack, verified.Method);
        Assert.Equal(bytes.Length, verified.ActualSizeBytes);
        Assert.Equal(checksum, verified.ActualChecksum);
        Assert.Equal(stored.Location, verified.Location);

        Assert.True(await Provider.DeleteAsync(key, CancellationToken.None));
        Assert.False(await Provider.ExistsAsync(key, CancellationToken.None));
        Assert.Null(await Provider.GetMetadataAsync(key, CancellationToken.None));
        Assert.False(await Provider.DeleteAsync(key, CancellationToken.None));
        Assert.Empty(AllFiles());
    }

    [Fact]
    public async Task EmptyObjectsAreSupported()
    {
        var key = new StorageObjectKey("empty.bak");

        var stored = await Put(key, new MemoryStream());

        Assert.Equal(0, stored.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([])), stored.Checksum!.Hex);
        Assert.True(await Provider.ExistsAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task NestedKeyCreatesDirectoriesBelowTheObjectsDirectory()
    {
        var key = new StorageObjectKey("tenant-x/mssql/sales/2026-10-05/sales_full.bak");

        await Put(key, PatternStream.Bytes(1000));

        var expected = Path.Combine(ObjectsDirectory, "tenant-x", "mssql", "sales", "2026-10-05", "sales_full.bak");
        Assert.Equal(PatternStream.Bytes(1000), await File.ReadAllBytesAsync(expected));
        Assert.Equal([expected], AllFiles());
    }

    [Fact]
    public async Task ExistingObjectWithFailPolicyIsRejectedAndUntouched()
    {
        var key = new StorageObjectKey("dir/db.bak");
        await Put(key, PatternStream.Bytes(5000));
        var before = await File.ReadAllBytesAsync(PhysicalPath(key.Value));

        var failure = await ExpectFailure(StorageFailureKind.AlreadyExists, () => Put(key, new PatternStream(9000)));

        Assert.NotNull(failure);
        Assert.Equal(before, await File.ReadAllBytesAsync(PhysicalPath(key.Value)));
        Assert.Single(AllFiles());
    }

    [Fact]
    public async Task ReplacePolicySwapsTheContentAndLeavesNoTemporaryFile()
    {
        var key = new StorageObjectKey("db.bak");
        await Put(key, PatternStream.Bytes(5000));

        var replaced = await Provider.PutAsync(key, new PatternStream(12_345), new StoragePutOptions(StorageOverwritePolicy.Replace), null, CancellationToken.None);

        Assert.Equal(12_345, replaced.SizeBytes);
        Assert.Equal(PatternStream.ChecksumOf(12_345), replaced.Checksum);
        Assert.Equal(PatternStream.Bytes(12_345), await File.ReadAllBytesAsync(PhysicalPath(key.Value)));
        Assert.Equal([PhysicalPath(key.Value)], AllFiles());
    }

    [Fact]
    public async Task ReplacePolicyCreatesTheObjectWhenNothingExists()
    {
        var key = new StorageObjectKey("fresh.bak");

        await Provider.PutAsync(key, new PatternStream(10), new StoragePutOptions(StorageOverwritePolicy.Replace), null, CancellationToken.None);

        Assert.Equal(PatternStream.Bytes(10), await File.ReadAllBytesAsync(PhysicalPath(key.Value)));
    }

    [Fact]
    public async Task ReplacedObjectStaysVisibleUntilTheNewOneIsComplete()
    {
        var key = new StorageObjectKey("db.bak");
        await Put(key, PatternStream.Bytes(3000));
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var replacing = Provider.PutAsync(key, new PatternStream(2 * MiB, Stall(stalled)), new StoragePutOptions(StorageOverwritePolicy.Replace), null, cancellation.Token);
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var during = await Provider.VerifyAsync(key, new StorageVerificationExpectation(3000, PatternStream.ChecksumOf(3000)), CancellationToken.None);
        Assert.Equal(StorageVerificationStatus.Verified, during.Status);
        Assert.Equal(2, AllFiles().Count);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replacing);

        Assert.Equal([PhysicalPath(key.Value)], AllFiles());
        Assert.Equal(PatternStream.Bytes(3000), await File.ReadAllBytesAsync(PhysicalPath(key.Value)));
    }

    [Fact]
    public async Task ContentIsNotVisibleUnderTheKeyWhileItIsStillBeingWritten()
    {
        var key = new StorageObjectKey("nested/in-flight.bak");
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var writing = Provider.PutAsync(key, new PatternStream(MiB, Stall(stalled)), new StoragePutOptions(StorageOverwritePolicy.Fail), null, cancellation.Token);
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(await Provider.ExistsAsync(key, CancellationToken.None));
        Assert.Null(await Provider.GetMetadataAsync(key, CancellationToken.None));
        var partial = Assert.Single(AllFiles());
        Assert.Equal(Path.GetDirectoryName(PhysicalPath(key.Value)), Path.GetDirectoryName(partial));
        Assert.StartsWith(".in-flight.bak.", Path.GetFileName(partial), StringComparison.Ordinal);
        Assert.EndsWith(".partial", partial, StringComparison.Ordinal);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
        Assert.Empty(AllFiles());
    }

    [Fact]
    public async Task ExpectedChecksumMismatchFailsAndLeavesNothing()
    {
        var key = new StorageObjectKey("sub/db.bak");
        var wrong = PatternStream.ChecksumOf(99);

        await ExpectFailure(StorageFailureKind.IntegrityMismatch, () => Provider.PutAsync(
            key, new PatternStream(100), new StoragePutOptions(StorageOverwritePolicy.Fail, ExpectedChecksum: wrong), null, CancellationToken.None));

        Assert.Empty(AllFiles());
        Assert.False(await Provider.ExistsAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task ChecksumMismatchDuringReplaceKeepsTheOriginal()
    {
        var key = new StorageObjectKey("db.bak");
        await Put(key, PatternStream.Bytes(400));

        await ExpectFailure(StorageFailureKind.IntegrityMismatch, () => Provider.PutAsync(
            key, new PatternStream(100), new StoragePutOptions(StorageOverwritePolicy.Replace, ExpectedChecksum: PatternStream.ChecksumOf(99)), null, CancellationToken.None));

        Assert.Equal(PatternStream.Bytes(400), await File.ReadAllBytesAsync(PhysicalPath(key.Value)));
        Assert.Single(AllFiles());
    }

    [Fact]
    public async Task MatchingExpectationsAreAccepted()
    {
        var key = new StorageObjectKey("ok.bak");

        var stored = await Provider.PutAsync(
            key, new PatternStream(3 * MiB), new StoragePutOptions(StorageOverwritePolicy.Fail, PatternStream.ChecksumOf(3 * MiB), 3 * MiB), null, CancellationToken.None);

        Assert.Equal(3 * MiB, stored.SizeBytes);
        Assert.Equal(PatternStream.ChecksumOf(3 * MiB), stored.Checksum);
    }

    [Fact]
    public async Task SourceLongerThanExpectedLengthIsRejectedBeforeWritingMoreThanExpected()
    {
        var progress = new RecordingProgress();

        await ExpectFailure(StorageFailureKind.IntegrityMismatch, () => Provider.PutAsync(
            new StorageObjectKey("long.bak"), new PatternStream(3 * MiB), new StoragePutOptions(StorageOverwritePolicy.Fail, ExpectedLength: 100), progress, CancellationToken.None));

        Assert.Empty(progress.Values);
        Assert.Empty(AllFiles());
    }

    [Fact]
    public async Task SourceShorterThanExpectedLengthIsRejected()
    {
        await ExpectFailure(StorageFailureKind.IntegrityMismatch, () => Provider.PutAsync(
            new StorageObjectKey("short.bak"), new PatternStream(10), new StoragePutOptions(StorageOverwritePolicy.Fail, ExpectedLength: 5000), null, CancellationToken.None));

        await ExpectFailure(StorageFailureKind.IntegrityMismatch, () => Provider.PutAsync(
            new StorageObjectKey("empty.bak"), new MemoryStream(), new StoragePutOptions(StorageOverwritePolicy.Fail, ExpectedLength: 1), null, CancellationToken.None));

        Assert.Empty(AllFiles());
    }

    [Fact]
    public async Task SourceFailingMidCopyIsAnIoFailureAndLeavesNothing()
    {
        var key = new StorageObjectKey("broken.bak");
        var progress = new RecordingProgress();

        var failure = await ExpectFailure(StorageFailureKind.Io, () => Provider.PutAsync(
            key,
            new PatternStream((MiB * 3) / 2, _ => throw new IOException("network share vanished")),
            new StoragePutOptions(StorageOverwritePolicy.Fail),
            progress,
            CancellationToken.None));

        Assert.IsType<IOException>(failure.InnerException);
        Assert.Equal([(long)MiB, (MiB * 3L) / 2], progress.Values);
        Assert.Empty(AllFiles());
        Assert.False(await Provider.ExistsAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task SourceFailingDuringReplaceKeepsTheOriginal()
    {
        var key = new StorageObjectKey("db.bak");
        await Put(key, PatternStream.Bytes(400));

        await ExpectFailure(StorageFailureKind.Io, () => Provider.PutAsync(
            key, new PatternStream(MiB, _ => throw new IOException("boom")), new StoragePutOptions(StorageOverwritePolicy.Replace), null, CancellationToken.None));

        Assert.Equal(PatternStream.Bytes(400), await File.ReadAllBytesAsync(PhysicalPath(key.Value)));
        Assert.Single(AllFiles());
    }

    [Fact]
    public async Task SourceDiskFullErrorIsNotBlamedOnTheStorage()
    {
        var failure = await ExpectFailure(StorageFailureKind.Io, () => Provider.PutAsync(
            new StorageObjectKey("x.bak"),
            new PatternStream(10, _ => throw new IOException("source disk full", 28)),
            new StoragePutOptions(StorageOverwritePolicy.Fail),
            null,
            CancellationToken.None));

        Assert.NotEqual(StorageFailureKind.InsufficientSpace, failure.Kind);
    }

    [Fact]
    public async Task CancellationMidCopySurfacesUnwrappedAndLeavesNothing()
    {
        var key = new StorageObjectKey("cancelled/db.bak");
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var writing = Provider.PutAsync(key, new PatternStream(2 * MiB, Stall(stalled)), new StoragePutOptions(StorageOverwritePolicy.Fail), null, cancellation.Token);
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cancellation.CancelAsync();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
        Assert.IsNotType<StorageException>(thrown);
        Assert.Empty(AllFiles());
        Assert.False(await Provider.ExistsAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationBetweenBlocksStopsTheCopy()
    {
        using var cancellation = new CancellationTokenSource();
        var progress = new Progress(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider.PutAsync(
            new StorageObjectKey("between.bak"), new PatternStream(5 * MiB), new StoragePutOptions(StorageOverwritePolicy.Fail), progress, cancellation.Token));

        Assert.Equal(1, progress.Reports);
        Assert.Empty(AllFiles());
    }

    [Fact]
    public async Task PreCancelledOperationsDoNothing()
    {
        var cancelled = new CancellationToken(true);
        var key = new StorageObjectKey("never.bak");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider.PutAsync(key, new PatternStream(10), new StoragePutOptions(StorageOverwritePolicy.Fail), null, cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider.ExistsAsync(key, cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider.GetMetadataAsync(key, cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider.DeleteAsync(key, cancelled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider.VerifyAsync(key, new StorageVerificationExpectation(1, PatternStream.ChecksumOf(1)), cancelled));

        Assert.Empty(AllFiles());
    }

    [Fact]
    public async Task VerifyReportsMissing()
    {
        var key = new StorageObjectKey("nothing/here.bak");

        var result = await Provider.VerifyAsync(key, new StorageVerificationExpectation(10, PatternStream.ChecksumOf(10)), CancellationToken.None);

        Assert.Equal(StorageVerificationStatus.Missing, result.Status);
        Assert.Equal(StorageVerificationMethod.ReadBack, result.Method);
        Assert.Null(result.ActualSizeBytes);
        Assert.Null(result.ActualChecksum);
        Assert.Equal(new StorageLocation(Provider.Id, key), result.Location);
    }

    [Fact]
    public async Task VerifyReportsSizeMismatchWithoutReadingTheContent()
    {
        var key = new StorageObjectKey("size.bak");
        await Put(key, PatternStream.Bytes(1000));

        var result = await Provider.VerifyAsync(key, new StorageVerificationExpectation(999, PatternStream.ChecksumOf(999)), CancellationToken.None);

        Assert.Equal(StorageVerificationStatus.SizeMismatch, result.Status);
        Assert.Equal(1000, result.ActualSizeBytes);
        Assert.Null(result.ActualChecksum);
    }

    [Fact]
    public async Task VerifyDetectsTamperingWithTheStoredFile()
    {
        var key = new StorageObjectKey("tamper.bak");
        var original = PatternStream.Bytes(4 * MiB);
        await Put(key, original);
        var tampered = (byte[])original.Clone();
        tampered[(3 * MiB) + 17] ^= 0xFF;
        await File.WriteAllBytesAsync(PhysicalPath(key.Value), tampered);

        var result = await Provider.VerifyAsync(key, new StorageVerificationExpectation(original.Length, ContentChecksum.Sha256(SHA256.HashData(original))), CancellationToken.None);

        Assert.Equal(StorageVerificationStatus.ChecksumMismatch, result.Status);
        Assert.Equal(original.Length, result.ActualSizeBytes);
        Assert.Equal(ContentChecksum.Sha256(SHA256.HashData(tampered)), result.ActualChecksum);
    }

    [Fact]
    public async Task StreamsLargeObjectsInBlocks()
    {
        var key = new StorageObjectKey("large/20mib.bak");
        const long size = 20L * MiB;
        var progress = new RecordingProgress();

        var stored = await Provider.PutAsync(key, new PatternStream(size), new StoragePutOptions(StorageOverwritePolicy.Fail), progress, CancellationToken.None);

        Assert.Equal(size, stored.SizeBytes);
        Assert.Equal(PatternStream.ChecksumOf(size), stored.Checksum);
        Assert.Equal(Enumerable.Range(1, 20).Select(i => i * (long)MiB), progress.Values);

        var verified = await Provider.VerifyAsync(key, new StorageVerificationExpectation(size, stored.Checksum!), CancellationToken.None);
        Assert.Equal(StorageVerificationStatus.Verified, verified.Status);
        Assert.Equal(size, new FileInfo(PhysicalPath(key.Value)).Length);
    }

    [Fact]
    public async Task ConcurrentWritersToOneKeyYieldExactlyOneCompleteObject()
    {
        var key = new StorageObjectKey("race.bak");
        var sizes = Enumerable.Range(1, 8).Select(i => i * 100_000L).ToArray();

        var outcomes = await Task.WhenAll(sizes.Select(async size =>
        {
            try
            {
                await Provider.PutAsync(key, new PatternStream(size), new StoragePutOptions(StorageOverwritePolicy.Fail), null, CancellationToken.None);
                return (Size: size, Failure: (StorageFailureKind?)null);
            }
            catch (StorageException exception)
            {
                return (Size: size, Failure: exception.Kind);
            }
        }));

        var winner = Assert.Single(outcomes, outcome => outcome.Failure is null);
        Assert.All(outcomes.Where(outcome => outcome.Failure is not null), outcome => Assert.Equal(StorageFailureKind.AlreadyExists, outcome.Failure));
        Assert.Equal(PatternStream.Bytes(winner.Size), await File.ReadAllBytesAsync(PhysicalPath(key.Value)));
        Assert.Single(AllFiles());
    }

    [Fact]
    public async Task ADirectoryOccupyingTheKeyBlocksTheWriteForBothPolicies()
    {
        await Put(new StorageObjectKey("a/b.bak"), PatternStream.Bytes(10));

        foreach (var policy in Enum.GetValues<StorageOverwritePolicy>())
        {
            await ExpectFailure(StorageFailureKind.AlreadyExists, () => Provider.PutAsync(new StorageObjectKey("a"), new PatternStream(10), new StoragePutOptions(policy), null, CancellationToken.None));
        }

        Assert.False(await Provider.ExistsAsync(new StorageObjectKey("a"), CancellationToken.None));
        Assert.False(await Provider.DeleteAsync(new StorageObjectKey("a"), CancellationToken.None));
        Assert.Single(AllFiles());
    }

    [Fact]
    public async Task AFileOccupyingADirectoryOfTheKeyIsAnIoFailure()
    {
        await Put(new StorageObjectKey("a"), PatternStream.Bytes(10));

        await ExpectFailure(StorageFailureKind.Io, () => Provider.PutAsync(new StorageObjectKey("a/b.bak"), new PatternStream(10), new StoragePutOptions(StorageOverwritePolicy.Fail), null, CancellationToken.None));

        Assert.False(await Provider.ExistsAsync(new StorageObjectKey("a/b.bak"), CancellationToken.None));
        Assert.Single(AllFiles());
    }

    [Fact]
    public async Task DeleteRemovesOnlyTheNamedObject()
    {
        await Put(new StorageObjectKey("d/one.bak"), PatternStream.Bytes(10));
        await Put(new StorageObjectKey("d/two.bak"), PatternStream.Bytes(20));

        Assert.True(await Provider.DeleteAsync(new StorageObjectKey("d/one.bak"), CancellationToken.None));

        Assert.False(await Provider.ExistsAsync(new StorageObjectKey("d/one.bak"), CancellationToken.None));
        Assert.True(await Provider.ExistsAsync(new StorageObjectKey("d/two.bak"), CancellationToken.None));
    }

    [Fact]
    public async Task MissingRootFailsEveryOperationWithoutCreatingIt()
    {
        var missing = NewTempPath();
        var provider = CreateProvider(missing);
        var key = new StorageObjectKey("x/y.bak");

        await ExpectFailure(StorageFailureKind.InvalidConfiguration, () => provider.PutAsync(key, new PatternStream(10), new StoragePutOptions(StorageOverwritePolicy.Fail), null, CancellationToken.None), missing);
        await ExpectFailure(StorageFailureKind.InvalidConfiguration, () => provider.ExistsAsync(key, CancellationToken.None), missing);
        await ExpectFailure(StorageFailureKind.InvalidConfiguration, () => provider.GetMetadataAsync(key, CancellationToken.None), missing);
        await ExpectFailure(StorageFailureKind.InvalidConfiguration, () => provider.DeleteAsync(key, CancellationToken.None), missing);
        await ExpectFailure(StorageFailureKind.InvalidConfiguration, () => provider.VerifyAsync(key, new StorageVerificationExpectation(1, PatternStream.ChecksumOf(1)), CancellationToken.None), missing);

        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task ProviderStartsWorkingWhenTheRootAppearsLater()
    {
        var late = NewTempPath();
        var provider = CreateProvider(late);
        var key = new StorageObjectKey("late.bak");
        await ExpectFailure(StorageFailureKind.InvalidConfiguration, () => provider.ExistsAsync(key, CancellationToken.None), late);

        Directory.CreateDirectory(late);
        var stored = await provider.PutAsync(key, new PatternStream(10), new StoragePutOptions(StorageOverwritePolicy.Fail), null, CancellationToken.None);

        Assert.Equal(10, stored.SizeBytes);
    }

    [SymlinkFact]
    public async Task DirectorySymlinkInsideTheStoreIsNeverFollowed()
    {
        var outside = NewOutsideDirectory();
        Directory.CreateDirectory(ObjectsDirectory);
        Directory.CreateSymbolicLink(Path.Combine(ObjectsDirectory, "escape"), outside);
        var key = new StorageObjectKey("escape/planted.bak");
        await File.WriteAllBytesAsync(Path.Combine(outside, "planted.bak"), [1, 2, 3]);

        await ExpectFailure(StorageFailureKind.AccessDenied, () => Provider.PutAsync(key, new PatternStream(10), new StoragePutOptions(StorageOverwritePolicy.Replace), null, CancellationToken.None), outside);
        await ExpectFailure(StorageFailureKind.AccessDenied, () => Provider.ExistsAsync(key, CancellationToken.None), outside);
        await ExpectFailure(StorageFailureKind.AccessDenied, () => Provider.GetMetadataAsync(key, CancellationToken.None), outside);
        await ExpectFailure(StorageFailureKind.AccessDenied, () => Provider.DeleteAsync(key, CancellationToken.None), outside);
        await ExpectFailure(StorageFailureKind.AccessDenied, () => Provider.VerifyAsync(key, new StorageVerificationExpectation(3, PatternStream.ChecksumOf(3)), CancellationToken.None), outside);

        Assert.Equal([Path.Combine(outside, "planted.bak")], Directory.GetFileSystemEntries(outside));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(outside, "planted.bak")));
    }

    [SymlinkFact]
    public async Task FileSymlinkAsTheTargetIsNeverFollowedOrReplaced()
    {
        var outside = NewOutsideDirectory();
        var secret = Path.Combine(outside, "secret.bin");
        await File.WriteAllBytesAsync(secret, [9, 9, 9]);
        Directory.CreateDirectory(ObjectsDirectory);
        File.CreateSymbolicLink(Path.Combine(ObjectsDirectory, "link.bak"), secret);
        var key = new StorageObjectKey("link.bak");

        await ExpectFailure(StorageFailureKind.AccessDenied, () => Provider.PutAsync(key, new PatternStream(10), new StoragePutOptions(StorageOverwritePolicy.Replace), null, CancellationToken.None), outside);
        await ExpectFailure(StorageFailureKind.AccessDenied, () => Provider.PutAsync(key, new PatternStream(10), new StoragePutOptions(StorageOverwritePolicy.Fail), null, CancellationToken.None), outside);
        await ExpectFailure(StorageFailureKind.AccessDenied, () => Provider.VerifyAsync(key, new StorageVerificationExpectation(3, PatternStream.ChecksumOf(3)), CancellationToken.None), outside);
        await ExpectFailure(StorageFailureKind.AccessDenied, () => Provider.DeleteAsync(key, CancellationToken.None), outside);

        Assert.Equal(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(secret));
        Assert.NotNull(new FileInfo(Path.Combine(ObjectsDirectory, "link.bak")).LinkTarget);
    }

    [SymlinkFact]
    public async Task DanglingSymlinkIsRefusedToo()
    {
        var outside = NewOutsideDirectory();
        Directory.CreateDirectory(ObjectsDirectory);
        File.CreateSymbolicLink(Path.Combine(ObjectsDirectory, "dangling.bak"), Path.Combine(outside, "not-there"));

        await ExpectFailure(StorageFailureKind.AccessDenied, () => Provider.PutAsync(new StorageObjectKey("dangling.bak"), new PatternStream(10), new StoragePutOptions(StorageOverwritePolicy.Replace), null, CancellationToken.None), outside);

        Assert.Empty(Directory.GetFileSystemEntries(outside));
    }

    [SymlinkFact]
    public async Task RootConfiguredAsASymlinkIsHonoured()
    {
        var real = NewOutsideDirectory();
        var linkedRoot = NewTempPath();
        Directory.CreateSymbolicLink(linkedRoot, real);
        var provider = CreateProvider(linkedRoot);

        await provider.PutAsync(new StorageObjectKey("via-link.bak"), new PatternStream(100), new StoragePutOptions(StorageOverwritePolicy.Fail), null, CancellationToken.None);

        Assert.True(await provider.ExistsAsync(new StorageObjectKey("via-link.bak"), CancellationToken.None));
        Assert.Single(Directory.EnumerateFiles(real, "*", SearchOption.AllDirectories));
    }

    protected string PhysicalPath(string key) => Path.Combine([ObjectsDirectory, .. key.Split('/')]);

    protected List<string> AllFiles() => [.. Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)];

    protected string NewOutsideDirectory()
    {
        var path = NewTempPath();
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Runs <paramref name="action"/>, expects a <see cref="StorageException"/> of <paramref name="kind"/>, and proves its message leaks no path of this test.</summary>
    protected async Task<StorageException> ExpectFailure(StorageFailureKind kind, Func<Task> action, params string[] extraPaths)
    {
        var failure = await Assert.ThrowsAsync<StorageException>(action);

        Assert.Equal(kind, failure.Kind);
        foreach (var path in extraPaths.Append(Root).Append(Path.GetTempPath()).Append("bp_test_"))
        {
            Assert.DoesNotContain(path, failure.Message, StringComparison.Ordinal);
        }

        return failure;
    }

    private static Func<CancellationToken, Task> Stall(TaskCompletionSource signal) =>
        async cancellationToken =>
        {
            signal.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        };

    private readonly List<string> _tempPaths = [];

    private string NewTempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "bp_test_" + Guid.NewGuid().ToString("N"));
        _tempPaths.Add(path);
        return path;
    }

    private Task<StorageObjectMetadata> Put(StorageObjectKey key, byte[] bytes) => Put(key, new MemoryStream(bytes));

    private Task<StorageObjectMetadata> Put(StorageObjectKey key, Stream content) =>
        Provider.PutAsync(key, content, new StoragePutOptions(StorageOverwritePolicy.Fail), null, CancellationToken.None);

    private sealed class Progress(Action<long> onReport) : IProgress<long>
    {
        private int _reports;

        public int Reports => _reports;

        public void Report(long value)
        {
            Interlocked.Increment(ref _reports);
            onReport(value);
        }
    }
}
