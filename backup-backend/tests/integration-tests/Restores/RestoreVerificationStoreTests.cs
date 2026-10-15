using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Restores;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Infrastructure.Restores;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.RestoreWorld;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// Isolated restore verification and the cleanup it owes, through the real store and database: the backend names the disposable database before anything exists, an
/// agent that dies leaves a tracked removal rather than an unknown leftover, removal is leased, fenced, tenant-bound and bounded, and a rollback copy is only ever removed
/// after its guarded retention and an explicit request.
/// </summary>
public sealed class RestoreVerificationStoreTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string RollbackName = "bp_old_0123456789abcdef0123456789abcdef";
    private static readonly TimeSpan CleanupLease = TimeSpan.FromMinutes(15);
    private readonly RestoreWorld _world = new(fixture);
    private static DateTimeOffset At(int seconds) => Now.AddSeconds(seconds);

    private async Task EnableVerificationAsync(StoreAgent agent)
    {
        await using var context = fixture.CreatePlatformContext();
        context.AgentCapabilities.Add(new AgentCapabilityRecord(agent.AgentId, agent.TenantId, RestoreTaskTypes.MssqlVerificationCapability, CapabilitySupport.Supported, null, "revision-2", Accepted));
        await context.SaveChangesAsync();
    }

    private static RestoreCommand VerificationCommand(RestoreSeed seed, DateTimeOffset? now = null, string? key = null) =>
        Command(seed, key, now: now, databaseName: PlatformDatabaseNames.NewVerificationName()) with { Purpose = RestorePurpose.Verification };

    private async Task<RestoreOperation> CreateVerificationAsync(RestoreSeed seed, PostgreSqlRestoreStore? store = null, DateTimeOffset? now = null)
    {
        var result = await (store ?? _world.Store()).CreateAsync(VerificationCommand(seed, now), default);
        Assert.Equal(RestoreCreateOutcome.Created, result.Outcome);
        return result.Operation!;
    }

    private const string PassedReport = "{\"verification\":{\"checks\":[{\"checkId\":\"integrity\",\"status\":\"Passed\",\"reasonCode\":\"ok\",\"message\":\"m\"}],\"cleanup\":\"Dropped\"}}";

    private static RestoreCompletion Passed(bool removed) => new(RestoreStatus.Succeeded, null, RestoreTargetState.NotModified, PassedReport, removed);

    private async Task<RestoreSeed> SeedAsync()
    {
        var seed = await _world.SeedCentralAsync();
        await EnableVerificationAsync(seed.Agent);
        return seed;
    }

    [Fact]
    public async Task AVerificationNeedsTheAgentsVerificationCapabilityNotJustTheRestoreOne()
    {
        var seed = await _world.SeedCentralAsync();
        var refused = await _world.Store().CreateAsync(VerificationCommand(seed), default);
        Assert.Equal(RestoreCreateOutcome.AgentCannotRestore, refused.Outcome);
        Assert.Equal(0, await _world.CountOperationsAsync(seed.ArtifactId));

        await EnableVerificationAsync(seed.Agent);
        var operation = await CreateVerificationAsync(seed);
        Assert.Equal(RestorePurpose.Verification, operation.Purpose);
        Assert.Equal(RestoreCleanupKind.VerificationDatabase, operation.Cleanup.Kind);
        Assert.Equal(RestoreCleanupState.Idle, operation.Cleanup.State);
        Assert.Equal(1, await _world.ActivePinsAsync(seed.ReplicaId));
    }

    [Fact]
    public async Task AUserRestoreCanNeverUseAReservedNameAndAVerificationCanNeverUseAnyOther()
    {
        var seed = await SeedAsync();
        foreach (var name in new[] { PlatformDatabaseNames.NewVerificationName(), RollbackName })
            await Assert.ThrowsAsync<InvalidRestoreRequestException>(() => _world.Store().CreateAsync(Command(seed, databaseName: name), default));
        await Assert.ThrowsAsync<InvalidRestoreRequestException>(() => _world.Store().CreateAsync(Command(seed, databaseName: "Sales") with { Purpose = RestorePurpose.Verification }, default));
        Assert.Equal(0, await _world.CountOperationsAsync(seed.ArtifactId));
    }

    [Fact]
    public async Task APassedVerificationWithItsDatabaseDroppedIsCompleteAndAudited()
    {
        var seed = await SeedAsync();
        var store = _world.Store();
        var operation = await CreateVerificationAsync(seed, store, At(0));
        var claim = await RunToAsync(store, seed.Agent, At(1));

        Assert.Equal(BackupMutationResult.Accepted, await CompleteAsync(store, claim, At(30), Passed(removed: true)));

        var persisted = await _world.OperationAsync(operation.Id);
        Assert.Equal((RestoreStatus.Succeeded, RestoreTargetState.NotModified, RestoreCleanupState.Completed), (persisted.Status, persisted.TargetState!.Value, persisted.Cleanup.State));
        Assert.Equal(operation.DatabaseName, persisted.Cleanup.DatabaseName);
        Assert.Equal(0, await _world.ActivePinsAsync(seed.ReplicaId));
        Assert.Null(await store.ClaimCleanupAsync(seed.TenantId, seed.AgentId, At(60), CleanupLease, default));
        await using var context = fixture.CreatePlatformContext();
        var actions = await context.AuditEvents.AsNoTracking().Where(row => row.TargetId == operation.Id.ToString("D")).Select(row => row.Action).ToListAsync();
        Assert.Contains("restore.verification.requested", actions);
        Assert.Contains("restore.verification.completed", actions);
        Assert.DoesNotContain("restore.cleanup.requested", actions);
    }

    [Fact]
    public async Task AVerificationSuccessNeedsTheEnginesOwnPassedChecks()
    {
        var seed = await SeedAsync();
        var store = _world.Store();
        await CreateVerificationAsync(seed, store, At(0));
        var claim = await RunToAsync(store, seed.Agent, At(1));

        // No report, or a failed check, cannot be a success; the lease stays and the agent may report the truth.
        Assert.Equal(BackupMutationResult.Conflict, await CompleteAsync(store, claim, At(30), new RestoreCompletion(RestoreStatus.Succeeded, null, RestoreTargetState.NotModified, null, true)));
        var failedCheck = "{\"verification\":{\"checks\":[{\"checkId\":\"integrity\",\"status\":\"Failed\",\"reasonCode\":\"x\",\"message\":\"m\"}],\"cleanup\":\"Dropped\"}}";
        Assert.Equal(BackupMutationResult.Conflict, await CompleteAsync(store, claim, At(30), new RestoreCompletion(RestoreStatus.Succeeded, null, RestoreTargetState.NotModified, failedCheck, true)));
        Assert.Equal(RestoreStatus.Running, (await _world.OperationAsync(claim.Operation.Id)).Status);

        Assert.Equal(BackupMutationResult.Accepted, await CompleteAsync(store, claim, At(31), Passed(removed: true)));
    }

    [Fact]
    public async Task ADeadAgentLeavesATrackedRemovalWhichTheAgentLeasesFencedAndCompletes()
    {
        var seed = await SeedAsync();
        var store = _world.Store();
        var operation = await CreateVerificationAsync(seed, store, At(0));
        var claim = await RunToAsync(store, seed.Agent, At(1));

        // The agent never reports; the lease expires.
        // The database is shared by this class's tests, so other tests' expired leases may be swept too.
        Assert.True(await store.ExpireStaleAsync(At(1) + Lease + TimeSpan.FromSeconds(1), default) >= 1);
        var lost = await _world.OperationAsync(operation.Id);
        Assert.Equal((RestoreStatus.AgentLost, RestoreCleanupState.Pending), (lost.Status, lost.Cleanup.State));

        var leasedAt = At(1) + Lease + TimeSpan.FromSeconds(5);
        var cleanup = await store.ClaimCleanupAsync(seed.TenantId, seed.AgentId, leasedAt, CleanupLease, default);
        Assert.NotNull(cleanup);
        Assert.Equal(operation.Id, cleanup.Operation.Id);
        Assert.Equal(operation.DatabaseName, cleanup.Operation.Cleanup.DatabaseName);
        Assert.Equal("mssql", cleanup.Engine);
        // Leased once: a second poll gets nothing, and a wrong token or another operation's id is refused.
        Assert.Null(await _world.Store().ClaimCleanupAsync(seed.TenantId, seed.AgentId, leasedAt.AddSeconds(1), CleanupLease, default));
        Assert.Equal(BackupMutationResult.Conflict, await store.CompleteCleanupAsync(seed.TenantId, seed.AgentId, operation.Id, Guid.NewGuid(), true, null, leasedAt.AddSeconds(2), default));
        Assert.Equal(BackupMutationResult.NotFound, await store.CompleteCleanupAsync(seed.TenantId, seed.AgentId, Guid.NewGuid(), cleanup.LeaseToken, true, null, leasedAt.AddSeconds(2), default));

        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteCleanupAsync(seed.TenantId, seed.AgentId, operation.Id, cleanup.LeaseToken, true, null, leasedAt.AddSeconds(3), default));
        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteCleanupAsync(seed.TenantId, seed.AgentId, operation.Id, cleanup.LeaseToken, true, null, leasedAt.AddSeconds(4), default));
        Assert.Equal(RestoreCleanupState.Completed, (await _world.OperationAsync(operation.Id)).Cleanup.State);
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(1, await context.AuditEvents.CountAsync(row => row.TargetId == operation.Id.ToString("D") && row.Action == "restore.cleanup.completed"));
    }

    [Fact]
    public async Task AFailedRemovalBacksOffIsRetriedAndParksAfterItsBudgetUntilAnOperatorRetries()
    {
        var seed = await SeedAsync();
        var store = _world.Store();
        var operation = await CreateVerificationAsync(seed, store, At(0));
        var claim = await RunToAsync(store, seed.Agent, At(1));
        await CompleteAsync(store, claim, At(10), Passed(removed: false));
        var clock = At(20);

        for (var attempt = 1; attempt <= RestoreCleanup.MaxAutomaticAttempts; attempt++)
        {
            var leased = await store.ClaimCleanupAsync(seed.TenantId, seed.AgentId, clock, CleanupLease, default);
            Assert.NotNull(leased);
            Assert.Equal(BackupMutationResult.Accepted, await store.CompleteCleanupAsync(seed.TenantId, seed.AgentId, operation.Id, leased.LeaseToken, false, "database-in-use", clock.AddSeconds(1), default));
            var row = await _world.OperationAsync(operation.Id);
            Assert.Equal((attempt, "database-in-use"), (row.Cleanup.Attempts, row.Cleanup.LastFailureCode));
            if (attempt == RestoreCleanup.MaxAutomaticAttempts) break;
            // Not due yet: nothing is offered before the backoff has passed.
            Assert.Null(await store.ClaimCleanupAsync(seed.TenantId, seed.AgentId, clock.AddSeconds(2), CleanupLease, default));
            clock = row.Cleanup.NextAttemptAt!.Value;
        }

        var parked = await _world.OperationAsync(operation.Id);
        Assert.Null(parked.Cleanup.NextAttemptAt);
        Assert.True(parked.Cleanup.NeedsAttention(clock.AddDays(30)));
        Assert.Null(await store.ClaimCleanupAsync(seed.TenantId, seed.AgentId, clock.AddDays(30), CleanupLease, default));

        var retry = await store.RequestCleanupAsync(seed.TenantId, operation.Id, clock.AddDays(30), TimeSpan.FromDays(7), Guid.NewGuid(), default);
        Assert.Equal(RestoreCleanupRequestOutcome.Accepted, retry.Outcome);
        Assert.NotNull(await store.ClaimCleanupAsync(seed.TenantId, seed.AgentId, clock.AddDays(30), CleanupLease, default));
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(1, await context.AuditEvents.CountAsync(row => row.TargetId == operation.Id.ToString("D") && row.Action == "restore.cleanup.requested"));
        Assert.Equal(RestoreCleanup.MaxAutomaticAttempts, await context.AuditEvents.CountAsync(row => row.TargetId == operation.Id.ToString("D") && row.Action == "restore.cleanup.failed"));
    }

    [Fact]
    public async Task ARemovalIsNeverLeasedToAnotherTenantOrAnotherAgentAndNeverBesideALiveRestore()
    {
        var seed = await SeedAsync();
        var other = await SeedAsync();
        var store = _world.Store();
        var operation = await CreateVerificationAsync(seed, store, At(0));
        var claim = await RunToAsync(store, seed.Agent, At(1));
        await CompleteAsync(store, claim, At(10), Passed(removed: false));

        Assert.Null(await store.ClaimCleanupAsync(other.TenantId, other.AgentId, At(60), CleanupLease, default));
        Assert.Null(await store.ClaimCleanupAsync(seed.TenantId, other.AgentId, At(60), CleanupLease, default));
        var sibling = await _world.AddAgentAsync(seed.TenantId);
        Assert.Null(await store.ClaimCleanupAsync(seed.TenantId, sibling.AgentId, At(60), CleanupLease, default));
        Assert.Equal(RestoreCleanupRequestOutcome.NotFound, (await store.RequestCleanupAsync(other.TenantId, operation.Id, At(60), TimeSpan.Zero, null, default)).Outcome);
        Assert.Equal(BackupMutationResult.NotFound, await store.CompleteCleanupAsync(other.TenantId, other.AgentId, operation.Id, Guid.NewGuid(), true, null, At(60), default));

        // The owning agent is busy with a live restore: housekeeping waits.
        await _world.CreateAsync(seed, store, At(20));
        await MustClaimAsync(store, seed.Agent, At(21));
        Assert.Null(await store.ClaimCleanupAsync(seed.TenantId, seed.AgentId, At(22), CleanupLease, default));
    }

    [Fact]
    public async Task ACancelledAndAnUnclaimedVerificationNeverCreatedAnythingSoOweNothing()
    {
        var seed = await SeedAsync();
        var store = _world.Store();
        var withdrawn = await CreateVerificationAsync(seed, store, At(0));
        await store.RequestCancellationAsync(seed.TenantId, withdrawn.Id, At(5), default, Guid.NewGuid());
        var unclaimed = await CreateVerificationAsync(seed, store, At(6));
        await store.ExpireUnclaimedAsync(At(6) + Unclaimed + TimeSpan.FromSeconds(1), Unclaimed, default);

        foreach (var id in new[] { withdrawn.Id, unclaimed.Id })
        {
            var row = await _world.OperationAsync(id);
            Assert.Equal(RestoreStatus.Cancelled, row.Status);
            Assert.Equal(RestoreCleanupState.Completed, row.Cleanup.State);
        }
        Assert.Null(await store.ClaimCleanupAsync(seed.TenantId, seed.AgentId, At(5000), CleanupLease, default));
    }

    [Fact]
    public async Task VerificationsAreListedOnlyWhenAskedForAndTheirTenantNeverSeesAnothers()
    {
        var seed = await SeedAsync();
        var other = await SeedAsync();
        var store = _world.Store();
        var verification = await CreateVerificationAsync(seed, store, At(0));
        var restore = await _world.CreateAsync(seed, store, At(1));
        await CreateVerificationAsync(other, store, At(2));

        var restores = await store.ListAsync(seed.TenantId, new RestoreQuery(), default);
        Assert.Equal([restore.Id], restores.Items.Select(item => item.Id));
        var verifications = await store.ListAsync(seed.TenantId, new RestoreQuery(ArtifactId: seed.ArtifactId, Purpose: RestorePurpose.Verification), default);
        Assert.Equal([verification.Id], verifications.Items.Select(item => item.Id));
        Assert.Empty((await store.ListAsync(other.TenantId, new RestoreQuery(ArtifactId: seed.ArtifactId, Purpose: RestorePurpose.Verification), default)).Items);
    }

    // ---- the rollback copy -------------------------------------------------------------------------------------------------

    private async Task<RestoreOperation> ReplacedWithRollbackAsync(RestoreSeed seed, PostgreSqlRestoreStore store)
    {
        var operation = await _world.CreateAsync(seed, store, At(0), mode: RestoreMode.ReplaceExistingDatabase, databaseName: "Sales");
        var claim = await RunToAsync(store, seed.Agent, At(1));
        Assert.Equal(BackupMutationResult.Accepted, await CompleteAsync(store, claim, At(30),
            new RestoreCompletion(RestoreStatus.Succeeded, null, RestoreTargetState.Online, "{\"databaseState\":\"ONLINE\"}", RetainedDatabaseName: RollbackName)));
        return await _world.OperationAsync(operation.Id);
    }

    [Fact]
    public async Task ARollbackCopyIsRetainedNeverLeasedByItselfAndRemovedOnlyAfterItsRetentionAndAnExplicitRequest()
    {
        var seed = await SeedAsync();
        var store = _world.Store();
        var operation = await ReplacedWithRollbackAsync(seed, store);
        var retention = TimeSpan.FromDays(7);
        Assert.Equal((RestoreCleanupKind.RollbackCopy, RestoreCleanupState.Retained, RollbackName), (operation.Cleanup.Kind, operation.Cleanup.State, operation.Cleanup.DatabaseName));
        Assert.Null(await store.ClaimCleanupAsync(seed.TenantId, seed.AgentId, At(0).AddDays(400), CleanupLease, default));

        var early = await store.RequestCleanupAsync(seed.TenantId, operation.Id, At(30).AddDays(1), retention, Guid.NewGuid(), default);
        Assert.Equal(RestoreCleanupRequestOutcome.RetentionNotElapsed, early.Outcome);
        Assert.Equal(At(30) + retention, early.RetainedUntil);
        Assert.Equal(RestoreCleanupState.Retained, (await _world.OperationAsync(operation.Id)).Cleanup.State);

        var admin = Guid.NewGuid();
        var accepted = await store.RequestCleanupAsync(seed.TenantId, operation.Id, At(30) + retention, retention, admin, default);
        Assert.Equal(RestoreCleanupRequestOutcome.Accepted, accepted.Outcome);
        var lease = await store.ClaimCleanupAsync(seed.TenantId, seed.AgentId, At(30) + retention, CleanupLease, default);
        Assert.Equal((RestoreCleanupKind.RollbackCopy, RollbackName), (lease!.Operation.Cleanup.Kind, lease.Operation.Cleanup.DatabaseName));
        Assert.Equal(BackupMutationResult.Accepted, await store.CompleteCleanupAsync(seed.TenantId, seed.AgentId, operation.Id, lease.LeaseToken, true, null, At(31) + retention, default));

        await using var context = fixture.CreatePlatformContext();
        var audit = await context.AuditEvents.AsNoTracking().SingleAsync(row => row.TargetId == operation.Id.ToString("D") && row.Action == "restore.cleanup.requested");
        Assert.Equal((AuditActorKind.User, admin), (audit.ActorKind, audit.ActorUserId.Value));
    }

    [Fact]
    public async Task ARollbackCopyOfAnotherTenantsRestoreCannotBeRequestedForRemoval()
    {
        var seed = await SeedAsync();
        var other = await SeedAsync();
        var store = _world.Store();
        var operation = await ReplacedWithRollbackAsync(seed, store);

        var result = await store.RequestCleanupAsync(other.TenantId, operation.Id, At(30).AddDays(30), TimeSpan.Zero, Guid.NewGuid(), default);

        Assert.Equal(RestoreCleanupRequestOutcome.NotFound, result.Outcome);
        Assert.Equal(RestoreCleanupState.Retained, (await _world.OperationAsync(operation.Id)).Cleanup.State);
    }

    [Fact]
    public async Task TheDatabaseRefusesACleanupRecordThatDisagreesWithTheOperation()
    {
        var seed = await SeedAsync();
        var operation = await CreateVerificationAsync(seed);
        await Assert.ThrowsAnyAsync<Exception>(() => _world.ExecuteAsync("UPDATE restore_operations SET \"CleanupDatabaseName\" = 'bp_verify_other' WHERE \"Id\" = @id", ("id", operation.Id)));
        await Assert.ThrowsAnyAsync<Exception>(() => _world.ExecuteAsync("UPDATE restore_operations SET \"CleanupState\" = 'Pending' WHERE \"Id\" = @id", ("id", operation.Id)));
        await Assert.ThrowsAnyAsync<Exception>(() => _world.ExecuteAsync("UPDATE restore_operations SET \"Purpose\" = 'Execution' WHERE \"Id\" = @id", ("id", operation.Id)));
    }
}
