using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using static BackupPlatform.UnitTests.RestoreFixtures;

namespace BackupPlatform.UnitTests;

/// <summary>
/// The invariants of an isolated restore verification and of the cleanup it owes: the backend names the disposable database before anything exists, a lost agent
/// can never leave an unknown leftover, removal is leased, fenced and bounded, and a rollback copy is never removed without an operator.
/// </summary>
public sealed class RestoreVerificationDomainTests
{
    private static RestoreOperation Verification(string? name = null, RestoreMode mode = RestoreMode.NewDatabase, bool queue = true)
    {
        var operation = new RestoreOperation(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), Guid.NewGuid(), RestoreSourceAccess.CentralStorage, AgentId.New(), Target(),
            name ?? PlatformDatabaseNames.NewVerificationName(), mode, false, null, "key", "fingerprint", RequestedUserId, Now, RestorePurpose.Verification);
        if (queue) operation.QueueFirstAttempt(Guid.NewGuid(), Now);
        return operation;
    }

    private static RestoreCompletion Passed(bool removed) => new(RestoreStatus.Succeeded, null, RestoreTargetState.NotModified, "{}", removed);

    private static RestoreOperation Ended(bool removed, DateTimeOffset? at = null)
    {
        var operation = Verification();
        var token = Begin(operation);
        operation.Complete(token, at ?? Now.AddMinutes(1), Passed(removed));
        return operation;
    }

    [Theory]
    [InlineData("Sales")]
    [InlineData("bp_verify_short")]
    [InlineData("bp_verify_0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("bp_old_0123456789abcdef0123456789abcdef")]
    public void AVerificationOnlyEverCreatesAPlatformNamedDatabase(string name) =>
        Assert.Throws<ArgumentException>(() => Verification(name));

    [Fact]
    public void AVerificationCanNeverReplaceADatabaseOrRunThroughASupportSession()
    {
        Assert.Throws<ArgumentException>(() => Verification(mode: RestoreMode.ReplaceExistingDatabase));
        Assert.Throws<ArgumentException>(() => new RestoreOperation(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), Guid.NewGuid(), RestoreSourceAccess.CentralStorage, AgentId.New(),
            Target(), PlatformDatabaseNames.NewVerificationName(), RestoreMode.NewDatabase, false, null, "key", "fp", RequestedUserId, Now, RestorePurpose.Verification,
            supportSessionId: Guid.NewGuid()));
    }

    [Theory]
    [InlineData("bp_verify_0123456789abcdef0123456789abcdef")]
    [InlineData("bp_old_0123456789abcdef0123456789abcdef")]
    [InlineData("BP_OLD_anything")]
    [InlineData("bp_verify_")]
    public void AUserRestoreCanNeverTargetAReservedName(string name)
    {
        Assert.Throws<ArgumentException>(() => Operation(databaseName: name));
        Assert.Throws<ArgumentException>(() => RestoreOperation.ValidateDatabaseName(name, "postgres"));
    }

    [Fact]
    public void AVerificationOwnsExactlyItsOwnDatabaseAndOwesNothingWhileItRuns()
    {
        var operation = Verification();
        Assert.Equal(RestoreCleanupKind.VerificationDatabase, operation.Cleanup.Kind);
        Assert.Equal(operation.DatabaseName, operation.Cleanup.DatabaseName);
        Assert.Equal(RestoreCleanupState.Idle, operation.Cleanup.State);
        Assert.False(operation.TryLeaseCleanup(Guid.NewGuid(), Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void AVerificationAlwaysLeavesTheUsersDatabaseNotModified()
    {
        var operation = Verification();
        var token = Begin(operation);
        Assert.Throws<InvalidOperationException>(() => operation.Complete(token, Now.AddMinutes(1), new RestoreCompletion(RestoreStatus.Succeeded, null, RestoreTargetState.Online, "{}", true)));
        Assert.Throws<InvalidOperationException>(() => operation.Complete(token, Now.AddMinutes(1), new RestoreCompletion(RestoreStatus.Failed, RestoreFailureCodes.VerificationFailed, RestoreTargetState.PartiallyRestored, null, true)));
    }

    [Fact]
    public void ADroppedDisposableDatabaseIsCompletedAtOnceAndAnythingElseIsOwed()
    {
        var dropped = Ended(removed: true);
        Assert.Equal(RestoreCleanupState.Completed, dropped.Cleanup.State);
        Assert.False(dropped.TryLeaseCleanup(Guid.NewGuid(), Now.AddHours(1), TimeSpan.FromMinutes(5)));

        var owed = Ended(removed: false);
        Assert.Equal(RestoreCleanupState.Pending, owed.Cleanup.State);
        Assert.True(owed.Cleanup.CanLease(Now.AddMinutes(1)));
    }

    [Fact]
    public void AnAgentThatDiesMidVerificationLeavesARemovalOwedUnderTheNameTheBackendChose()
    {
        var operation = Verification();
        Begin(operation);
        Assert.True(operation.Expire(Now + Lease + TimeSpan.FromSeconds(1)));
        Assert.Equal(RestoreStatus.AgentLost, operation.Status);
        Assert.Equal(RestoreTargetState.NotModified, operation.TargetState);
        Assert.Equal(RestoreCleanupState.Pending, operation.Cleanup.State);
        Assert.Equal(operation.DatabaseName, operation.Cleanup.DatabaseName);
    }

    [Fact]
    public void AVerificationNobodyClaimedOrWithdrawnWhilePendingNeverCreatedAnything()
    {
        var withdrawn = Verification();
        withdrawn.RequestCancellation(Now);
        Assert.Equal(RestoreCleanupState.Completed, withdrawn.Cleanup.State);

        var unclaimed = Verification();
        Assert.True(unclaimed.ExpireUnclaimed(Now.AddHours(2), TimeSpan.FromHours(1)));
        Assert.Equal(RestoreCleanupState.Completed, unclaimed.Cleanup.State);
    }

    [Fact]
    public void ARemovalIsLeasedOnceFencedByItsTokenAndAStaleReportIsRefused()
    {
        var operation = Ended(removed: false);
        var first = Guid.NewGuid();
        var at = Now.AddMinutes(2);
        Assert.True(operation.TryLeaseCleanup(first, at, TimeSpan.FromMinutes(5)));
        Assert.Equal(1, operation.Cleanup.Attempts);
        Assert.False(operation.TryLeaseCleanup(Guid.NewGuid(), at.AddMinutes(1), TimeSpan.FromMinutes(5)));

        // The first agent stalls; the lease expires and the removal is offered again under a new token.
        var second = Guid.NewGuid();
        Assert.True(operation.TryLeaseCleanup(second, at.AddMinutes(6), TimeSpan.FromMinutes(5)));
        Assert.Equal(2, operation.Cleanup.Attempts);
        Assert.Throws<InvalidOperationException>(() => operation.ReportCleanup(first, true, null, at.AddMinutes(7)));
        Assert.Equal(RestoreCleanupState.Pending, operation.Cleanup.State);

        operation.ReportCleanup(second, true, null, at.AddMinutes(7));
        Assert.Equal(RestoreCleanupState.Completed, operation.Cleanup.State);
        // A late repeat, even a failed one, changes nothing.
        operation.ReportCleanup(first, false, "drop-failed", at.AddMinutes(8));
        Assert.Equal(RestoreCleanupState.Completed, operation.Cleanup.State);
    }

    [Fact]
    public void ALateSuccessIsAcceptedUntilSomebodyElseLeasedTheRemoval()
    {
        var operation = Ended(removed: false);
        var token = Guid.NewGuid();
        Assert.True(operation.TryLeaseCleanup(token, Now.AddMinutes(2), TimeSpan.FromMinutes(1)));
        operation.ReportCleanup(token, true, null, Now.AddMinutes(30));
        Assert.Equal(RestoreCleanupState.Completed, operation.Cleanup.State);
    }

    [Fact]
    public void AFailedRemovalBacksOffExponentiallyThenParksForAnOperator()
    {
        var operation = Ended(removed: false);
        var clock = Now.AddMinutes(2);
        for (var attempt = 1; attempt <= RestoreCleanup.MaxAutomaticAttempts; attempt++)
        {
            Assert.True(operation.TryLeaseCleanup(Guid.NewGuid(), clock, TimeSpan.FromMinutes(5)), $"attempt {attempt}");
            var token = operation.Cleanup.LeaseToken!.Value;
            operation.ReportCleanup(token, false, "drop-failed", clock);
            Assert.Equal("drop-failed", operation.Cleanup.LastFailureCode);
            if (attempt < RestoreCleanup.MaxAutomaticAttempts)
            {
                Assert.Equal(clock + RestoreCleanup.Backoff(attempt), operation.Cleanup.NextAttemptAt);
                Assert.False(operation.TryLeaseCleanup(Guid.NewGuid(), clock, TimeSpan.FromMinutes(5)));
                clock = operation.Cleanup.NextAttemptAt!.Value;
            }
        }

        Assert.Null(operation.Cleanup.NextAttemptAt);
        Assert.True(operation.Cleanup.NeedsAttention(clock));
        Assert.False(operation.TryLeaseCleanup(Guid.NewGuid(), clock.AddDays(1), TimeSpan.FromMinutes(5)));

        Assert.True(operation.RequestCleanup(clock.AddDays(1), RequestedUserId));
        Assert.Equal(0, operation.Cleanup.Attempts);
        Assert.False(operation.Cleanup.NeedsAttention(clock.AddDays(1)));
        Assert.True(operation.TryLeaseCleanup(Guid.NewGuid(), clock.AddDays(1), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void BackoffGrowsAndIsCapped()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), RestoreCleanup.Backoff(1));
        Assert.Equal(TimeSpan.FromMinutes(2), RestoreCleanup.Backoff(2));
        Assert.Equal(TimeSpan.FromMinutes(32), RestoreCleanup.Backoff(6));
        Assert.Equal(RestoreCleanup.MaxBackoff, RestoreCleanup.Backoff(50));
    }

    [Fact]
    public void AnOperatorCannotStackARequestOnALiveLeaseOrOnAnOperationThatOwesNothing()
    {
        var operation = Ended(removed: false);
        Assert.True(operation.TryLeaseCleanup(Guid.NewGuid(), Now.AddMinutes(2), TimeSpan.FromMinutes(5)));
        Assert.False(operation.RequestCleanup(Now.AddMinutes(3), RequestedUserId));
        Assert.False(Ended(removed: true).RequestCleanup(Now.AddMinutes(3), RequestedUserId));
        Assert.False(Queued().RequestCleanup(Now, RequestedUserId));
    }

    // ---- the rollback copy of a replace restore -----------------------------------------------------------------------------

    private const string Rollback = "bp_old_0123456789abcdef0123456789abcdef";

    private static RestoreOperation SucceededReplace(string? retained)
    {
        var operation = Queued(RestoreMode.ReplaceExistingDatabase);
        var token = Begin(operation);
        operation.Complete(token, Now.AddMinutes(1), new RestoreCompletion(RestoreStatus.Succeeded, null, RestoreTargetState.Online, null, RetainedDatabaseName: retained));
        return operation;
    }

    [Fact]
    public void AReplaceRestoreKeepsItsRollbackCopyUntilAnOperatorAsks()
    {
        var operation = SucceededReplace(Rollback);
        Assert.Equal(RestoreCleanupKind.RollbackCopy, operation.Cleanup.Kind);
        Assert.Equal(Rollback, operation.Cleanup.DatabaseName);
        Assert.Equal(RestoreCleanupState.Retained, operation.Cleanup.State);
        // Nothing leases a retained copy, however long it stays.
        Assert.False(operation.TryLeaseCleanup(Guid.NewGuid(), Now.AddYears(5), TimeSpan.FromMinutes(5)));

        Assert.True(operation.RequestCleanup(Now.AddDays(8), RequestedUserId));
        Assert.Equal(RestoreCleanupState.Pending, operation.Cleanup.State);
        Assert.Equal(RequestedUserId, operation.Cleanup.RequestedByUserId);
        Assert.True(operation.TryLeaseCleanup(Guid.NewGuid(), Now.AddDays(8), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void OnlyASuccessfulReplaceRestoreCanRetainAPlatformRollbackName()
    {
        Assert.Equal(RestoreCleanupState.Idle, SucceededReplace(null).Cleanup.State);
        Assert.Throws<ArgumentException>(() => SucceededReplace("bp_old_notahexname"));
        Assert.Throws<ArgumentException>(() => SucceededReplace("Sales"));

        var newDatabase = Queued();
        var token = Begin(newDatabase);
        Assert.Throws<InvalidOperationException>(() => newDatabase.Complete(token, Now.AddMinutes(1), new RestoreCompletion(RestoreStatus.Succeeded, null, RestoreTargetState.Online, null, RetainedDatabaseName: Rollback)));
    }
}
