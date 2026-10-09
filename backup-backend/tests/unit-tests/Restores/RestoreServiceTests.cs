using System.Reflection;
using System.Text.Json;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Restores;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;
using static BackupPlatform.UnitTests.RestoreFixtures;

namespace BackupPlatform.UnitTests;

/// <summary>The restore service against a recording fake store: request mapping, store commands, view and snapshot projection and the fixed sentences.</summary>
public sealed class RestoreServiceTests
{
    private static readonly TenantId Tenant = TenantId.New();
    private static readonly Guid ArtifactId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid AgentGuid = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid ReplicaGuid = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid AttemptId = Guid.Parse("dddddddd-0000-0000-0000-000000000004");
    private static readonly Guid LeaseToken = Guid.Parse("eeeeeeee-0000-0000-0000-000000000005");
    private static readonly Guid User = Guid.Parse("ffffffff-0000-0000-0000-000000000006");
    private static readonly string GenericReason = "The restore could not complete because of an internal error.";

    private sealed class Harness
    {
        public Harness(RestoreExecutionOptions? options = null)
        {
            Options = options ?? new RestoreExecutionOptions();
            Service = new RestoreService(Store, Options, Clock);
        }

        public FakeRestoreStore Store { get; } = new();
        public RestoreClock Clock { get; } = new(Now.AddMinutes(7));
        public RestoreExecutionOptions Options { get; }
        public RestoreService Service { get; }
    }

    private static StartRestoreRequest Request() => new(AgentGuid, new RestoreTargetRequest("sql01.corp.local", 1433, false, "sql-cred_1"), "Sales_Restored");

    private static AgentIdentity Identity() => new(new AgentId(AgentGuid), Tenant, Guid.Parse("99999999-0000-0000-0000-000000000009"), 3);

    private static RestoreOperation Terminal(RestoreCompletion completion)
    {
        var operation = Running(out var token);
        operation.Complete(token, Now.AddMinutes(1), completion);
        return operation;
    }

    private static RestoreTaskResult SampleResult() => new()
    {
        PreCheck = [new RestorePreCheckItemResult("disk-space", "Passed", "ok", "There is enough space.")],
        Warnings = [new RestoreWarningResult("W-1", "A warning.")],
        FileMappings = [new RestoreFileMappingResult { LogicalName = "Sales", TargetPhysicalName = "/data/Sales_Restored.mdf", SizeBytes = 10 }],
        ChecksumVerified = true,
        BackupVerified = true,
        DatabaseState = "ONLINE"
    };

    private static RestoreTaskCompletionRequest SuccessRequest(RestoreTaskResult? result = null) =>
        new() { LeaseToken = LeaseToken, Status = "Succeeded", TargetState = "Online", Result = result };

    private static RestoreOperation SucceededWithResult()
    {
        var completion = RestoreInputValidation.ToCompletion(SuccessRequest(SampleResult()));
        return Terminal(completion);
    }

    // ---- StartAsync: mapping of store outcomes ----

    [Fact]
    public async Task ACreatedRestoreIsStartedAndReturnedWithoutItsResult()
    {
        var h = new Harness();
        var operation = Queued();
        h.Store.OnCreate = _ => new RestoreCreateResult(RestoreCreateOutcome.Created, operation);

        var result = await h.Service.StartAsync(Tenant, ArtifactId, "key-1", User, Request(), default);

        Assert.Equal(RestoreStartOutcome.Started, result.Outcome);
        Assert.Equal(operation.Id, result.Restore!.Id);
        Assert.Equal("Pending", result.Restore.Status);
        Assert.Null(result.Restore.Result);
        Assert.Null(result.Code);
        Assert.Null(result.Detail);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task AReplayedRestoreReturnsTheOriginalOperation()
    {
        var h = new Harness();
        var operation = SucceededWithResult();
        h.Store.OnCreate = _ => new RestoreCreateResult(RestoreCreateOutcome.Replayed, operation);

        var result = await h.Service.StartAsync(Tenant, ArtifactId, "key-1", User, Request(), default);

        Assert.Equal(RestoreStartOutcome.Replayed, result.Outcome);
        Assert.Equal(operation.Id, result.Restore!.Id);
        Assert.Equal("Succeeded", result.Restore.Status);
        Assert.Null(result.Restore.Result);
        Assert.Null(result.Code);
        Assert.Null(result.Message);
    }

    [Theory]
    [InlineData(RestoreCreateOutcome.ArtifactNotFound, RestoreStartOutcome.ArtifactNotFound)]
    [InlineData(RestoreCreateOutcome.AgentNotFound, RestoreStartOutcome.AgentNotFound)]
    public async Task ANotFoundOutcomeCarriesNoCodeAndNoMessage(RestoreCreateOutcome created, RestoreStartOutcome expected)
    {
        var h = new Harness();
        h.Store.OnCreate = _ => new RestoreCreateResult(created);

        var result = await h.Service.StartAsync(Tenant, ArtifactId, "key-1", User, Request(), default);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Restore);
        Assert.Null(result.Code);
        Assert.Null(result.Detail);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task AnInactiveAgentIsAnAgentCannotRestoreRefusalWithTheAgentNotActiveDetail()
    {
        var h = new Harness();
        h.Store.OnCreate = _ => new RestoreCreateResult(RestoreCreateOutcome.AgentNotActive);

        var result = await h.Service.StartAsync(Tenant, ArtifactId, "key-1", User, Request(), default);

        Assert.Equal(RestoreStartOutcome.AgentNotActive, result.Outcome);
        Assert.Equal("agent_cannot_restore", result.Code);
        Assert.Equal("agent_not_active", result.Detail);
        Assert.Contains("not active", result.Message);
        Assert.Null(result.Restore);
    }

    [Fact]
    public async Task AnAgentWithoutTheRestoreCapabilityIsAnAgentCannotRestoreRefusalWithTheCapabilityDetail()
    {
        var h = new Harness();
        h.Store.OnCreate = _ => new RestoreCreateResult(RestoreCreateOutcome.AgentCannotRestore);

        var result = await h.Service.StartAsync(Tenant, ArtifactId, "key-1", User, Request(), default);

        Assert.Equal(RestoreStartOutcome.AgentCannotRestore, result.Outcome);
        Assert.Equal("agent_cannot_restore", result.Code);
        Assert.Equal("capability_not_supported", result.Detail);
        Assert.Contains("restore", result.Message);
        Assert.Null(result.Restore);
    }

    [Fact]
    public async Task AReusedKeyWithADifferentRequestIsAnIdempotencyConflict()
    {
        var h = new Harness();
        h.Store.OnCreate = _ => new RestoreCreateResult(RestoreCreateOutcome.IdempotencyConflict);

        var result = await h.Service.StartAsync(Tenant, ArtifactId, "key-1", User, Request(), default);

        Assert.Equal(RestoreStartOutcome.IdempotencyConflict, result.Outcome);
        Assert.Equal("idempotency_conflict", result.Code);
        Assert.Null(result.Detail);
        Assert.Contains("Idempotency-Key", result.Message);
        Assert.Null(result.Restore);
    }

    public static TheoryData<RestoreSourceRejection, string> RejectionCodes => new()
    {
        { RestoreSourceRejection.ReplicaNotFound, "replica_not_found" },
        { RestoreSourceRejection.ReplicaNotRestorable, "replica_not_restorable" },
        { RestoreSourceRejection.ReplicaNotVerified, "replica_not_verified" },
        { RestoreSourceRejection.ChecksumUnavailable, "checksum_unavailable" },
        { RestoreSourceRejection.BackupTypeUnsupported, "backup_type_unsupported" },
        { RestoreSourceRejection.AgentTransferRequired, "agent_transfer_required" },
        { RestoreSourceRejection.NoAccessibleReplica, "no_accessible_replica" }
    };

    [Theory]
    [MemberData(nameof(RejectionCodes))]
    public async Task ARejectedSourceIsReportedWithItsSnakeCaseCodeAndItsFixedMessage(RestoreSourceRejection rejection, string code)
    {
        var h = new Harness();
        h.Store.OnCreate = _ => new RestoreCreateResult(RestoreCreateOutcome.SourceRejected, Rejection: rejection);

        var result = await h.Service.StartAsync(Tenant, ArtifactId, "key-1", User, Request(), default);

        Assert.Equal(RestoreStartOutcome.SourceRejected, result.Outcome);
        Assert.Equal(code, result.Code);
        Assert.Equal(RestoreService.RejectionMessage(rejection), result.Message);
        Assert.Null(result.Detail);
        Assert.Null(result.Restore);
    }

    [Theory]
    [MemberData(nameof(RejectionCodes))]
    public void RejectionCodeIsTheEnumNameInSnakeCase(RestoreSourceRejection rejection, string code) => Assert.Equal(code, RestoreService.RejectionCode(rejection));

    [Fact]
    public void EveryRejectionHasItsOwnNonEmptyMessage()
    {
        var rejections = Enum.GetValues<RestoreSourceRejection>();
        var messages = rejections.Select(RestoreService.RejectionMessage).ToArray();

        Assert.All(messages, message => Assert.False(string.IsNullOrWhiteSpace(message)));
        Assert.Equal(messages.Length, messages.Distinct().Count());
    }

    [Fact]
    public void AnAgentTransferRejectionExplainsThatTransferringBetweenAgentsIsNotSupported()
    {
        var message = RestoreService.RejectionMessage(RestoreSourceRejection.AgentTransferRequired);
        Assert.Contains("Transferring a backup between agents is not supported", message);
        Assert.Contains("central storage", message);
    }

    [Theory]
    [InlineData(RestoreSourceRejection.ReplicaNotFound, "does not belong")]
    [InlineData(RestoreSourceRejection.ReplicaNotRestorable, "no longer be restored")]
    [InlineData(RestoreSourceRejection.ReplicaNotVerified, "not been verified")]
    [InlineData(RestoreSourceRejection.ChecksumUnavailable, "checksum")]
    [InlineData(RestoreSourceRejection.BackupTypeUnsupported, "full database backup")]
    [InlineData(RestoreSourceRejection.NoAccessibleReplica, "reachable")]
    public void EachRejectionMessageSaysWhatIsWrong(RestoreSourceRejection rejection, string phrase) =>
        Assert.Contains(phrase, RestoreService.RejectionMessage(rejection));

    [Fact]
    public void AnUnknownRejectionFallsBackToTheNoAccessibleReplicaMessage() =>
        Assert.Equal(RestoreService.RejectionMessage(RestoreSourceRejection.NoAccessibleReplica), RestoreService.RejectionMessage((RestoreSourceRejection)99));

    // ---- StartAsync: what the store is asked ----

    [Fact]
    public async Task TheStoreReceivesAFullyValidatedCommand()
    {
        var h = new Harness();
        var request = Request() with { ReplicaId = ReplicaGuid, Options = new RestoreOptionsRequest(TerminateActiveConnections: true, VerifyBackup: false) };

        await h.Service.StartAsync(Tenant, ArtifactId, "key-123", User, request, default);

        var command = Assert.Single(h.Store.Created);
        var validated = RestoreInputValidation.Validate(ArtifactId, request);
        Assert.Equal(Tenant, command.TenantId);
        Assert.Equal(ArtifactId, command.ArtifactId);
        Assert.Equal(ReplicaGuid, command.ReplicaId);
        Assert.Equal(new AgentId(AgentGuid), command.AgentId);
        Assert.Equal(new RestoreTarget("sql01.corp.local", 1433, false, "sql-cred_1", true, false), command.Target);
        Assert.Equal("Sales_Restored", command.DatabaseName);
        Assert.Equal(RestoreMode.NewDatabase, command.Mode);
        Assert.False(command.OverwriteConfirmed);
        Assert.Equal(new RestoreOptions(true, true, false), command.Options);
        Assert.Equal("key-123", command.IdempotencyKey);
        Assert.Equal(validated.Fingerprint, command.RequestFingerprint);
        Assert.Equal(User, command.RequestedByUserId);
        Assert.Equal(h.Clock.GetUtcNow(), command.Now);
    }

    [Fact]
    public async Task AnOmittedReplicaIsPassedAsNullSoTheStoreChoosesOne()
    {
        var h = new Harness();
        await h.Service.StartAsync(Tenant, ArtifactId, "key-1", null, Request(), default);

        var command = Assert.Single(h.Store.Created);
        Assert.Null(command.ReplicaId);
        Assert.Null(command.RequestedByUserId);
    }

    [Fact]
    public async Task AConfirmedReplaceReachesTheStoreAsAConfirmedOverwrite()
    {
        var h = new Harness();
        await h.Service.StartAsync(Tenant, ArtifactId, "key-1", User, Request() with { Mode = "replaceexistingdatabase", ConfirmOverwrite = "Sales_Restored" }, default);

        var command = Assert.Single(h.Store.Created);
        Assert.Equal(RestoreMode.ReplaceExistingDatabase, command.Mode);
        Assert.True(command.OverwriteConfirmed);
    }

    [Fact]
    public async Task TheCommandCarriesTheBackendClockAtTheMomentOfTheCall()
    {
        var h = new Harness();
        await h.Service.StartAsync(Tenant, ArtifactId, "key-1", User, Request(), default);
        h.Clock.Advance(TimeSpan.FromMinutes(3));
        await h.Service.StartAsync(Tenant, ArtifactId, "key-2", User, Request(), default);

        Assert.Equal(2, h.Store.Created.Count);
        Assert.Equal(Now.AddMinutes(7), h.Store.Created[0].Now);
        Assert.Equal(Now.AddMinutes(10), h.Store.Created[1].Now);
    }

    [Fact]
    public async Task TheSameRequestUnderDifferentKeysHasTheSameFingerprint()
    {
        var h = new Harness();
        await h.Service.StartAsync(Tenant, ArtifactId, "key-1", User, Request(), default);
        await h.Service.StartAsync(Tenant, ArtifactId, "key-2", User, Request(), default);
        await h.Service.StartAsync(Tenant, ArtifactId, "key-3", User, Request() with { DatabaseName = "Other" }, default);

        Assert.Equal(h.Store.Created[0].RequestFingerprint, h.Store.Created[1].RequestFingerprint);
        Assert.NotEqual(h.Store.Created[0].RequestFingerprint, h.Store.Created[2].RequestFingerprint);
    }

    [Fact]
    public async Task AnInvalidIdempotencyKeyIsRefusedBeforeTheStoreIsAsked()
    {
        var h = new Harness();
        foreach (var key in new string?[] { null, "", " ", "has space", new string('k', 129), "schlüssel", "scheduled-1", "SCHEDULED-1", "tab\tkey" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => h.Service.StartAsync(Tenant, ArtifactId, key!, User, Request(), default));
        }
        Assert.Empty(h.Store.Calls);
    }

    [Fact]
    public async Task AKeyOfTheMaximumLengthIsAccepted()
    {
        var h = new Harness();
        await h.Service.StartAsync(Tenant, ArtifactId, new string('k', 128), User, Request(), default);
        Assert.Single(h.Store.Created);
    }

    [Fact]
    public async Task AnInvalidKeyIsReportedBeforeAnInvalidRequest()
    {
        var h = new Harness();
        var request = Request() with { DatabaseName = "master" };
        await Assert.ThrowsAsync<ArgumentException>(() => h.Service.StartAsync(Tenant, ArtifactId, "", User, request, default));
        Assert.Empty(h.Store.Calls);
    }

    [Fact]
    public async Task AnInvalidRequestIsRefusedWithTheOffendingFieldAndTheStoreIsNotAsked()
    {
        var h = new Harness();
        var failure = await Assert.ThrowsAsync<InvalidRestoreRequestException>(() => h.Service.StartAsync(Tenant, ArtifactId, "key-1", User, Request() with { DatabaseName = "master" }, default));

        Assert.Equal("databaseName", failure.Field);
        Assert.Empty(h.Store.Calls);
    }

    [Fact]
    public async Task AnUnconfirmedReplaceIsRefusedBeforeTheStoreIsAsked()
    {
        var h = new Harness();
        var failure = await Assert.ThrowsAsync<InvalidRestoreRequestException>(() => h.Service.StartAsync(Tenant, ArtifactId, "key-1", User, Request() with { Mode = "ReplaceExistingDatabase" }, default));

        Assert.Equal("confirmOverwrite", failure.Field);
        Assert.Equal("confirmation_required", failure.Code);
        Assert.Empty(h.Store.Calls);
    }

    [Fact]
    public async Task AnEmptyArtifactIdIsRefusedBeforeTheStoreIsAsked()
    {
        var h = new Harness();
        var failure = await Assert.ThrowsAsync<InvalidRestoreRequestException>(() => h.Service.StartAsync(Tenant, Guid.Empty, "key-1", User, Request(), default));

        Assert.Equal("artifactId", failure.Field);
        Assert.Empty(h.Store.Calls);
    }

    [Fact]
    public async Task ANullRequestIsAProgrammingErrorAndTheStoreIsNotAsked()
    {
        var h = new Harness();
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Service.StartAsync(Tenant, ArtifactId, "key-1", User, null!, default));
        Assert.Empty(h.Store.Calls);
    }

    // ---- reading and cancelling ----

    [Fact]
    public async Task GettingARestoreReturnsTheDetailViewWithItsResult()
    {
        var h = new Harness();
        var operation = SucceededWithResult();
        h.Store.OnGet = (_, _) => operation;

        var view = await h.Service.GetAsync(Tenant, operation.Id, default);

        Assert.Equal((Tenant, operation.Id), Assert.Single(h.Store.Gets));
        Assert.Equal(operation.Id, view!.Id);
        Assert.NotNull(view.Result);
        Assert.True(view.Result.ChecksumVerified);
        Assert.Equal("ONLINE", view.Result.DatabaseState);
        Assert.Equal("disk-space", Assert.Single(view.Result.PreCheck).CheckId);
    }

    [Fact]
    public async Task GettingAnUnknownRestoreReturnsNull()
    {
        var h = new Harness();
        Assert.Null(await h.Service.GetAsync(Tenant, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task CancellingAsksTheStoreWithTheClockAndReturnsTheDetailView()
    {
        var h = new Harness();
        var operation = Queued();
        var id = operation.Id;
        h.Store.OnCancel = (_, _, now) =>
        {
            operation.RequestCancellation(now);
            return operation;
        };

        var view = await h.Service.CancelAsync(Tenant, id, default);

        Assert.Equal((Tenant, id, Now.AddMinutes(7)), Assert.Single(h.Store.Cancellations));
        Assert.Equal("Cancelled", view!.Status);
        Assert.Equal(RestoreFailureCodes.Cancelled, view.FailureCode);
        Assert.Equal("NotModified", view.TargetState);
        Assert.False(view.ReplicaPinned);
    }

    [Fact]
    public async Task CancellingAnUnknownRestoreReturnsNull()
    {
        var h = new Harness();
        Assert.Null(await h.Service.CancelAsync(Tenant, Guid.NewGuid(), default));
        Assert.Single(h.Store.Cancellations);
    }

    [Fact]
    public async Task TheCancelViewIncludesTheResultOfAnAlreadyFinishedRestore()
    {
        var h = new Harness();
        var operation = SucceededWithResult();
        h.Store.OnCancel = (_, _, _) => operation;

        var view = await h.Service.CancelAsync(Tenant, operation.Id, default);

        Assert.Equal("Succeeded", view!.Status);
        Assert.NotNull(view.Result);
    }

    // ---- listing ----

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-3, -5, 1, 1)]
    [InlineData(1, 25, 1, 25)]
    [InlineData(2, 100, 2, 100)]
    [InlineData(3, 101, 3, 100)]
    [InlineData(5, int.MaxValue, 5, 100)]
    [InlineData(int.MinValue, int.MinValue, 1, 1)]
    public async Task ListingClampsThePageAndThePageSize(int page, int pageSize, int expectedPage, int expectedPageSize)
    {
        var h = new Harness();
        await h.Service.ListAsync(Tenant, new RestoreQuery(Page: page, PageSize: pageSize), default);

        var (tenant, query) = Assert.Single(h.Store.Lists);
        Assert.Equal(Tenant, tenant);
        Assert.Equal(expectedPage, query.Page);
        Assert.Equal(expectedPageSize, query.PageSize);
    }

    [Fact]
    public async Task ListingPassesTheFiltersThroughUnchanged()
    {
        var h = new Harness();
        await h.Service.ListAsync(Tenant, new RestoreQuery(RestoreStatus.Running, ArtifactId, AgentGuid, 2, 10), default);

        var (_, query) = Assert.Single(h.Store.Lists);
        Assert.Equal(RestoreStatus.Running, query.Status);
        Assert.Equal(ArtifactId, query.ArtifactId);
        Assert.Equal(AgentGuid, query.AgentId);
    }

    [Fact]
    public async Task ListingMapsEveryItemWithoutItsResultAndKeepsTheStoresPaging()
    {
        var h = new Harness();
        var finished = SucceededWithResult();
        var pending = Queued();
        h.Store.OnList = (_, _) => new RestorePage([finished, pending], 42, 2, 25);

        var page = await h.Service.ListAsync(Tenant, new RestoreQuery(Page: 2, PageSize: 25), default);

        Assert.Equal(42, page.TotalCount);
        Assert.Equal(2, page.Page);
        Assert.Equal(25, page.PageSize);
        Assert.Equal(new[] { finished.Id, pending.Id }, page.Items.Select(item => item.Id).ToArray());
        Assert.All(page.Items, item => Assert.Null(item.Result));
        Assert.Equal("Succeeded", page.Items[0].Status);
        Assert.Equal("Pending", page.Items[1].Status);
    }

    [Fact]
    public async Task AnEmptyPageIsAnEmptyView()
    {
        var h = new Harness();
        var page = await h.Service.ListAsync(Tenant, new RestoreQuery(), default);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    // ---- the target agent ----

    [Fact]
    public async Task ClaimingPassesTheIdentityTheClockAndTheConfiguredDurations()
    {
        var h = new Harness(new RestoreExecutionOptions { LeaseDuration = TimeSpan.FromSeconds(45), UnclaimedExpiry = TimeSpan.FromHours(2) });
        var identity = Identity();

        var snapshot = await h.Service.ClaimAsync(identity, default);

        Assert.Null(snapshot);
        var claim = Assert.Single(h.Store.Claims);
        Assert.Equal(identity.TenantId, claim.Tenant);
        Assert.Equal(identity.AgentId, claim.Agent);
        Assert.Equal(Now.AddMinutes(7), claim.Now);
        Assert.Equal(TimeSpan.FromSeconds(45), claim.Lease);
        Assert.Equal(TimeSpan.FromHours(2), claim.UnclaimedExpiry);
    }

    [Fact]
    public async Task ClaimingWithTheDefaultOptionsUsesTheBackupLeaseAndOneHour()
    {
        var h = new Harness();
        await h.Service.ClaimAsync(Identity(), default);

        var claim = Assert.Single(h.Store.Claims);
        Assert.Equal(BackupOrchestrator.LeaseDuration, claim.Lease);
        Assert.Equal(TimeSpan.FromHours(1), claim.UnclaimedExpiry);
    }

    [Fact]
    public async Task AClaimedRestoreIsReturnedAsATaskSnapshot()
    {
        var h = new Harness();
        var (operation, _, claim) = Claimed();
        h.Store.ClaimResult = claim;

        var snapshot = await h.Service.ClaimAsync(Identity(), default);

        Assert.Equal(operation.Id, snapshot!.OperationId);
        Assert.Equal(claim.Attempt.Id, snapshot.AttemptId);
    }

    [Fact]
    public async Task StartingPassesTheIdentityTheTokenTheMetadataTheClockAndTheLease()
    {
        var h = new Harness();
        var outcome = new RestoreLeaseOutcome(BackupMutationResult.Accepted, Now.AddMinutes(9), true);
        h.Store.StartResult = outcome;
        var identity = Identity();

        var result = await h.Service.StartAttemptAsync(identity, AttemptId, new BackupTaskStartRequest { LeaseToken = LeaseToken, SoftwareVersion = "2.1", ProtocolVersion = "1.0", Platform = "Linux" }, default);

        Assert.Equal(outcome, result);
        var (command, lease) = Assert.Single(h.Store.Starts);
        Assert.Equal(identity.TenantId, command.TenantId);
        Assert.Equal(identity.AgentId, command.AgentId);
        Assert.Equal(AttemptId, command.AttemptId);
        Assert.Equal(LeaseToken, command.LeaseToken);
        Assert.Equal(Now.AddMinutes(7), command.Now);
        Assert.Equal("2.1", command.SoftwareVersion);
        Assert.Equal("1.0", command.ProtocolVersion);
        Assert.Equal("Linux", command.Platform);
        Assert.Equal(h.Options.LeaseDuration, lease);
    }

    [Fact]
    public async Task AMalformedStartIsRefusedBeforeTheStoreIsAsked()
    {
        var h = new Harness();
        await Assert.ThrowsAsync<ArgumentException>(() => h.Service.StartAttemptAsync(Identity(), AttemptId,
            new BackupTaskStartRequest { LeaseToken = Guid.Empty, SoftwareVersion = "2.1", ProtocolVersion = "1.0", Platform = "Linux" }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Service.StartAttemptAsync(Identity(), AttemptId,
            new BackupTaskStartRequest { LeaseToken = LeaseToken, SoftwareVersion = "", ProtocolVersion = "1.0", Platform = "Linux" }, default));
        Assert.Empty(h.Store.Calls);
    }

    [Fact]
    public async Task RenewingPassesTheIdentityTheTokenTheClockAndTheLease()
    {
        var h = new Harness(new RestoreExecutionOptions { LeaseDuration = TimeSpan.FromSeconds(90) });
        var outcome = new RestoreLeaseOutcome(BackupMutationResult.Accepted, Now.AddMinutes(9), false);
        h.Store.RenewResult = outcome;
        var identity = Identity();

        var result = await h.Service.RenewLeaseAsync(identity, AttemptId, new BackupTaskLeaseRequest { LeaseToken = LeaseToken }, default);

        Assert.Equal(outcome, result);
        var (command, lease) = Assert.Single(h.Store.Renewals);
        Assert.Equal(new RestoreLeaseCommand(identity.TenantId, identity.AgentId, AttemptId, LeaseToken, Now.AddMinutes(7)), command);
        Assert.Equal(TimeSpan.FromSeconds(90), lease);
    }

    [Fact]
    public async Task RenewingWithoutATokenIsRefusedBeforeTheStoreIsAsked()
    {
        var h = new Harness();
        await Assert.ThrowsAsync<ArgumentException>(() => h.Service.RenewLeaseAsync(Identity(), AttemptId, new BackupTaskLeaseRequest { LeaseToken = Guid.Empty }, default));
        Assert.Empty(h.Store.Calls);
    }

    [Fact]
    public async Task ReportingProgressPassesTheStagePercentAndTheCommand()
    {
        var h = new Harness();
        h.Store.ProgressResult = BackupMutationResult.Conflict;
        var identity = Identity();

        var result = await h.Service.ReportProgressAsync(identity, AttemptId, new BackupTaskProgressRequest { LeaseToken = LeaseToken, Stage = RestoreStages.Restoring, Percent = 37.5 }, default);

        Assert.Equal(BackupMutationResult.Conflict, result);
        var (command, progress) = Assert.Single(h.Store.Progresses);
        Assert.Equal(new RestoreLeaseCommand(identity.TenantId, identity.AgentId, AttemptId, LeaseToken, Now.AddMinutes(7)), command);
        Assert.Equal(new RestoreProgress(RestoreStages.Restoring, 37.5), progress);
    }

    [Fact]
    public async Task ReportingProgressWithoutAPercentPassesNull()
    {
        var h = new Harness();
        await h.Service.ReportProgressAsync(Identity(), AttemptId, new BackupTaskProgressRequest { LeaseToken = LeaseToken, Stage = RestoreStages.Preparing }, default);
        Assert.Null(Assert.Single(h.Store.Progresses).Progress.Percent);
    }

    [Theory]
    [InlineData("Completed", 10.0)]
    [InlineData("Nope", 10.0)]
    [InlineData(RestoreStages.Restoring, 101.0)]
    [InlineData(RestoreStages.Restoring, double.NaN)]
    public async Task MalformedProgressIsRefusedBeforeTheStoreIsAsked(string stage, double percent)
    {
        var h = new Harness();
        await Assert.ThrowsAsync<ArgumentException>(() => h.Service.ReportProgressAsync(Identity(), AttemptId, new BackupTaskProgressRequest { LeaseToken = LeaseToken, Stage = stage, Percent = percent }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Service.ReportProgressAsync(Identity(), AttemptId, new BackupTaskProgressRequest { LeaseToken = Guid.Empty, Stage = RestoreStages.Restoring }, default));
        Assert.Empty(h.Store.Calls);
    }

    [Fact]
    public async Task CompletingPassesTheCommandAndTheMappedCompletion()
    {
        var h = new Harness();
        h.Store.CompleteResult = BackupMutationResult.NotFound;
        var identity = Identity();

        var result = await h.Service.CompleteAsync(identity, AttemptId,
            new RestoreTaskCompletionRequest { LeaseToken = LeaseToken, Status = "Failed", FailureCode = RestoreFailureCodes.InsufficientDiskSpace, TargetState = "NotModified" }, default);

        Assert.Equal(BackupMutationResult.NotFound, result);
        var (command, completion) = Assert.Single(h.Store.Completions);
        Assert.Equal(new RestoreLeaseCommand(identity.TenantId, identity.AgentId, AttemptId, LeaseToken, Now.AddMinutes(7)), command);
        Assert.Equal(new RestoreCompletion(RestoreStatus.Failed, RestoreFailureCodes.InsufficientDiskSpace, RestoreTargetState.NotModified, null), completion);
    }

    [Fact]
    public async Task ACancelledCompletionWithoutACodeReachesTheStoreWithTheCancelledCode()
    {
        var h = new Harness();
        await h.Service.CompleteAsync(Identity(), AttemptId, new RestoreTaskCompletionRequest { LeaseToken = LeaseToken, Status = "Cancelled", TargetState = "PartiallyRestored" }, default);

        var completion = Assert.Single(h.Store.Completions).Completion;
        Assert.Equal(RestoreStatus.Cancelled, completion.Status);
        Assert.Equal(RestoreFailureCodes.Cancelled, completion.FailureCode);
        Assert.Equal(RestoreTargetState.PartiallyRestored, completion.TargetState);
    }

    [Fact]
    public async Task ACompletionResultIsStoredAsTheReSerialisedTypedResult()
    {
        var h = new Harness();
        await h.Service.CompleteAsync(Identity(), AttemptId, SuccessRequest(SampleResult()), default);

        var completion = Assert.Single(h.Store.Completions).Completion;
        var stored = RestoreInputValidation.ReadResult(completion.ResultJson)!;
        Assert.Equal("ONLINE", stored.DatabaseState);
        Assert.Equal("Sales", Assert.Single(stored.FileMappings).LogicalName);
    }

    [Fact]
    public async Task AMalformedCompletionIsRefusedBeforeTheStoreIsAsked()
    {
        var h = new Harness();
        await Assert.ThrowsAsync<ArgumentException>(() => h.Service.CompleteAsync(Identity(), AttemptId, new RestoreTaskCompletionRequest { LeaseToken = LeaseToken, Status = "Succeeded", TargetState = "NotModified" }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Service.CompleteAsync(Identity(), AttemptId, new RestoreTaskCompletionRequest { LeaseToken = LeaseToken, Status = "Failed", FailureCode = "Cancelled", TargetState = "NotModified" }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Service.CompleteAsync(Identity(), AttemptId, new RestoreTaskCompletionRequest { LeaseToken = Guid.Empty, Status = "Succeeded", TargetState = "Online" }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Service.CompleteAsync(Identity(), AttemptId, SuccessRequest(new RestoreTaskResult { DatabaseState = "bad\0" }), default));
        Assert.Empty(h.Store.Calls);
    }

    // ---- the fixed sentences ----

    private static List<string> AllFailureCodeConstants() => typeof(RestoreFailureCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
        .Select(field => (string)field.GetRawConstantValue()!)
        .ToList();

    public static TheoryData<string> FailureCodesExceptInternalError
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var code in AllFailureCodeConstants().Where(code => code != RestoreFailureCodes.InternalError)) data.Add(code);
            return data;
        }
    }


    [Theory]
    [MemberData(nameof(FailureCodesExceptInternalError))]
    public void EveryFailureCodeHasItsOwnSpecificSentence(string code)
    {
        var reason = RestoreService.Reason(code);

        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.NotEqual(GenericReason, reason);
        Assert.EndsWith(".", reason);
        Assert.DoesNotContain("internal error", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoTwoFailureCodesShareASentence()
    {
        var specific = AllFailureCodeConstants().Where(code => code != RestoreFailureCodes.InternalError).Select(code => RestoreService.Reason(code)).ToArray();
        Assert.Equal(specific.Length, specific.Distinct().Count());
    }

    [Fact]
    public void InternalErrorAndAnyUnknownCodeShareTheGenericInternalErrorSentence()
    {
        Assert.Equal(GenericReason, RestoreService.Reason(RestoreFailureCodes.InternalError));
        Assert.Equal(GenericReason, RestoreService.Reason("SomethingTheBackendNeverHeardOf"));
        Assert.Equal(GenericReason, RestoreService.Reason(""));
        Assert.Equal(GenericReason, RestoreService.Reason("sqlrestorefailed"));
    }

    [Fact]
    public void NoCodeMeansNoReason() => Assert.Null(RestoreService.Reason(null));

    [Fact]
    public void TheBackendOnlyOutcomesExplainThemselves()
    {
        Assert.Equal("The restore was cancelled.", RestoreService.Reason(RestoreFailureCodes.Cancelled));
        Assert.Contains("lease expired", RestoreService.Reason(RestoreFailureCodes.AgentLost));
        Assert.Contains("without touching the target", RestoreService.Reason(RestoreFailureCodes.NotClaimed));
    }

    [Fact]
    public void NoTargetStateMeansNoSummary() => Assert.Null(RestoreService.TargetStateSummary(null));

    [Fact]
    public void EveryTargetStateHasItsOwnSummary()
    {
        var summaries = Enum.GetValues<RestoreTargetState>().Select(state => RestoreService.TargetStateSummary(state)).ToArray();
        Assert.All(summaries, summary => Assert.False(string.IsNullOrWhiteSpace(summary)));
        Assert.Equal(4, summaries.Distinct().Count());
        Assert.Equal("The target server was not modified.", RestoreService.TargetStateSummary(RestoreTargetState.NotModified));
        Assert.Equal("The restored database is online.", RestoreService.TargetStateSummary(RestoreTargetState.Online));
        Assert.Contains("RESTORING", RestoreService.TargetStateSummary(RestoreTargetState.PartiallyRestored));
        Assert.Contains("previous contents of a replaced database are gone", RestoreService.TargetStateSummary(RestoreTargetState.PartiallyRestored));
        Assert.Contains("could not be established", RestoreService.TargetStateSummary(RestoreTargetState.Unknown));
    }

    [Fact]
    public void AnUndefinedTargetStateIsTreatedAsUnknownNeverAsHealthy() =>
        Assert.Equal(RestoreService.TargetStateSummary(RestoreTargetState.Unknown), RestoreService.TargetStateSummary((RestoreTargetState)99));

    // ---- ToSnapshot ----

    private static (RestoreOperation Operation, BackupReplica Replica, RestoreClaim Claim) Claimed(bool agentLocal = false, RestoreOptions? options = null)
    {
        var agent = new AgentId(AgentGuid);
        var artifact = Artifact(Tenant);
        var replica = AddTo(artifact, agentLocal ? AgentReplica(artifact, AgentGuid, "tenant/2026/10/backup-local.bak") : CentralReplica(artifact, "tenant/2026/10/backup-central.bak"));
        var operation = Operation(access: agentLocal ? RestoreSourceAccess.AgentLocal : RestoreSourceAccess.CentralStorage, agent: agent, tenant: Tenant, replicaId: replica.Id,
            artifactId: artifact.Id, options: options, databaseName: "Sales_Restored");
        operation.QueueFirstAttempt(Guid.NewGuid(), Now);
        Claim(operation, Now.AddSeconds(10));
        return (operation, replica, new RestoreClaim(operation, operation.CurrentAttempt, replica));
    }

    [Fact]
    public void ACentralClaimIsProjectedFieldByField()
    {
        var (operation, replica, claim) = Claimed();
        var snapshot = RestoreService.ToSnapshot(claim);

        Assert.Equal(1, snapshot.Version);
        Assert.Equal(operation.Id, snapshot.OperationId);
        Assert.Equal(claim.Attempt.Id, snapshot.AttemptId);
        Assert.Equal(1, snapshot.AttemptNumber);
        Assert.Equal(Tenant.Value, snapshot.TenantId);
        Assert.Equal(claim.Attempt.LeaseToken, snapshot.LeaseToken);
        Assert.Equal(claim.Attempt.LeaseExpiresAt, snapshot.LeaseExpiresAt);
        Assert.Equal(Now.AddSeconds(10) + Lease, snapshot.LeaseExpiresAt);
        Assert.Equal(operation.ArtifactId, snapshot.ArtifactId);
        Assert.Equal(replica.Id, snapshot.ReplicaId);

        Assert.Equal("central", snapshot.Source.StorageProvider);
        Assert.Equal("default", snapshot.Source.StorageTarget);
        Assert.Equal("tenant/2026/10/backup-central.bak", snapshot.Source.StorageKey);
        Assert.Equal("Backend", snapshot.Source.StorageExecutionLocation);
        Assert.Equal(1000, snapshot.Source.SizeBytes);
        Assert.Equal(Hash, snapshot.Source.Checksum);
        Assert.Equal("sha256", snapshot.Source.ChecksumAlgorithm);
        Assert.Equal("CentralStorage", snapshot.Source.Access);

        Assert.Equal("sql01.corp.local", snapshot.Target.Host);
        Assert.Equal(1433, snapshot.Target.Port);
        Assert.False(snapshot.Target.IntegratedSecurity);
        Assert.Equal("sql-cred_1", snapshot.Target.DatabaseCredentialReference);
        Assert.True(snapshot.Target.Encrypt);
        Assert.False(snapshot.Target.TrustServerCertificate);

        Assert.Equal("Sales_Restored", snapshot.DatabaseName);
        Assert.Equal("NewDatabase", snapshot.Mode);
        Assert.Equal(new RestoreTaskOptions { TerminateActiveConnections = false, VerifyChecksum = true, VerifyBackup = true }, snapshot.Options);
        Assert.Equal("default", snapshot.StagingReference);
        Assert.Equal(RestoreService.StagingReference, snapshot.StagingReference);
        Assert.False(snapshot.CancellationRequested);
    }

    [Fact]
    public void AnAgentLocalClaimNamesTheAgentLocation()
    {
        var (_, replica, claim) = Claimed(agentLocal: true);
        var snapshot = RestoreService.ToSnapshot(claim);

        Assert.Equal("local", snapshot.Source.StorageProvider);
        Assert.Equal("Agent", snapshot.Source.StorageExecutionLocation);
        Assert.Equal("AgentLocal", snapshot.Source.Access);
        Assert.Equal(replica.Key, snapshot.Source.StorageKey);
    }

    [Fact]
    public void TheSnapshotCarriesTheRequestedOptionsAndMode()
    {
        var (_, _, claim) = Claimed(options: new RestoreOptions(TerminateActiveConnections: true, VerifyChecksum: false, VerifyBackup: false));
        var snapshot = RestoreService.ToSnapshot(claim);

        Assert.True(snapshot.Options.TerminateActiveConnections);
        Assert.False(snapshot.Options.VerifyChecksum);
        Assert.False(snapshot.Options.VerifyBackup);
    }

    [Fact]
    public void AReplaceRestoreIsProjectedAsTheReplaceMode()
    {
        var artifact = Artifact(Tenant);
        var replica = AddTo(artifact, CentralReplica(artifact));
        var operation = Operation(RestoreMode.ReplaceExistingDatabase, access: RestoreSourceAccess.CentralStorage, tenant: Tenant, replicaId: replica.Id, artifactId: artifact.Id);
        operation.QueueFirstAttempt(Guid.NewGuid(), Now);
        Claim(operation);

        Assert.Equal("ReplaceExistingDatabase", RestoreService.ToSnapshot(new RestoreClaim(operation, operation.CurrentAttempt, replica)).Mode);
    }

    [Fact]
    public void ACancellationRequestIsVisibleInTheSnapshot()
    {
        var (operation, _, claim) = Claimed();
        operation.RequestCancellation(Now.AddSeconds(20));
        Assert.True(RestoreService.ToSnapshot(claim).CancellationRequested);
    }

    [Fact]
    public void TheSnapshotNeverCarriesTheIdempotencyKeyTheFingerprintTheRequesterOrASecret()
    {
        var (operation, replica, claim) = Claimed();
        var json = JsonSerializer.Serialize(RestoreService.ToSnapshot(claim));

        Assert.DoesNotContain(operation.IdempotencyKey, json);
        Assert.DoesNotContain(operation.RequestFingerprint, json);
        Assert.DoesNotContain(RequestedUserId.ToString(), json);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(replica.Key, json);
        // The only filesystem-shaped value is the relative storage key.
        Assert.DoesNotContain("\\\\", json);
    }

    // ---- ToView ----

    [Fact]
    public void APendingRestoreIsProjectedWithNoOutcomeYet()
    {
        var operation = Queued();
        var view = RestoreService.ToView(operation, includeResult: true);

        Assert.Equal(operation.Id, view.Id);
        Assert.Equal(operation.ArtifactId, view.ArtifactId);
        Assert.Equal(operation.ReplicaId, view.ReplicaId);
        Assert.Equal(operation.AgentId.Value, view.AgentId);
        Assert.Equal("Pending", view.Status);
        Assert.Equal("NewDatabase", view.Mode);
        Assert.Equal("Forbidden", view.OverwritePolicy);
        Assert.Equal("CentralStorage", view.SourceAccess);
        Assert.Equal("Sales", view.DatabaseName);
        Assert.Equal(new RestoreTargetView("sql01.corp.local", 1433, false, "sql-cred_1", true, false), view.Target);
        Assert.Equal(new RestoreOptionsView(false, true, true), view.Options);
        Assert.Equal(Now, view.CreatedAt);
        Assert.Null(view.StartedAt);
        Assert.Null(view.CompletedAt);
        Assert.Null(view.CancellationRequestedAt);
        Assert.Null(view.FailureCode);
        Assert.Null(view.Reason);
        Assert.Null(view.TargetState);
        Assert.Null(view.TargetStateSummary);
        Assert.Equal(new RestoreProgressView(null, null, null), view.Progress);
        Assert.True(view.ReplicaPinned);
        Assert.Null(view.Result);

        var attempt = Assert.Single(view.Attempts);
        Assert.Equal(operation.CurrentAttempt.Id, attempt.Id);
        Assert.Equal(1, attempt.Number);
        Assert.Equal("Pending", attempt.Status);
        Assert.Equal(Now, attempt.CreatedAt);
        Assert.Null(attempt.LeasedAt);
        Assert.Null(attempt.StartedAt);
        Assert.Null(attempt.CompletedAt);
        Assert.Null(attempt.FailureCode);
        Assert.Null(attempt.Reason);
        Assert.Null(attempt.TargetState);
        Assert.Null(attempt.ProgressStage);
    }

    [Fact]
    public void ARunningRestoreShowsItsLatestProgressAndStaysPinned()
    {
        var operation = Running(out var token);
        operation.Progress(token, Now.AddSeconds(30), RestoreStages.Restoring, 42.5);

        var view = RestoreService.ToView(operation, includeResult: true);

        Assert.Equal("Running", view.Status);
        Assert.Equal(Now, view.StartedAt);
        Assert.Null(view.CompletedAt);
        Assert.Equal(new RestoreProgressView(RestoreStages.Restoring, 42.5, Now.AddSeconds(30)), view.Progress);
        Assert.True(view.ReplicaPinned);
        Assert.Null(view.TargetState);
        Assert.Null(view.TargetStateSummary);
        var attempt = Assert.Single(view.Attempts);
        Assert.Equal("Running", attempt.Status);
        Assert.Equal(Now, attempt.LeasedAt);
        Assert.Equal(Now, attempt.StartedAt);
        Assert.Equal(RestoreStages.Restoring, attempt.ProgressStage);
        Assert.Equal(42.5, attempt.ProgressPercent);
        Assert.Equal(Now.AddSeconds(30), attempt.ProgressAt);
    }

    [Fact]
    public void ASucceededRestoreShowsTheOnlineTargetAndReleasesThePinInTheView()
    {
        var operation = SucceededWithResult();
        var view = RestoreService.ToView(operation, includeResult: true);

        Assert.Equal("Succeeded", view.Status);
        Assert.Equal("Online", view.TargetState);
        Assert.Equal("The restored database is online.", view.TargetStateSummary);
        Assert.Null(view.FailureCode);
        Assert.Null(view.Reason);
        Assert.Equal(Now.AddMinutes(1), view.CompletedAt);
        Assert.False(view.ReplicaPinned);
        Assert.Equal(new RestoreProgressView(RestoreStages.Completed, 100, Now.AddMinutes(1)), view.Progress);
        Assert.Equal("Succeeded", Assert.Single(view.Attempts).Status);
        Assert.Equal("Online", Assert.Single(view.Attempts).TargetState);
    }

    [Fact]
    public void TheResultIsOnlyIncludedWhenAsked()
    {
        var operation = SucceededWithResult();

        var detailed = RestoreService.ToView(operation, includeResult: true);
        Assert.NotNull(detailed.Result);
        Assert.True(detailed.Result.ChecksumVerified);
        Assert.True(detailed.Result.BackupVerified);
        Assert.Equal("ONLINE", detailed.Result.DatabaseState);
        Assert.Equal("A warning.", Assert.Single(detailed.Result.Warnings).Message);
        Assert.Equal("/data/Sales_Restored.mdf", Assert.Single(detailed.Result.FileMappings).TargetPhysicalName);

        Assert.Null(RestoreService.ToView(operation, includeResult: false).Result);
    }

    [Fact]
    public void AnUnreadableStoredResultIsShownAsNoResult()
    {
        var operation = Terminal(new RestoreCompletion(RestoreStatus.Succeeded, null, RestoreTargetState.Online, "this is not json"));
        Assert.Null(RestoreService.ToView(operation, includeResult: true).Result);
    }

    [Fact]
    public void AFailedRestoreShowsItsCodeASentenceAndWhatWasLeftOnTheTarget()
    {
        var operation = Terminal(new RestoreCompletion(RestoreStatus.Failed, RestoreFailureCodes.SqlRestoreFailed, RestoreTargetState.PartiallyRestored, null));
        var view = RestoreService.ToView(operation, includeResult: true);

        Assert.Equal("Failed", view.Status);
        Assert.Equal("SqlRestoreFailed", view.FailureCode);
        Assert.Equal(RestoreService.Reason(RestoreFailureCodes.SqlRestoreFailed), view.Reason);
        Assert.Equal("PartiallyRestored", view.TargetState);
        Assert.Equal(RestoreService.TargetStateSummary(RestoreTargetState.PartiallyRestored), view.TargetStateSummary);
        Assert.False(view.ReplicaPinned);
        var attempt = Assert.Single(view.Attempts);
        Assert.Equal("SqlRestoreFailed", attempt.FailureCode);
        Assert.Equal(view.Reason, attempt.Reason);
        Assert.Equal("PartiallyRestored", attempt.TargetState);
        Assert.Equal(RestoreStages.Failed, view.Progress.Stage);
    }

    [Fact]
    public void ACancelledPendingRestoreShowsTheCancellationAndAnUntouchedTarget()
    {
        var operation = Queued();
        operation.RequestCancellation(Now.AddSeconds(30));
        var view = RestoreService.ToView(operation, includeResult: true);

        Assert.Equal("Cancelled", view.Status);
        Assert.Equal("Cancelled", view.FailureCode);
        Assert.Equal("The restore was cancelled.", view.Reason);
        Assert.Equal("NotModified", view.TargetState);
        Assert.Equal("The target server was not modified.", view.TargetStateSummary);
        Assert.Equal(Now.AddSeconds(30), view.CancellationRequestedAt);
        Assert.Equal(Now.AddSeconds(30), view.CompletedAt);
        Assert.False(view.ReplicaPinned);
        Assert.Null(view.Result);
        Assert.Equal(RestoreStages.Cancelled, view.Progress.Stage);
        Assert.Equal("Cancelled", Assert.Single(view.Attempts).Status);
    }

    [Fact]
    public void ACancellationRequestOnARunningRestoreIsVisibleWhileItIsStillActive()
    {
        var operation = Running(out _);
        operation.RequestCancellation(Now.AddSeconds(30));
        var view = RestoreService.ToView(operation, includeResult: false);

        Assert.Equal("Running", view.Status);
        Assert.Equal(Now.AddSeconds(30), view.CancellationRequestedAt);
        Assert.True(view.ReplicaPinned);
        Assert.Null(view.CompletedAt);
        Assert.Null(view.FailureCode);
    }

    [Fact]
    public void AnAgentLostRestoreShowsAnUnknownTargetAndTheLeaseSentence()
    {
        var operation = Running(out _);
        operation.Expire(Now + Lease);
        var view = RestoreService.ToView(operation, includeResult: true);

        Assert.Equal("AgentLost", view.Status);
        Assert.Equal("AgentLost", view.FailureCode);
        Assert.Equal(RestoreService.Reason(RestoreFailureCodes.AgentLost), view.Reason);
        Assert.Equal("Unknown", view.TargetState);
        Assert.Equal(RestoreService.TargetStateSummary(RestoreTargetState.Unknown), view.TargetStateSummary);
        Assert.False(view.ReplicaPinned);
        var attempt = Assert.Single(view.Attempts);
        Assert.Equal("AgentLost", attempt.Status);
        Assert.Equal("AgentLost", attempt.FailureCode);
        Assert.Equal(view.Reason, attempt.Reason);
        Assert.Equal("Unknown", attempt.TargetState);
    }

    [Fact]
    public void AnUnclaimedRestoreShowsTheNotClaimedSentence()
    {
        var operation = Queued();
        operation.ExpireUnclaimed(Now + TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        var view = RestoreService.ToView(operation, includeResult: false);

        Assert.Equal("Cancelled", view.Status);
        Assert.Equal("NotClaimed", view.FailureCode);
        Assert.Equal(RestoreService.Reason(RestoreFailureCodes.NotClaimed), view.Reason);
        Assert.Equal("NotModified", view.TargetState);
        Assert.Null(view.CancellationRequestedAt);
    }

    [Fact]
    public void AReplaceRestoreIsProjectedWithItsOverwritePolicy()
    {
        var operation = Queued(RestoreMode.ReplaceExistingDatabase);
        var view = RestoreService.ToView(operation, includeResult: false);

        Assert.Equal("ReplaceExistingDatabase", view.Mode);
        Assert.Equal("ReplaceExisting", view.OverwritePolicy);
    }

    [Fact]
    public void AnAgentLocalRestoreIsProjectedWithItsSourceAccessAndCustomOptions()
    {
        var operation = Queued(access: RestoreSourceAccess.AgentLocal, options: new RestoreOptions(true, false, false));
        var view = RestoreService.ToView(operation, includeResult: false);

        Assert.Equal("AgentLocal", view.SourceAccess);
        Assert.Equal(new RestoreOptionsView(true, false, false), view.Options);
    }

    [Fact]
    public void TheViewNeverCarriesTheIdempotencyKeyOrTheFingerprint()
    {
        var operation = SucceededWithResult();
        var json = JsonSerializer.Serialize(RestoreService.ToView(operation, includeResult: true));

        Assert.DoesNotContain(operation.IdempotencyKey, json);
        Assert.DoesNotContain(operation.RequestFingerprint, json);
        Assert.DoesNotContain(RequestedUserId.ToString(), json);
    }
}
