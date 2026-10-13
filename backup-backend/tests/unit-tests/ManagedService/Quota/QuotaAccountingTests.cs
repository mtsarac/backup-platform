using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Storage;
using BackupPlatform.ManagedService.Quota;

namespace BackupPlatform.UnitTests.ManagedService.Quota;

public sealed class QuotaAccountingTests
{
    private const long Gb = 1_000_000_000;
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The facts of the connection a test replica lives on. Ownership and scope are plain data of the connection: a central connection on the backend is
    /// platform-managed (label "managed"), everything else tenant-managed (label "tenant") — the same properties the system default connections have.
    /// </summary>
    private static ReplicaStorageFacts Facts(string provider, BackupStorageExecutionLocation location = BackupStorageExecutionLocation.Backend)
    {
        var platform = provider == "central" && location == BackupStorageExecutionLocation.Backend;
        return new(provider, platform ? StorageOwnership.PlatformManaged : StorageOwnership.TenantManaged, platform ? "managed" : "tenant", Guid.NewGuid());
    }

    private static ReplicaFact Replica(
        Guid artifact, string provider = "central", BackupReplicaStatus status = BackupReplicaStatus.Available, long size = 2 * Gb,
        BackupStorageExecutionLocation location = BackupStorageExecutionLocation.Backend) =>
        new(artifact, size, size, Facts(provider, location), status);

    private static long Total(QuotaAccountingMode mode, QuotaScopeRule scope, params ReplicaFact[] replicas)
    {
        var accumulator = new QuotaUsageAccumulator(mode, scope);
        foreach (var replica in replicas)
        {
            accumulator.Add(replica);
        }
        return accumulator.TotalBytes;
    }

    private static QuotaPolicy Policy(QuotaAccountingMode mode, QuotaScopeRule scope) =>
        new(QuotaPolicyId.New(), "test", "Test", mode, scope, QuotaEnforcementMode.BlockNewBackups, 80, 90, true, Now);

    private static QuotaScopeRule PlatformOnly { get; } = QuotaScopeRule.FromJson("""[{"kind":"ownershipType","values":["PlatformManaged"]}]""");

    private static ReplicaFact[] ThreeCopiesOfOneArtifact(Guid artifact) =>
    [
        Replica(artifact, "central"),
        Replica(artifact, "googledrive"),
        Replica(artifact, "sftp")
    ];

    [Fact]
    public void CanonicalExampleCountsOnceLogicallyAndThreeTimesPhysically()
    {
        var replicas = ThreeCopiesOfOneArtifact(Guid.NewGuid());
        Assert.Equal(2 * Gb, Total(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All, replicas));
        Assert.Equal(6 * Gb, Total(QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeRule.All, replicas));
    }

    [Fact]
    public void LogicalCountsAnArtifactOnceWhateverTheNumberOfCopies()
    {
        var artifact = Guid.NewGuid();
        var copies = Enumerable.Range(0, 25).Select(index => Replica(artifact, $"p{index}")).ToArray();
        Assert.Equal(2 * Gb, Total(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All, copies));
        Assert.Equal(50 * Gb, Total(QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeRule.All, copies));
    }

    [Fact]
    public void LogicalDedupesPerArtifactEvenWhenReplicasOfDifferentArtifactsInterleave()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var interleaved = new[]
        {
            Replica(a, "central", size: 3 * Gb), Replica(b, "central", size: 5 * Gb), Replica(a, "sftp", size: 3 * Gb),
            Replica(b, "sftp", size: 5 * Gb), Replica(a, "googledrive", size: 3 * Gb)
        };
        Assert.Equal(8 * Gb, Total(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All, interleaved));
        Assert.Equal(19 * Gb, Total(QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeRule.All, interleaved));
    }

    [Theory]
    [InlineData(BackupReplicaStatus.Available)]
    [InlineData(BackupReplicaStatus.DeletePending)]
    [InlineData(BackupReplicaStatus.Deleting)]
    [InlineData(BackupReplicaStatus.DeleteFailed)]
    public void EveryStatusExceptDeletedStillOccupiesStorage(BackupReplicaStatus status)
    {
        var replica = Replica(Guid.NewGuid(), status: status);
        Assert.Equal(2 * Gb, Total(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All, replica));
        Assert.Equal(2 * Gb, Total(QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeRule.All, replica));
    }

    [Theory]
    [InlineData(QuotaAccountingMode.LogicalArtifactBytes)]
    [InlineData(QuotaAccountingMode.PhysicalReplicaBytes)]
    public void ADeletedReplicaCountsNothing(QuotaAccountingMode mode) =>
        Assert.Equal(0, Total(mode, QuotaScopeRule.All, Replica(Guid.NewGuid(), status: BackupReplicaStatus.Deleted)));

    [Fact]
    public void AnArtifactStaysCountedWhileAnyCopyOccupiesStorageAndDropsOnlyWhenTheLastIsDeleted()
    {
        var artifact = Guid.NewGuid();
        var oneGone = new[] { Replica(artifact, "central", BackupReplicaStatus.Deleted), Replica(artifact, "sftp", BackupReplicaStatus.DeletePending) };
        Assert.Equal(2 * Gb, Total(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All, oneGone));
        Assert.Equal(2 * Gb, Total(QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeRule.All, oneGone));

        var allGone = new[] { Replica(artifact, "central", BackupReplicaStatus.Deleted), Replica(artifact, "sftp", BackupReplicaStatus.Deleted) };
        Assert.Equal(0, Total(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All, allGone));
    }

    [Fact]
    public void ADeletedReplicaDoesNotUseUpTheOncePerArtifactSlot()
    {
        var artifact = Guid.NewGuid();
        var deletedFirst = new[] { Replica(artifact, "central", BackupReplicaStatus.Deleted), Replica(artifact, "sftp") };
        Assert.Equal(2 * Gb, Total(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All, deletedFirst));
    }

    [Fact]
    public void AnOutOfScopeReplicaDoesNotUseUpTheOncePerArtifactSlot()
    {
        var artifact = Guid.NewGuid();
        var replicas = new[] { Replica(artifact, "sftp"), Replica(artifact, "central") };
        Assert.Equal(2 * Gb, Total(QuotaAccountingMode.LogicalArtifactBytes, PlatformOnly, replicas));
    }

    [Fact]
    public void OwnershipScopeCountsPlatformManagedStorageOnly()
    {
        var artifact = Guid.NewGuid();
        var replicas = new[]
        {
            Replica(artifact, "central"),
            Replica(artifact, "local", location: BackupStorageExecutionLocation.Agent),
            Replica(artifact, "googledrive")
        };
        Assert.Equal(2 * Gb, Total(QuotaAccountingMode.PhysicalReplicaBytes, PlatformOnly, replicas));
        Assert.Equal(2 * Gb, Total(QuotaAccountingMode.LogicalArtifactBytes, PlatformOnly, replicas));

        var tenantOnly = QuotaScopeRule.FromJson("""[{"kind":"ownershipType","values":["TenantManaged"]}]""");
        Assert.Equal(4 * Gb, Total(QuotaAccountingMode.PhysicalReplicaBytes, tenantOnly, replicas));
        Assert.Equal(2 * Gb, Total(QuotaAccountingMode.LogicalArtifactBytes, tenantOnly, replicas));
    }

    [Theory]
    [InlineData(QuotaAccountingMode.LogicalArtifactBytes)]
    [InlineData(QuotaAccountingMode.PhysicalReplicaBytes)]
    public void AnArtifactOnlyOnTenantManagedReplicasCountsZeroUnderAPlatformManagedPolicy(QuotaAccountingMode mode)
    {
        var artifact = Guid.NewGuid();
        var replicas = new[]
        {
            Replica(artifact, "local", location: BackupStorageExecutionLocation.Agent),
            Replica(artifact, "sftp"),
            Replica(artifact, "central", location: BackupStorageExecutionLocation.Agent)
        };
        Assert.Equal(0, Total(mode, PlatformOnly, replicas));
    }

    [Fact]
    public void ProviderScopeCountsTheListedProvidersOnly()
    {
        var scope = QuotaScopeRule.FromJson("""[{"kind":"providerType","values":["sftp","googledrive"]}]""");
        var replicas = ThreeCopiesOfOneArtifact(Guid.NewGuid());
        Assert.Equal(4 * Gb, Total(QuotaAccountingMode.PhysicalReplicaBytes, scope, replicas));
        Assert.Equal(2 * Gb, Total(QuotaAccountingMode.LogicalArtifactBytes, scope, replicas));
        Assert.Equal(0, Total(QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeRule.FromJson("""[{"kind":"providerType","values":["local"]}]"""), replicas));
    }

    [Fact]
    public void PhysicalUsesEachReplicasOwnSize()
    {
        var artifact = Guid.NewGuid();
        var replicas = new[]
        {
            new ReplicaFact(artifact, 10, 10, Facts("central"), BackupReplicaStatus.Available),
            new ReplicaFact(artifact, 10, 7, Facts("sftp"), BackupReplicaStatus.Available)
        };
        Assert.Equal(17, Total(QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeRule.All, replicas));
        Assert.Equal(10, Total(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All, replicas));
    }

    [Theory]
    [InlineData(QuotaAccountingMode.LogicalArtifactBytes)]
    [InlineData(QuotaAccountingMode.PhysicalReplicaBytes)]
    public void ZeroAndNegativeSizesNeverReduceTheTotal(QuotaAccountingMode mode)
    {
        var replicas = new[] { Replica(Guid.NewGuid(), size: 5), Replica(Guid.NewGuid(), "sftp", size: -100), Replica(Guid.NewGuid(), "sftp", size: 0) };
        Assert.Equal(5, Total(mode, QuotaScopeRule.All, replicas));
    }

    [Theory]
    [InlineData(QuotaAccountingMode.LogicalArtifactBytes)]
    [InlineData(QuotaAccountingMode.PhysicalReplicaBytes)]
    public void TotalsSaturateAtLongMaxValueInsteadOfOverflowing(QuotaAccountingMode mode)
    {
        var replicas = new[] { Replica(Guid.NewGuid(), size: long.MaxValue), Replica(Guid.NewGuid(), size: long.MaxValue), Replica(Guid.NewGuid(), size: 1) };
        Assert.Equal(long.MaxValue, Total(mode, QuotaScopeRule.All, replicas));
    }

    [Fact]
    public void ANearlyFullTotalSaturatesOnTheNextByte()
    {
        var replicas = new[] { Replica(Guid.NewGuid(), size: long.MaxValue - 1), Replica(Guid.NewGuid(), size: 1), Replica(Guid.NewGuid(), size: 1) };
        Assert.Equal(long.MaxValue, Total(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All, replicas));
    }

    [Fact]
    public void AnUndefinedAccountingModeIsRefusedNotCountedAsZero()
    {
        var accumulator = new QuotaUsageAccumulator((QuotaAccountingMode)0, QuotaScopeRule.All);
        Assert.Throws<ArgumentOutOfRangeException>(() => accumulator.Add(Replica(Guid.NewGuid())));
    }

    [Fact]
    public void ContributionIsWhatOneArtifactAddsUnderThePolicy()
    {
        var replicas = ThreeCopiesOfOneArtifact(Guid.NewGuid());
        Assert.Equal(2 * Gb, QuotaAccounting.Contribution(Policy(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All), replicas));
        Assert.Equal(6 * Gb, QuotaAccounting.Contribution(Policy(QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeRule.All), replicas));
        Assert.Equal(2 * Gb, QuotaAccounting.Contribution(Policy(QuotaAccountingMode.PhysicalReplicaBytes, PlatformOnly), replicas));
        Assert.Equal(0, QuotaAccounting.Contribution(Policy(QuotaAccountingMode.LogicalArtifactBytes, PlatformOnly), replicas.Skip(1)));
        Assert.Equal(0, QuotaAccounting.Contribution(Policy(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All), []));
    }

    [Fact]
    public void NewAccumulatorFollowsThePolicyAndRejectsNull()
    {
        var accumulator = QuotaAccounting.NewAccumulator(Policy(QuotaAccountingMode.PhysicalReplicaBytes, PlatformOnly));
        foreach (var replica in ThreeCopiesOfOneArtifact(Guid.NewGuid()))
        {
            accumulator.Add(replica);
        }
        Assert.Equal(2 * Gb, accumulator.TotalBytes);
        Assert.Throws<ArgumentNullException>(() => QuotaAccounting.NewAccumulator(null!));
    }
}
