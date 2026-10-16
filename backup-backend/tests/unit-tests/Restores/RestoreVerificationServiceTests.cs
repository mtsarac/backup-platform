using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Restores;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using static BackupPlatform.UnitTests.RestoreFixtures;

namespace BackupPlatform.UnitTests;

/// <summary>What a user can and cannot ask of an isolated verification, and what the agent's report must look like before the backend believes it.</summary>
public sealed class RestoreVerificationServiceTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly Guid AgentGuid = Guid.NewGuid();
    private static readonly Guid Lease = Guid.NewGuid();
    private const string Rollback = "bp_old_0123456789abcdef0123456789abcdef";

    private static readonly RestoreTargetRequest Sql = new("sql01.corp.local", 1433, false, "sql-cred_1");

    private static (RestoreService Service, FakeRestoreStore Store) Create(RestoreExecutionOptions? options = null)
    {
        var store = new FakeRestoreStore();
        store.OnCreate = command => new RestoreCreateResult(RestoreCreateOutcome.Created, new RestoreOperation(Guid.NewGuid(), command.TenantId, command.ArtifactId, Guid.NewGuid(),
            RestoreSourceAccess.CentralStorage, command.AgentId, command.Target, command.DatabaseName, command.Mode, command.OverwriteConfirmed, command.Options, command.IdempotencyKey,
            command.RequestFingerprint, command.RequestedByUserId, command.Now, command.Purpose));
        return (new RestoreService(store, options ?? new RestoreExecutionOptions(), new RestoreClock(Now)), store);
    }

    [Fact]
    public async Task TheBackendNamesTheDisposableDatabaseAndTheCallerCannotChooseItsModeOrConfirmAnOverwrite()
    {
        var (service, store) = Create();
        var artifact = Guid.NewGuid();
        var first = await service.VerifyAsync(Tenant, artifact, "key-1", RequestedUserId, new StartRestoreVerificationRequest(AgentGuid, Sql), CancellationToken.None);
        var second = await service.VerifyAsync(Tenant, artifact, "key-2", RequestedUserId, new StartRestoreVerificationRequest(AgentGuid, Sql), CancellationToken.None);

        Assert.Equal(RestoreStartOutcome.Started, first.Outcome);
        Assert.Equal(2, store.Created.Count);
        Assert.All(store.Created, command =>
        {
            Assert.Equal(RestorePurpose.Verification, command.Purpose);
            Assert.Equal(RestoreMode.NewDatabase, command.Mode);
            Assert.False(command.OverwriteConfirmed);
            Assert.False(command.Options.TerminateActiveConnections);
            Assert.True(PlatformDatabaseNames.IsVerification(command.DatabaseName));
        });
        Assert.NotEqual(store.Created[0].DatabaseName, store.Created[1].DatabaseName);
        // The name is not part of what makes two requests the same request, so a replayed key is judged on what the caller actually asked for.
        Assert.Equal(store.Created[0].RequestFingerprint, store.Created[1].RequestFingerprint);
        Assert.Equal("Verification", first.Restore!.Purpose);
        Assert.Equal(PlatformDatabaseNames.VerificationPrefix, first.Restore.DatabaseName[..PlatformDatabaseNames.VerificationPrefix.Length]);
    }

    [Fact]
    public void AVerificationRequestThatNamesADatabaseOrAModeIsInvalid()
    {
        Assert.Throws<InvalidRestoreRequestException>(() => RestoreInputValidation.Validate(Guid.NewGuid(),
            new StartRestoreRequest(AgentGuid, Sql, "Sales"), RestorePurpose.Verification));
        Assert.Equal("mode", Assert.Throws<InvalidRestoreRequestException>(() => RestoreInputValidation.Validate(Guid.NewGuid(),
            new StartRestoreRequest(AgentGuid, Sql, string.Empty, Mode: "ReplaceExistingDatabase"), RestorePurpose.Verification)).Field);
        Assert.Equal("confirmOverwrite", Assert.Throws<InvalidRestoreRequestException>(() => RestoreInputValidation.Validate(Guid.NewGuid(),
            new StartRestoreRequest(AgentGuid, Sql, string.Empty, ConfirmOverwrite: "x"), RestorePurpose.Verification)).Field);
        var valid = RestoreInputValidation.Validate(Guid.NewGuid(), new StartRestoreRequest(AgentGuid, Sql, string.Empty, Options: new RestoreOptionsRequest(TerminateActiveConnections: true)),
            RestorePurpose.Verification);
        Assert.False(valid.Options.TerminateActiveConnections);
    }

    // ---- the agent's report ------------------------------------------------------------------------------------------------

    private static RestoreTaskCompletionRequest Report(string status, string state, string cleanup, string? failure = null, string? retained = null,
        params RestorePreCheckItemResult[] checks) => new()
    {
        LeaseToken = Lease, Status = status, TargetState = state, FailureCode = failure,
        Result = new RestoreTaskResult
        {
            RetainedDatabaseName = retained,
            Verification = new RestoreVerificationResult { Checks = checks, Cleanup = cleanup }
        }
    };

    private static RestorePreCheckItemResult Check(string status) => new("integrity", status, "ok", "Fine.");

    [Theory]
    [InlineData("Dropped", true)]
    [InlineData("NotCreated", true)]
    [InlineData("Failed", false)]
    public void TheDisposableDatabaseIsGoneOnlyWhenTheAgentSaysSo(string cleanup, bool removed)
    {
        var completion = RestoreInputValidation.ToCompletion(Report("Succeeded", "NotModified", cleanup, checks: Check("Passed")));
        Assert.Equal(removed, completion.TemporaryDatabaseRemoved);
        Assert.Equal(RestoreTargetState.NotModified, completion.TargetState);
        Assert.False(RestoreInputValidation.ToCompletion(new RestoreTaskCompletionRequest { LeaseToken = Lease, Status = "Failed", TargetState = "NotModified", FailureCode = "InternalError" }).TemporaryDatabaseRemoved);
    }

    [Fact]
    public void AMalformedVerificationReportIsRefused()
    {
        Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Report("Succeeded", "NotModified", "Vanished", checks: Check("Passed"))));
        Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Report("Succeeded", "NotModified", "Dropped", checks: Check("Skipped"))));
        Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Report("Succeeded", "NotModified", "Dropped", checks: new RestorePreCheckItemResult("integrity", "Passed", "ok", "bad\u0000text"))));
        Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Report("Succeeded", "NotModified", "Dropped", retained: "Sales", checks: Check("Passed"))));
        Assert.Throws<ArgumentException>(() => RestoreInputValidation.ToCompletion(Report("Succeeded", "NotModified", "Dropped",
            checks: Enumerable.Range(0, RestoreInputValidation.MaxVerificationChecks + 1).Select(_ => Check("Passed")).ToArray())));
    }

    [Fact]
    public void ARetainedRollbackCopyIsOnlyHonouredOnSuccess()
    {
        var success = RestoreInputValidation.ToCompletion(new RestoreTaskCompletionRequest
        {
            LeaseToken = Lease, Status = "Succeeded", TargetState = "Online", Result = new RestoreTaskResult { RetainedDatabaseName = Rollback }
        });
        Assert.Equal(Rollback, success.RetainedDatabaseName);
        var failed = RestoreInputValidation.ToCompletion(new RestoreTaskCompletionRequest
        {
            LeaseToken = Lease, Status = "Failed", TargetState = "Unknown", FailureCode = "InternalError", Result = new RestoreTaskResult { RetainedDatabaseName = Rollback }
        });
        Assert.Null(failed.RetainedDatabaseName);
    }

    // ---- operator cleanup ---------------------------------------------------------------------------------------------------

    private static RestoreOperation Replaced()
    {
        var operation = Queued(RestoreMode.ReplaceExistingDatabase);
        var token = Begin(operation);
        operation.Complete(token, Now.AddMinutes(1), new RestoreCompletion(RestoreStatus.Succeeded, null, RestoreTargetState.Online, null, RetainedDatabaseName: Rollback));
        return operation;
    }

    [Fact]
    public async Task RemovingARollbackCopyNeedsItsExactNameAndOnlyAsksTheStore()
    {
        var (service, store) = Create();
        var operation = Replaced();
        store.OnGet = (_, _) => operation;
        store.CleanupRequestResult = new RestoreCleanupRequestResult(RestoreCleanupRequestOutcome.Accepted, operation);

        foreach (var wrong in new[] { null, "", "bp_old_other", Rollback.ToUpperInvariant(), Rollback + " " })
        {
            Assert.Equal(RestoreCleanupRequestOutcome.ConfirmationMismatch,
                (await service.RequestCleanupAsync(Tenant, operation.Id, RestoreCleanupKind.RollbackCopy, wrong, RequestedUserId, CancellationToken.None)).Outcome);
        }
        Assert.Empty(store.CleanupRequests);

        var accepted = await service.RequestCleanupAsync(Tenant, operation.Id, RestoreCleanupKind.RollbackCopy, Rollback, RequestedUserId, CancellationToken.None);
        Assert.Equal(RestoreCleanupRequestOutcome.Accepted, accepted.Outcome);
        var request = Assert.Single(store.CleanupRequests);
        Assert.Equal((operation.Id, new RestoreExecutionOptions().RollbackRetention, RequestedUserId), (request.RestoreId, request.Retention, request.User));
    }

    [Fact]
    public async Task ARoutedKindMustMatchWhatTheOperationOwes()
    {
        var (service, store) = Create();
        var operation = Replaced();
        store.OnGet = (_, _) => operation;
        // The routine retry route can never be used to drop a rollback copy, and the rollback route never retries a verification.
        Assert.Equal(RestoreCleanupRequestOutcome.NotApplicable,
            (await service.RequestCleanupAsync(Tenant, operation.Id, RestoreCleanupKind.VerificationDatabase, null, RequestedUserId, CancellationToken.None)).Outcome);
        Assert.Equal(RestoreCleanupRequestOutcome.NotFound,
            (await Create().Service.RequestCleanupAsync(Tenant, Guid.NewGuid(), RestoreCleanupKind.RollbackCopy, Rollback, RequestedUserId, CancellationToken.None)).Outcome);
        Assert.Empty(store.CleanupRequests);
    }

    [Fact]
    public void TheViewNeverOffersARollbackRemovalInsideItsRetentionAndNeverToSupportCallers()
    {
        var operation = Replaced();
        var retention = TimeSpan.FromDays(7);
        var early = RestoreService.ToView(operation, false, retention, Now.AddDays(1)).Cleanup!;
        Assert.Equal((RestoreCleanupKind.RollbackCopy.ToString(), "Retained", Rollback, false), (early.Kind, early.State, early.DatabaseName, early.CanRequest));
        Assert.Equal(operation.CompletedAt + retention, early.RetainedUntil);
        Assert.True(RestoreService.ToView(operation, false, retention, Now.AddDays(8)).Cleanup!.CanRequest);
        // The support projection passes neither retention nor a clock: it can see the copy but is never offered its removal.
        Assert.False(RestoreService.ToView(operation, false).Cleanup!.CanRequest);
    }

    [Fact]
    public void AVerificationViewStatesItsOutcomeAndWhatBecameOfItsDisposableDatabase()
    {
        var restored = Verified(removed: true, RestoreStatus.Succeeded);
        var view = RestoreService.ToView(restored, false, TimeSpan.FromDays(7), Now.AddHours(1));
        Assert.Equal("Restored", view.VerificationOutcome);
        Assert.Equal("Completed", view.Cleanup!.State);
        Assert.Equal("NotModified", view.TargetState);

        var owed = Verified(removed: false, RestoreStatus.Failed);
        var failedView = RestoreService.ToView(owed, false, TimeSpan.FromDays(7), Now.AddHours(1));
        Assert.Equal("Failed", failedView.VerificationOutcome);
        Assert.Equal("Pending", failedView.Cleanup!.State);
        Assert.True(failedView.Cleanup.CanRequest);
        Assert.Equal("The backup restored, but the restored database did not pass its integrity or data-read checks.", failedView.Reason);

        var running = new RestoreOperation(Guid.NewGuid(), Tenant, Guid.NewGuid(), Guid.NewGuid(), RestoreSourceAccess.CentralStorage, AgentId.New(), Target(),
            PlatformDatabaseNames.NewVerificationName(), RestoreMode.NewDatabase, false, null, "k", "f", RequestedUserId, Now, RestorePurpose.Verification);
        running.QueueFirstAttempt(Guid.NewGuid(), Now);
        Assert.Null(RestoreService.ToView(running, false).VerificationOutcome);
        running.RequestCancellation(Now);
        Assert.Equal("NotRun", RestoreService.ToView(running, false).VerificationOutcome);
    }

    private static RestoreOperation Verified(bool removed, RestoreStatus status)
    {
        var operation = new RestoreOperation(Guid.NewGuid(), Tenant, Guid.NewGuid(), Guid.NewGuid(), RestoreSourceAccess.CentralStorage, AgentId.New(), Target(),
            PlatformDatabaseNames.NewVerificationName(), RestoreMode.NewDatabase, false, null, "k", "f", RequestedUserId, Now, RestorePurpose.Verification);
        operation.QueueFirstAttempt(Guid.NewGuid(), Now);
        var token = Begin(operation);
        operation.Complete(token, Now.AddMinutes(1), status == RestoreStatus.Succeeded
            ? new RestoreCompletion(RestoreStatus.Succeeded, null, RestoreTargetState.NotModified, "{}", removed)
            : new RestoreCompletion(RestoreStatus.Failed, RestoreFailureCodes.VerificationFailed, RestoreTargetState.NotModified, "{}", removed));
        return operation;
    }

    // ---- the agent's lease --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACleanupReportIsValidatedBeforeTheStoreSeesIt()
    {
        var (service, store) = Create();
        var identity = new AgentIdentity(new AgentId(AgentGuid), Tenant, Guid.NewGuid(), 1);
        var operationId = Guid.NewGuid();

        await service.CompleteCleanupAsync(identity, operationId, new RestoreCleanupCompletionRequest { LeaseToken = Lease, Outcome = "Dropped" }, CancellationToken.None);
        await service.CompleteCleanupAsync(identity, operationId, new RestoreCleanupCompletionRequest { LeaseToken = Lease, Outcome = "Failed", FailureCode = "drop-failed" }, CancellationToken.None);
        Assert.Equal([true, false], store.CleanupCompletions.Select(entry => entry.Removed));

        foreach (var bad in new[]
        {
            new RestoreCleanupCompletionRequest { LeaseToken = Guid.Empty, Outcome = "Dropped" },
            new RestoreCleanupCompletionRequest { LeaseToken = Lease, Outcome = "Gone" },
            new RestoreCleanupCompletionRequest { LeaseToken = Lease, Outcome = "Failed" },
            new RestoreCleanupCompletionRequest { LeaseToken = Lease, Outcome = "Dropped", FailureCode = "drop-failed" },
            new RestoreCleanupCompletionRequest { LeaseToken = Lease, Outcome = "Failed", FailureCode = "drop failed; DROP TABLE" }
        })
            await Assert.ThrowsAsync<ArgumentException>(() => service.CompleteCleanupAsync(identity, operationId, bad, CancellationToken.None));
        Assert.Equal(2, store.CleanupCompletions.Count);
    }
}
