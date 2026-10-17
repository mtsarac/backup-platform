using BackupPlatform.Domain.Restores;
using static BackupPlatform.UnitTests.RestoreFixtures;

namespace BackupPlatform.UnitTests;

/// <summary>
/// The rules an agent's completion must satisfy, exercised through <see cref="RestoreOperation.Complete"/> (the validation itself is internal), plus the closed
/// vocabularies of failure codes and stages.
/// </summary>
public sealed class RestoreCompletionTests
{
    private static readonly string[] ReportableCodes =
    [
        "ReplicaUnavailable", "ArtifactTransferFailed", "ChecksumMismatch", "StorageUnavailable",
        "BackupInvalid", "BackupTypeUnsupported", "IncompatibleVersion", "FileMappingFailed",
        "PreCheckFailed", "TargetDatabaseExists", "TargetDatabaseNotFound", "TargetInUse", "TargetProtected", "InsufficientDiskSpace",
        "SqlRestoreFailed", "DatabaseUnavailable", "OperationTimedOut", "AuthenticationFailed", "PermissionDenied",
        "InvalidConfiguration", "UnsupportedCapability", "StagingInvalid", "InternalError", "VerificationFailed"
    ];

    public static TheoryData<string> AgentReportableCodes
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var code in ReportableCodes) data.Add(code);
            return data;
        }
    }

    public static TheoryData<string?> NonAgentReportableCodes => new()
    {
        null, RestoreFailureCodes.Cancelled, RestoreFailureCodes.AgentLost, RestoreFailureCodes.NotClaimed, "", "bogus", "internalerror", "SQLRESTOREFAILED", " SqlRestoreFailed"
    };

    public static TheoryData<RestoreTargetState> NonOnlineStates => new()
    {
        RestoreTargetState.NotModified, RestoreTargetState.PartiallyRestored, RestoreTargetState.Unknown
    };

    private static void Apply(RestoreCompletion completion, bool started = true)
    {
        var operation = Queued();
        var token = started ? Begin(operation) : Claim(operation);
        operation.Complete(token, Now.AddSeconds(1), completion);
    }

    private static RestoreOperation CompleteRunning(RestoreCompletion completion)
    {
        var operation = Running(out var token);
        operation.Complete(token, Now.AddSeconds(1), completion);
        return operation;
    }

    // ---- Succeeded ----

    [Fact]
    public void SuccessWithNoCodeAndAnOnlineTargetIsAccepted()
    {
        var operation = CompleteRunning(new RestoreCompletion(RestoreStatus.Succeeded, null, RestoreTargetState.Online, null));
        Assert.Equal(RestoreStatus.Succeeded, operation.Status);
        Assert.Equal(RestoreTargetState.Online, operation.TargetState);
    }

    [Theory]
    [MemberData(nameof(NonOnlineStates))]
    public void SuccessNeverLeavesAnythingButAnOnlineTarget(RestoreTargetState state)
    {
        var operation = Queued();
        var token = Begin(operation);
        Assert.Throws<InvalidOperationException>(() => operation.Complete(token, Now.AddSeconds(1), new RestoreCompletion(RestoreStatus.Succeeded, null, state, null)));
        Assert.Equal(RestoreStatus.Running, operation.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData(RestoreFailureCodes.SqlRestoreFailed)]
    [InlineData(RestoreFailureCodes.Cancelled)]
    public void SuccessCarriesNoFailureCodeAtAll(string code)
    {
        var operation = Queued();
        var token = Begin(operation);
        Assert.Throws<InvalidOperationException>(() => operation.Complete(token, Now.AddSeconds(1), new RestoreCompletion(RestoreStatus.Succeeded, code, RestoreTargetState.Online, null)));
        Assert.Equal(RestoreStatus.Running, operation.Status);
    }

    [Fact]
    public void SuccessIsRefusedForAnAttemptThatNeverStarted()
    {
        var operation = Queued();
        var token = Claim(operation);
        Assert.Throws<InvalidOperationException>(() => operation.Complete(token, Now.AddSeconds(1), Success()));
        Assert.Equal(RestoreStatus.Leased, operation.Status);
    }

    // ---- Failed ----

    [Theory]
    [MemberData(nameof(AgentReportableCodes))]
    public void EveryAgentReportableCodeCanEndAFailedRestoreWithAnyNonOnlineState(string code)
    {
        foreach (var state in new[] { RestoreTargetState.NotModified, RestoreTargetState.PartiallyRestored, RestoreTargetState.Unknown })
        {
            var operation = CompleteRunning(new RestoreCompletion(RestoreStatus.Failed, code, state, null));
            Assert.Equal(RestoreStatus.Failed, operation.Status);
            Assert.Equal(code, operation.FailureCode);
            Assert.Equal(state, operation.TargetState);
        }
    }

    [Theory]
    [MemberData(nameof(AgentReportableCodes))]
    public void AFailedRestoreNeverClaimsAnOnlineTarget(string code)
    {
        var operation = Queued();
        var token = Begin(operation);
        Assert.Throws<ArgumentException>(() => operation.Complete(token, Now.AddSeconds(1), new RestoreCompletion(RestoreStatus.Failed, code, RestoreTargetState.Online, null)));
        Assert.Equal(RestoreStatus.Running, operation.Status);
    }

    [Theory]
    [MemberData(nameof(NonAgentReportableCodes))]
    public void AFailedRestoreNeedsAnAgentReportableCode(string? code)
    {
        var operation = Queued();
        var token = Begin(operation);
        Assert.Throws<ArgumentException>(() => operation.Complete(token, Now.AddSeconds(1), new RestoreCompletion(RestoreStatus.Failed, code, RestoreTargetState.NotModified, null)));
        Assert.Equal(RestoreStatus.Running, operation.Status);
    }

    [Fact]
    public void AFailureCanEndAnAttemptThatWasOnlyLeased() => Apply(new RestoreCompletion(RestoreStatus.Failed, RestoreFailureCodes.StagingInvalid, RestoreTargetState.NotModified, null), started: false);

    // ---- Cancelled ----

    [Theory]
    [MemberData(nameof(NonOnlineStates))]
    public void ACancellationWithoutACodeOrWithTheCancelledCodeIsAcceptedForEveryNonOnlineState(RestoreTargetState state)
    {
        var withoutCode = CompleteRunning(new RestoreCompletion(RestoreStatus.Cancelled, null, state, null));
        Assert.Equal(RestoreStatus.Cancelled, withoutCode.Status);
        Assert.Null(withoutCode.FailureCode);
        Assert.Equal(state, withoutCode.TargetState);

        var withCode = CompleteRunning(new RestoreCompletion(RestoreStatus.Cancelled, RestoreFailureCodes.Cancelled, state, null));
        Assert.Equal(RestoreStatus.Cancelled, withCode.Status);
        Assert.Equal(RestoreFailureCodes.Cancelled, withCode.FailureCode);
        Assert.Equal(state, withCode.TargetState);
    }

    [Fact]
    public void ACancellationNeverClaimsAnOnlineTarget()
    {
        foreach (var code in new string?[] { null, RestoreFailureCodes.Cancelled })
        {
            var operation = Queued();
            var token = Begin(operation);
            Assert.Throws<ArgumentException>(() => operation.Complete(token, Now.AddSeconds(1), new RestoreCompletion(RestoreStatus.Cancelled, code, RestoreTargetState.Online, null)));
            Assert.Equal(RestoreStatus.Running, operation.Status);
        }
    }

    [Theory]
    [InlineData(RestoreFailureCodes.SqlRestoreFailed)]
    [InlineData(RestoreFailureCodes.AgentLost)]
    [InlineData(RestoreFailureCodes.NotClaimed)]
    [InlineData("")]
    [InlineData("cancelled")]
    public void ACancellationOnlyAcceptsTheCancelledCode(string code)
    {
        var operation = Queued();
        var token = Begin(operation);
        Assert.Throws<ArgumentException>(() => operation.Complete(token, Now.AddSeconds(1), new RestoreCompletion(RestoreStatus.Cancelled, code, RestoreTargetState.NotModified, null)));
        Assert.Equal(RestoreStatus.Running, operation.Status);
    }

    // ---- status and target state ----

    [Theory]
    [InlineData(RestoreStatus.Pending)]
    [InlineData(RestoreStatus.Leased)]
    [InlineData(RestoreStatus.Running)]
    [InlineData(RestoreStatus.AgentLost)]
    [InlineData((RestoreStatus)0)]
    [InlineData((RestoreStatus)42)]
    public void OnlyTheThreeAgentReportedOutcomesAreTerminalCompletions(RestoreStatus status)
    {
        var operation = Queued();
        var token = Begin(operation);
        Assert.Throws<ArgumentException>(() => operation.Complete(token, Now.AddSeconds(1), new RestoreCompletion(status, null, RestoreTargetState.NotModified, null)));
        Assert.Equal(RestoreStatus.Running, operation.Status);
        Assert.True(operation.Pin.IsActive);
    }

    [Theory]
    [InlineData((RestoreTargetState)0)]
    [InlineData((RestoreTargetState)5)]
    [InlineData((RestoreTargetState)(-1))]
    public void AnUndefinedTargetStateIsRefusedForEveryOutcome(RestoreTargetState state)
    {
        foreach (var completion in new[]
        {
            new RestoreCompletion(RestoreStatus.Succeeded, null, state, null),
            new RestoreCompletion(RestoreStatus.Failed, RestoreFailureCodes.InternalError, state, null),
            new RestoreCompletion(RestoreStatus.Cancelled, null, state, null)
        })
        {
            var operation = Queued();
            var token = Begin(operation);
            Assert.Throws<ArgumentException>(() => operation.Complete(token, Now.AddSeconds(1), completion));
            Assert.Equal(RestoreStatus.Running, operation.Status);
        }
    }

    // ---- RestoreFailureCodes ----

    [Theory]
    [MemberData(nameof(AgentReportableCodes))]
    public void EveryCodeAnAgentMayReportIsReportable(string code) => Assert.True(RestoreFailureCodes.IsAgentReportable(code));

    [Theory]
    [MemberData(nameof(NonAgentReportableCodes))]
    public void BackendOnlyOutcomesAndUnknownTextAreNotReportable(string? code) => Assert.False(RestoreFailureCodes.IsAgentReportable(code));

    [Fact]
    public void TheReportableCollectionIsExactlyTheDocumentedClosedSet()
    {
        Assert.Equal(ReportableCodes.Length, RestoreFailureCodes.AgentReportable.Count);
        Assert.Equal(ReportableCodes.OrderBy(code => code, StringComparer.Ordinal), RestoreFailureCodes.AgentReportable.OrderBy(code => code, StringComparer.Ordinal));
    }

    [Fact]
    public void FailureCodeValuesEqualTheirNames()
    {
        Assert.Equal("ReplicaUnavailable", RestoreFailureCodes.ReplicaUnavailable);
        Assert.Equal("Cancelled", RestoreFailureCodes.Cancelled);
        Assert.Equal("AgentLost", RestoreFailureCodes.AgentLost);
        Assert.Equal("NotClaimed", RestoreFailureCodes.NotClaimed);
        Assert.Equal("InternalError", RestoreFailureCodes.InternalError);
    }

    // ---- RestoreStages ----

    [Fact]
    public void TheStagesAreOrderedAndEndWithTheTerminalStages() =>
        Assert.Equal(new[] { "Preparing", "DownloadingArtifact", "VerifyingChecksum", "InspectingBackup", "PreparingTarget", "Restoring", "Finalizing", "Verifying", "CleaningUp", "Completed", "Failed", "Cancelled" },
            RestoreStages.All.ToArray());

    [Fact]
    public void EveryStageIsKnown() => Assert.All(RestoreStages.All, stage => Assert.True(RestoreStages.IsKnown(stage)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Nope")]
    [InlineData("restoring")]
    [InlineData("RESTORING")]
    [InlineData(" Restoring")]
    public void UnknownStagesAreNotKnown(string? stage) => Assert.False(RestoreStages.IsKnown(stage));

}
