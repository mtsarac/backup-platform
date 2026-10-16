using BackupPlatform.Agent.Core.Restore;
using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.UnitTests.RestorePipeline;

/// <summary>
/// The agent pipeline of an isolated verification: restore into the disposable database, prove it, and always drop it, whichever way the run ended. The one thing no
/// test here may allow is a database that the run created and nobody dropped without the result saying so.
/// </summary>
public sealed class VerificationPipelineTests : IDisposable
{
    private readonly RestorePipelineHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task APassingVerificationRestoresVerifiesAndDropsTheDisposableDatabaseBeforeItReportsSuccess()
    {
        var request = _h.VerificationRequest();
        var result = await _h.RunAsync(request);

        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
        Assert.Equal(DatabaseRestoreMode.IsolatedVerification, Assert.Single(_h.Provider.Executions).Mode);
        Assert.Equal(request.DatabaseName, Assert.Single(_h.Provider.Verifications).DatabaseName);
        var drop = Assert.Single(_h.Provider.Drops);
        Assert.Equal((request.DatabaseName, DatabaseDropKind.VerificationDatabase, false), (drop.Request.DatabaseName, drop.Request.Kind, drop.TokenCancelled));
        // The user's database is never touched, and what happened to the disposable one is stated.
        Assert.Equal(DatabaseRestoreTargetState.NotModified, result.TargetState);
        Assert.Equal(VerificationCleanup.Dropped, result.Verification!.Cleanup);
        Assert.True(result.Verification.Report!.Passed);
        Assert.Equal(
            [
                RestoreExecutionStage.Preparing, RestoreExecutionStage.DownloadingArtifact, RestoreExecutionStage.VerifyingChecksum, RestoreExecutionStage.InspectingBackup,
                RestoreExecutionStage.PreparingTarget, RestoreExecutionStage.Restoring, RestoreExecutionStage.Finalizing, RestoreExecutionStage.Verifying,
                RestoreExecutionStage.CleaningUp, RestoreExecutionStage.Completed
            ],
            result.Stages.Select(record => record.Stage));
        Assert.Empty(_h.StagedFiles);
    }

    [Fact]
    public async Task AFailedIntegrityCheckFailsTheRestoreButStillDropsTheDatabase()
    {
        _h.Provider.Verify = (_, _) => Task.FromResult(new RestoreVerificationReport(
            [new RestoreVerificationCheck(RestoreVerificationCheck.DatabaseOnline, true, "ok", "The restored database is online."),
             new RestoreVerificationCheck(RestoreVerificationCheck.Integrity, false, "integrity-check-failed", "The integrity check reported errors.")]));

        var result = await _h.RunAsync(_h.VerificationRequest());

        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Equal(RestoreAgentFailureCodes.VerificationFailed, result.Failure!.Code);
        Assert.Equal(RestoreExecutionStage.Verifying, result.Failure.Stage);
        Assert.Equal("integrity-check-failed", result.Failure.ReasonCode);
        Assert.Single(_h.Provider.Drops);
        Assert.Equal(VerificationCleanup.Dropped, result.Verification!.Cleanup);
        Assert.False(result.Verification.Report!.Passed);
    }

    [Fact]
    public async Task AVerificationTimeoutIsReportedAsATimeoutNotAnIntegrityFailure()
    {
        _h.Provider.Verify = (_, _) => Task.FromResult(new RestoreVerificationReport(
            [new RestoreVerificationCheck(RestoreVerificationCheck.DataReadable, false, RestorePipelineReasons.Timeout, "Reading the tables timed out.")]));

        var result = await _h.RunAsync(_h.VerificationRequest());

        Assert.Equal(RestoreAgentFailureCodes.OperationTimedOut, result.Failure!.Code);
    }

    [Fact]
    public async Task ACancelledRestoreStillDropsWhatItCreatedOnATokenThatIsNotCancelled()
    {
        using var cancel = new CancellationTokenSource();
        _h.Provider.Execute = async (request, _, token) =>
        {
            await cancel.CancelAsync();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        };

        var result = await _h.RunAsync(_h.VerificationRequest(), cancel.Token);

        Assert.Equal(DatabaseRestoreStatus.Cancelled, result.Status);
        var drop = Assert.Single(_h.Provider.Drops);
        Assert.False(drop.TokenCancelled);
        Assert.Equal(VerificationCleanup.Dropped, result.Verification!.Cleanup);
        Assert.Equal(DatabaseRestoreTargetState.NotModified, result.TargetState);
        Assert.Empty(_h.Provider.Verifications);
    }

    [Fact]
    public async Task ARestoreThatFailsMidwayLeavesNothingBehind()
    {
        _h.Provider.Execute = (request, _, _) => Task.FromResult(RestorePipelineHarness.NotSucceeded(request.Plan,
            new RestoreFailure(RestoreFailureKind.SqlRestoreFailed, RestoreExecutionStage.Restoring, "sql-restore-failed", "The engine could not complete the restore."),
            DatabaseRestoreTargetState.PartiallyRestored));

        var result = await _h.RunAsync(_h.VerificationRequest());

        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Single(_h.Provider.Drops);
        Assert.Empty(_h.Provider.Verifications);
        Assert.Equal(VerificationCleanup.Dropped, result.Verification!.Cleanup);
    }

    [Fact]
    public async Task AFailureBeforeTheEngineCreatedAnythingDropsNothing()
    {
        _h.Provider.PreCheck = RestorePipelineHarness.Report(RestorePipelineHarness.Failed(RestoreReasonCodes.InsufficientDiskSpace));

        var result = await _h.RunAsync(_h.VerificationRequest());

        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Equal(RestoreAgentFailureCodes.InsufficientDiskSpace, result.Failure!.Code);
        Assert.Empty(_h.Provider.Drops);
        Assert.Equal(VerificationCleanup.NotCreated, result.Verification!.Cleanup);
    }

    [Fact]
    public async Task ADropThatFailsOrThrowsIsReportedSoTheBackendKeepsTheRemovalOwed()
    {
        _h.Provider.Drop = (_, _) => Task.FromResult(new DatabaseDropResult(DatabaseDropOutcome.Failed, "database-in-use"));
        var failed = await _h.RunAsync(_h.VerificationRequest());
        Assert.Equal(DatabaseRestoreStatus.Succeeded, failed.Status);
        Assert.Equal(VerificationCleanup.Failed, failed.Verification!.Cleanup);
        Assert.Equal("database-in-use", failed.Verification.CleanupReasonCode);
        Assert.Contains(failed.Warnings, warning => warning.Code == RestorePipelineReasons.CleanupFailed);

        _h.Provider.Drop = (_, _) => throw new InvalidOperationException("SECRET-PASSWORD-123 must never be reported");
        var thrown = await _h.RunAsync(_h.VerificationRequest());
        Assert.Equal(VerificationCleanup.Failed, thrown.Verification!.Cleanup);
        Assert.Equal(RestorePipelineReasons.CleanupFailed, thrown.Verification.CleanupReasonCode);
        Assert.DoesNotContain("SECRET", string.Join(' ', thrown.Warnings.Select(warning => warning.Message)));
    }

    [Fact]
    public async Task AnOrdinaryRestoreNeverVerifiesOrDrops()
    {
        var result = await _h.RunAsync();

        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
        Assert.Null(result.Verification);
        Assert.Empty(_h.Provider.Verifications);
        Assert.Empty(_h.Provider.Drops);
    }
}
