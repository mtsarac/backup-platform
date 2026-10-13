using System.Net;
using System.Text.Json;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using static BackupPlatform.IntegrationTests.BackupApiScenario;
using static BackupPlatform.IntegrationTests.RetentionApiSupport;

namespace BackupPlatform.IntegrationTests;

public sealed class RetentionAuditIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private readonly RetentionWorld world = new(fixture);
    private static readonly DateTimeOffset Now = RetentionWorld.Accepted.AddHours(1);

    [Fact]
    public async Task ManualTransitionsCarryActualActorsAndIgnoreDuplicatesNoOpsAndOtherTenants()
    {
        var backup = await world.SeedAsync(RetentionKit.Policy(enabled: false));
        var requester = Guid.NewGuid();
        var canceller = Guid.NewGuid();
        var store = world.Store();
        Assert.Equal(BackupArtifactCancelKind.NothingToCancel, (await store.CancelDeleteAsync(backup.TenantId, backup.ArtifactId, Now, default, canceller)).Kind);
        Assert.Equal(BackupArtifactDeleteKind.NotFound, (await store.RequestDeleteAsync(TenantId.New(), backup.ArtifactId, requester, Now, default)).Kind);
        Assert.Equal(BackupArtifactCancelKind.NotFound, (await store.CancelDeleteAsync(TenantId.New(), backup.ArtifactId, Now, default, canceller)).Kind);
        Assert.Equal(BackupArtifactDeleteKind.Requested, (await store.RequestDeleteAsync(backup.TenantId, backup.ArtifactId, requester, Now, default)).Kind);
        Assert.Equal(BackupArtifactDeleteKind.AlreadyInProgress, (await store.RequestDeleteAsync(backup.TenantId, backup.ArtifactId, requester, Now, default)).Kind);
        Assert.Equal(BackupArtifactCancelKind.Restored, (await store.CancelDeleteAsync(backup.TenantId, backup.ArtifactId, Now.AddMinutes(1), default, canceller)).Kind);
        Assert.Equal(BackupArtifactCancelKind.NothingToCancel, (await store.CancelDeleteAsync(backup.TenantId, backup.ArtifactId, Now.AddMinutes(1), default, canceller)).Kind);

        var events = await EventsAsync(backup.ArtifactId);
        Assert.Equal(2, events.Count);
        AssertEvent(Assert.Single(events, e => e.Action == AuditActions.BackupArtifactDeleteRequested), backup.TenantId, new(requester), AuditActorKind.TenantAdministrator, Now, "delete_pending");
        AssertEvent(Assert.Single(events, e => e.Action == AuditActions.BackupArtifactDeleteCancelled), backup.TenantId, new(canceller), AuditActorKind.TenantAdministrator, Now.AddMinutes(1), "restored");
        Assert.All(events, e => Assert.DoesNotContain(backup.Key, e.DetailsJson));
    }

    [Fact]
    public async Task ConcurrentDoubleTriggersAppendOneEventPerCommittedTransition()
    {
        var backup = await world.SeedAsync(RetentionKit.Policy(enabled: false));
        var user = Guid.NewGuid();
        var gate = new RaceGate();
        var requests = Enumerable.Range(0, 6).Select(_ => gate.Run(() => world.Store().RequestDeleteAsync(backup.TenantId, backup.ArtifactId, user, Now, default))).ToArray();
        gate.Open();
        Assert.Single(await Task.WhenAll(requests), result => result.Kind == BackupArtifactDeleteKind.Requested);
        gate = new RaceGate();
        var cancellations = Enumerable.Range(0, 6).Select(_ => gate.Run(() => world.Store().CancelDeleteAsync(backup.TenantId, backup.ArtifactId, Now, default, user))).ToArray();
        gate.Open();
        Assert.Single(await Task.WhenAll(cancellations), result => result.Kind == BackupArtifactCancelKind.Restored);
        Assert.Equal(2, (await EventsAsync(backup.ArtifactId)).Count);
    }

    [Theory]
    [InlineData(AuditActions.BackupArtifactDeleteRequested)]
    [InlineData(AuditActions.BackupArtifactDeleteCancelled)]
    [InlineData(AuditActions.BackupArtifactRetentionApplied)]
    [InlineData(AuditActions.BackupReplicaPhysicalDeleteCompleted)]
    [InlineData(AuditActions.BackupReplicaPhysicalDeleteFailed)]
    public async Task AuditCommitFailureRollsBackTheCorrespondingStateTransition(string action)
    {
        var backup = await world.SeedAsync(RetentionKit.Policy(keepFor: TimeSpan.FromHours(1), grace: TimeSpan.FromSeconds(1)));
        var store = world.Store();
        ClaimedReplicaDeletion? claim = null;
        if (action is AuditActions.BackupArtifactDeleteCancelled or AuditActions.BackupReplicaPhysicalDeleteCompleted or AuditActions.BackupReplicaPhysicalDeleteFailed)
        {
            await store.RequestDeleteAsync(backup.TenantId, backup.ArtifactId, Guid.NewGuid(), Now, default);
            if (action != AuditActions.BackupArtifactDeleteCancelled)
                claim = await RetentionWorld.ClaimAsync(store, backup, Now.AddSeconds(1));
        }
        var before = await world.ReplicaAsync(backup.ReplicaId);
        var target = action is AuditActions.BackupReplicaPhysicalDeleteCompleted or AuditActions.BackupReplicaPhysicalDeleteFailed ? backup.ReplicaId : backup.ArtifactId;
        var failing = new PostgreSqlBackupRetentionStore(FailingOptions(fixture, action, target.ToString("D")));
        await Assert.ThrowsAsync<AuditCommitFailure>(async () =>
        {
            switch (action)
            {
                case AuditActions.BackupArtifactDeleteRequested:
                    await failing.RequestDeleteAsync(backup.TenantId, backup.ArtifactId, Guid.NewGuid(), Now, default);
                    break;
                case AuditActions.BackupArtifactDeleteCancelled:
                    await failing.CancelDeleteAsync(backup.TenantId, backup.ArtifactId, Now, default, Guid.NewGuid());
                    break;
                case AuditActions.BackupArtifactRetentionApplied:
                    await failing.ApplyExpiredRetentionAsync(Now, 1000, default);
                    break;
                case AuditActions.BackupReplicaPhysicalDeleteCompleted:
                    await failing.CompleteDeletionAsync(backup.ReplicaId, claim!.LeaseToken, Now.AddSeconds(1), default);
                    break;
                default:
                    await failing.FailDeletionAsync(backup.ReplicaId, claim!.LeaseToken, Now.AddSeconds(1), BackupDeleteFailureCodes.IoError, Now.AddMinutes(1), default);
                    break;
            }
        });
        var after = await world.ReplicaAsync(backup.ReplicaId);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.DeleteLeaseToken, after.DeleteLeaseToken);
        Assert.Null(after.DeletedAt);
        if (action == AuditActions.BackupArtifactRetentionApplied)
            Assert.Null((await world.ArtifactAsync(backup.ArtifactId)).RetentionAppliedAt);
        Assert.DoesNotContain(await EventsAsync(target), e => e.Action == action);
    }

    [Fact]
    public async Task FailedDeletesAndTakeoverDoNotPublishPhysicalCompletionAndUseSafeRetryDetails()
    {
        var backup = await world.SeedAsync(RetentionKit.Policy(enabled: false, grace: TimeSpan.FromSeconds(1)));
        var store = world.Store();
        await store.RequestDeleteAsync(backup.TenantId, backup.ArtifactId, null, Now, default);
        var first = (await RetentionWorld.ClaimAsync(store, backup, Now.AddSeconds(1)))!;
        Assert.False(await store.CompleteDeletionAsync(backup.ReplicaId, Guid.NewGuid(), Now, default));
        Assert.True(await store.FailDeletionAsync(backup.ReplicaId, first.LeaseToken, Now.AddSeconds(1), "secret-path-provider-message", Now.AddMinutes(1), default));
        Assert.False(await store.FailDeletionAsync(backup.ReplicaId, first.LeaseToken, Now.AddSeconds(1), BackupDeleteFailureCodes.IoError, Now.AddMinutes(1), default));
        var retry = (await RetentionWorld.ClaimAsync(store, backup, Now.AddMinutes(1)))!;
        var takeover = (await RetentionWorld.ClaimAsync(store, backup, Now.AddMinutes(1) + RetentionWorld.Lease))!;
        Assert.False(await store.CompleteDeletionAsync(backup.ReplicaId, retry.LeaseToken, Now.AddMinutes(7), default));
        Assert.True(await store.CompleteDeletionAsync(backup.ReplicaId, takeover.LeaseToken, Now.AddMinutes(7), default));
        Assert.False(await store.CompleteDeletionAsync(backup.ReplicaId, takeover.LeaseToken, Now.AddMinutes(7), default));
        var events = await EventsAsync(backup.ReplicaId);
        Assert.Equal(2, events.Count);
        var failure = Assert.Single(events, e => e.Action == AuditActions.BackupReplicaPhysicalDeleteFailed);
        AssertEvent(failure, backup.TenantId, UserId.System, AuditActorKind.System, Now.AddSeconds(1), "retry_scheduled");
        using var details = JsonDocument.Parse(failure.DetailsJson);
        Assert.Equal("unexpected", details.RootElement.GetProperty("failureCode").GetString());
        Assert.DoesNotContain("secret-path-provider-message", failure.DetailsJson);
        var completed = Assert.Single(events, e => e.Action == AuditActions.BackupReplicaPhysicalDeleteCompleted);
        AssertEvent(completed, backup.TenantId, UserId.System, AuditActorKind.System, Now.AddMinutes(7), "deleted");
    }

    [Fact]
    public async Task RetentionWorkerRecordsSoftDecisionBeforeGraceAndPhysicalCompletionOnlyAfterVerifiedAbsence()
    {
        await RetentionServiceWorld.RunAsync(async serviceWorld =>
        {
            var seed = await serviceWorld.SeedAsync(RetentionKit.Policy(keepFor: TimeSpan.FromHours(1), grace: TimeSpan.FromHours(1)));
            var clock = new MutableClock(Now);
            await serviceWorld.Service("audit-worker", clock).RunCycleAsync(default);
            await serviceWorld.Service("audit-worker", clock).RunCycleAsync(default);
            await using (var context = serviceWorld.Harness.Database.CreatePlatformContext())
            {
                var decisions = await context.AuditEvents.Where(e => e.TargetId == seed.Backup.ArtifactId.ToString("D")).ToListAsync();
                Assert.Single(decisions, e => e.Action == AuditActions.BackupArtifactRetentionApplied);
                Assert.False(await context.AuditEvents.AnyAsync(e => e.Action == AuditActions.BackupReplicaPhysicalDeleteCompleted));
            }
            Assert.True(serviceWorld.Exists(seed.Backup.Key));
            clock.Advance(TimeSpan.FromHours(1));
            await serviceWorld.Service("audit-worker", clock).RunCycleAsync(default);
            await serviceWorld.Service("audit-worker", clock).RunCycleAsync(default);
            Assert.False(serviceWorld.Exists(seed.Backup.Key));
            await using var verified = serviceWorld.Harness.Database.CreatePlatformContext();
            var completed = await verified.AuditEvents.SingleAsync(e => e.Action == AuditActions.BackupReplicaPhysicalDeleteCompleted);
            AssertEvent(completed, seed.Backup.TenantId, UserId.System, AuditActorKind.System, clock.GetUtcNow(), "deleted");
            using var details = JsonDocument.Parse(completed.DetailsJson);
            Assert.True(details.RootElement.GetProperty("absenceVerified").GetBoolean());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableOrphanAndQuarantineTransitionsAreAtomicIdempotentAndPathFree(bool quarantine)
    {
        var backup = await world.SeedAsync(RetentionKit.Policy(enabled: false));
        var connectionId = (await world.ReplicaAsync(backup.ReplicaId)).StorageConnectionId;
        var action = quarantine ? AuditActions.BackupReplicaQuarantined : AuditActions.BackupReplicaOrphanRecorded;
        var key = $"secret/repair-target-{Guid.NewGuid():N}.bak";
        var failing = new PostgreSqlReplicaRepairStore(FailingOptions(fixture, action));
        await Assert.ThrowsAsync<AuditCommitFailure>(() => failing.RecordRepairOrphanAsync(backup.TenantId, backup.ArtifactId, connectionId, "local", "local", key, 20, new string('a', 64), Now, default, quarantine));
        await using (var rolledBack = fixture.CreatePlatformContext())
            Assert.False(await rolledBack.BackupReplicas.AnyAsync(r => r.TenantId == backup.TenantId && r.Key == key));
        var store = new PostgreSqlReplicaRepairStore(fixture.Options);
        await store.RecordRepairOrphanAsync(TenantId.New(), backup.ArtifactId, connectionId, "local", "local", key, 20, new string('a', 64), Now, default, quarantine);
        await store.RecordRepairOrphanAsync(backup.TenantId, backup.ArtifactId, connectionId, "local", "local", key, 20, new string('a', 64), Now, default, quarantine);
        await store.RecordRepairOrphanAsync(backup.TenantId, backup.ArtifactId, connectionId, "local", "local", key, 20, new string('a', 64), Now.AddMinutes(1), default, quarantine);
        await using var context = fixture.CreatePlatformContext();
        var replica = await context.BackupReplicas.SingleAsync(r => r.TenantId == backup.TenantId && r.Key == key);
        var audit = Assert.Single(await EventsAsync(replica.Id));
        Assert.Equal(action, audit.Action);
        AssertEvent(audit, backup.TenantId, UserId.System, AuditActorKind.System, Now, quarantine ? "quarantined" : "delete_pending");
        Assert.DoesNotContain(key, audit.DetailsJson);
        Assert.DoesNotContain(".bak", audit.DetailsJson);
        Assert.Null(replica.DeletedAt);
    }

    [Fact]
    public async Task SweepCompletionIsVerifiedTenantAttributedOpaqueAndDeduplicated()
    {
        await RetentionServiceWorld.RunAsync(async serviceWorld =>
        {
            var ended = await serviceWorld.SeedFailedAttemptAsync(await serviceWorld.Harness.AddAgentAsync(), BackupFailureCodes.PreCheckFailed, RetentionWorld.Accepted);
            var key = ended.Key(prefix: "private-vault");
            var clock = new MutableClock(Now.AddDays(2));
            serviceWorld.Place(key, modified: Now.AddDays(-2));
            Assert.Equal(1, (await serviceWorld.Sweeper(clock).SweepAsync(default)).OrphansDeleted);
            Assert.False(serviceWorld.Exists(key));
            Assert.True(ManagedBackupLayout.TryParseStorageKey(key, out var parsed));

            // Second attempt to begin the same deletion recognizes completed status and yields null
            var claimedAgain = await serviceWorld.Store().BeginSweptDeletionAsync("local", "local:local", null, parsed!, key, clock.GetUtcNow(), TimeSpan.FromMinutes(10), default);
            Assert.Null(claimedAgain);

            // Cross-tenant mismatch is rejected
            var crossTenant = await serviceWorld.Store().BeginSweptDeletionAsync("local", "local:local", TenantId.New(), parsed!, key, clock.GetUtcNow(), TimeSpan.FromMinutes(10), default);
            Assert.Null(crossTenant);

            await using var context = serviceWorld.Harness.Database.CreatePlatformContext();
            var audit = await context.AuditEvents.SingleAsync(e => e.Action == AuditActions.BackupStorageOrphanDeleteCompleted);
            AssertEvent(audit, ended.TenantId, UserId.System, AuditActorKind.System, clock.GetUtcNow(), "deleted");
            Assert.Equal(64, audit.TargetId.Length);
            Assert.DoesNotContain(key, audit.DetailsJson);
            Assert.DoesNotContain("private-vault", audit.DetailsJson);
        });
    }

    [Fact]
    public async Task SweepCrashBeforePhysicalDeleteRecoversViaLedgerAndAbsenceVerification()
    {
        await RetentionServiceWorld.RunAsync(async serviceWorld =>
        {
            var ended = await serviceWorld.SeedFailedAttemptAsync(await serviceWorld.Harness.AddAgentAsync(), BackupFailureCodes.PreCheckFailed, RetentionWorld.Accepted);
            var key = ended.Key();
            var clock = new MutableClock(Now.AddDays(2));
            serviceWorld.Place(key, modified: Now.AddDays(-2));
            Assert.True(ManagedBackupLayout.TryParseStorageKey(key, out var parsed));

            // 1. Crash BEFORE physical delete: intent was persisted, lease expired
            var firstClaim = await serviceWorld.Store().BeginSweptDeletionAsync("local", "local:default", null, parsed!, key, clock.GetUtcNow().AddMinutes(-15), TimeSpan.FromMinutes(10), default);
            Assert.NotNull(firstClaim);
            Assert.True(serviceWorld.Exists(key));

            // 2. Next sweep cycle picks up ledger item, performs delete, verifies absence and completes
            var report = await serviceWorld.Sweeper(clock).SweepAsync(default);
            Assert.Equal(1, report.OrphansDeleted);
            Assert.False(serviceWorld.Exists(key));

            await using var context = serviceWorld.Harness.Database.CreatePlatformContext();
            var audit = await context.AuditEvents.SingleAsync(e => e.Action == AuditActions.BackupStorageOrphanDeleteCompleted);
            AssertEvent(audit, ended.TenantId, UserId.System, AuditActorKind.System, clock.GetUtcNow(), "deleted");
        });
    }

    [Fact]
    public async Task SweepCrashAfterPhysicalDeleteRecoversWhenFileVanishedFromEnumeration()
    {
        await RetentionServiceWorld.RunAsync(async serviceWorld =>
        {
            var ended = await serviceWorld.SeedFailedAttemptAsync(await serviceWorld.Harness.AddAgentAsync(), BackupFailureCodes.PreCheckFailed, RetentionWorld.Accepted);
            var key = ended.Key();
            var clock = new MutableClock(Now.AddDays(2));
            var filePath = serviceWorld.Place(key, modified: Now.AddDays(-2));
            Assert.True(ManagedBackupLayout.TryParseStorageKey(key, out var parsed));

            // 1. Intent persisted
            var firstClaim = await serviceWorld.Store().BeginSweptDeletionAsync("local", "local:local", null, parsed!, key, clock.GetUtcNow().AddMinutes(-15), TimeSpan.FromMinutes(10), default);
            Assert.NotNull(firstClaim);

            // 2. Physical delete happened, then node crashed before completing ledger/audit
            File.Delete(filePath);
            Assert.False(serviceWorld.Exists(key));

            // 3. File is gone from enumeration! Sweeper recovers from ledger directly, verifies absence and audits atomically
            var report = await serviceWorld.Sweeper(clock).SweepAsync(default);
            Assert.Equal(1, report.OrphansDeleted);

            await using var context = serviceWorld.Harness.Database.CreatePlatformContext();
            var audit = await context.AuditEvents.SingleAsync(e => e.Action == AuditActions.BackupStorageOrphanDeleteCompleted);
            AssertEvent(audit, ended.TenantId, UserId.System, AuditActorKind.System, clock.GetUtcNow(), "deleted");
        });
    }

    [Fact]
    public async Task SweepAuditCommitFailureRollsBackCleanupStateAndLeavesItRetryable()
    {
        await RetentionServiceWorld.RunAsync(async serviceWorld =>
        {
            var ended = await serviceWorld.SeedFailedAttemptAsync(await serviceWorld.Harness.AddAgentAsync(), BackupFailureCodes.PreCheckFailed, RetentionWorld.Accepted);
            var key = ended.Key();
            var clock = new MutableClock(Now.AddDays(2));
            serviceWorld.Place(key, modified: Now.AddDays(-2));
            Assert.True(ManagedBackupLayout.TryParseStorageKey(key, out var parsed));

            var failingStore = new PostgreSqlBackupRetentionStore(FailingOptions(serviceWorld.Harness.Database, AuditActions.BackupStorageOrphanDeleteCompleted));
            var claim = await failingStore.BeginSweptDeletionAsync("local", "local:default", null, parsed!, key, clock.GetUtcNow(), TimeSpan.FromMinutes(10), default);
            Assert.NotNull(claim);

            // Audit failure rolls back CompleteSweptDeletionAsync
            await Assert.ThrowsAsync<AuditCommitFailure>(() => failingStore.CompleteSweptDeletionAsync(claim!.Id, claim.LeaseToken, clock.GetUtcNow(), default));

            // Ledger row remains Pending
            await using (var context = serviceWorld.Harness.Database.CreatePlatformContext())
            {
                var row = await context.BackupStorageCleanups.SingleAsync(c => c.Id == claim!.Id);
                Assert.Equal(BackupStorageCleanupStatus.Pending, row.Status);
                Assert.Null(row.CompletedAt);
                Assert.False(await context.AuditEvents.AnyAsync(e => e.Action == AuditActions.BackupStorageOrphanDeleteCompleted));
            }

            // Normal store can now complete it successfully
            Assert.True(await serviceWorld.Store().CompleteSweptDeletionAsync(claim!.Id, claim.LeaseToken, clock.GetUtcNow(), default));

            await using (var context = serviceWorld.Harness.Database.CreatePlatformContext())
            {
                var row = await context.BackupStorageCleanups.SingleAsync(c => c.Id == claim!.Id);
                Assert.Equal(BackupStorageCleanupStatus.Completed, row.Status);
                Assert.NotNull(row.CompletedAt);
                Assert.Single(await context.AuditEvents.Where(e => e.Action == AuditActions.BackupStorageOrphanDeleteCompleted).ToListAsync());
            }
        });
    }

    [Fact]
    public async Task ConflictQuarantineIsLeaseFencedAtomicAndAppendedOnlyOnce()
    {
        var backup = await world.SeedAsync(RetentionKit.Policy(enabled: false));
        var connectionId = (await world.ReplicaAsync(backup.ReplicaId)).StorageConnectionId;
        var token = Guid.NewGuid();
        var targetKey = $"private/conflict-{Guid.NewGuid():N}.bak";
        var operation = new ReplicaRepairOperation(Guid.NewGuid(), backup.TenantId, backup.ArtifactId, connectionId,
            "local", backup.ReplicaId, Now, totalBytes: 100);
        operation.Claim(token, "repair-audit", Now, TimeSpan.FromMinutes(1));
        operation.SetTargetIntent(backup.ReplicaId, targetKey);
        await using (var setup = fixture.CreatePlatformContext())
        {
            setup.ReplicaRepairOperations.Add(operation);
            await setup.SaveChangesAsync();
        }
        var store = new PostgreSqlReplicaRepairStore(fixture.Options);
        Assert.False(await store.FailTargetConflictAsync(TenantId.New(), operation.Id, token, Now, default));
        Assert.False(await store.FailTargetConflictAsync(backup.TenantId, operation.Id, Guid.NewGuid(), Now, default));
        var failing = new PostgreSqlReplicaRepairStore(FailingOptions(fixture, AuditActions.BackupReplicaQuarantined));
        await Assert.ThrowsAsync<AuditCommitFailure>(() => failing.FailTargetConflictAsync(backup.TenantId, operation.Id, token, Now, default));
        await using (var rolledBack = fixture.CreatePlatformContext())
        {
            Assert.False(await rolledBack.BackupReplicas.AnyAsync(r => r.Key == targetKey));
            Assert.Equal(ReplicaRepairStatus.InProgress, (await rolledBack.ReplicaRepairOperations.SingleAsync(r => r.Id == operation.Id)).Status);
        }
        Assert.True(await store.FailTargetConflictAsync(backup.TenantId, operation.Id, token, Now, default));
        Assert.False(await store.FailTargetConflictAsync(backup.TenantId, operation.Id, token, Now, default));
        await using var context = fixture.CreatePlatformContext();
        var quarantined = await context.BackupReplicas.SingleAsync(r => r.Key == targetKey);
        Assert.Equal(BackupReplicaStatus.Quarantined, quarantined.Status);
        var audit = Assert.Single(await EventsAsync(quarantined.Id));
        AssertEvent(audit, backup.TenantId, UserId.System, AuditActorKind.System, Now, "quarantined");
        Assert.DoesNotContain(targetKey, audit.DetailsJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleRepairReconciliationAuditsOwnedOrphansAndUnownedQuarantineWithoutClaimingPhysicalDeletion(bool owned)
    {
        var backup = await world.SeedAsync(RetentionKit.Policy(enabled: false));
        var connectionId = (await world.ReplicaAsync(backup.ReplicaId)).StorageConnectionId;
        var token = Guid.NewGuid();
        var operation = new ReplicaRepairOperation(Guid.NewGuid(), backup.TenantId, backup.ArtifactId, connectionId,
            "local", backup.ReplicaId, Now.AddMinutes(-2), totalBytes: 100);
        operation.Claim(token, "crashed-repair", Now.AddMinutes(-2), TimeSpan.FromMinutes(1));
        var key = owned ? $"private/{ManagedBackupLayout.RepairFileName(operation.Id, token)}" : $"private/legacy-{Guid.NewGuid():N}.bak";
        operation.SetTargetIntent(backup.ReplicaId, key);
        await using (var setup = fixture.CreatePlatformContext())
        {
            setup.ReplicaRepairOperations.Add(operation);
            await setup.SaveChangesAsync();
        }
        var action = owned ? AuditActions.BackupReplicaOrphanRecorded : AuditActions.BackupReplicaQuarantined;
        var failing = new PostgreSqlReplicaRepairStore(FailingOptions(fixture, action));
        await Assert.ThrowsAsync<AuditCommitFailure>(() => failing.RecoverStaleRepairsAsync(Now, TimeSpan.FromMinutes(1), default));
        await using (var rolledBack = fixture.CreatePlatformContext())
            Assert.False(await rolledBack.BackupReplicas.AnyAsync(r => r.Key == key));
        var store = new PostgreSqlReplicaRepairStore(fixture.Options);
        await store.RecoverStaleRepairsAsync(Now, TimeSpan.FromMinutes(1), default);
        await store.RecoverStaleRepairsAsync(Now, TimeSpan.FromMinutes(1), default);
        await using var context = fixture.CreatePlatformContext();
        var replica = await context.BackupReplicas.SingleAsync(r => r.Key == key);
        Assert.Equal(owned ? BackupReplicaStatus.DeletePending : BackupReplicaStatus.Quarantined, replica.Status);
        Assert.Null(replica.DeletedAt);
        var audit = Assert.Single(await EventsAsync(replica.Id));
        Assert.Equal(action, audit.Action);
        AssertEvent(audit, backup.TenantId, UserId.System, AuditActorKind.System, Now, owned ? "delete_pending" : "quarantined");
        Assert.DoesNotContain(key, audit.DetailsJson);
        Assert.DoesNotContain("physical_delete_completed", audit.Action);
    }

    [Fact]
    public async Task ArtifactHttpAllowDenyAndCancelAuditUseTheActualAdministrator()
    {
        await using var scenario = await CreateAsync(fixture);
        var jobId = await CreateJobAsync(scenario, Policy(enabled: false));
        var backup = await AcceptBackupAsync(scenario, jobId);
        var deniedUser = await scenario.SessionAsync(TenantRole.Operator);
        using var denied = await DeleteArtifactAsync(scenario, backup.ArtifactId, deniedUser);
        using var deniedCancel = await CancelDeleteAsync(scenario, backup.ArtifactId, deniedUser);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deniedCancel.StatusCode);
        Assert.Empty(await EventsAsync(backup.ArtifactId));
        using var requested = await DeleteArtifactAsync(scenario, backup.ArtifactId);
        using var duplicate = await DeleteArtifactAsync(scenario, backup.ArtifactId);
        using var cancelled = await CancelDeleteAsync(scenario, backup.ArtifactId);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        var events = await EventsAsync(backup.ArtifactId);
        Assert.Equal(2, events.Count);
        Assert.All(events, audit =>
        {
            Assert.Equal(scenario.Setup.Account.UserId, audit.ActorUserId);
            Assert.Equal(AuditActorKind.TenantAdministrator, audit.ActorKind);
        });
    }

    private async Task<List<AuditEvent>> EventsAsync(Guid targetId)
    {
        await using var context = fixture.CreatePlatformContext();
        return await context.AuditEvents.AsNoTracking().Where(e => e.TargetId == targetId.ToString("D")).ToListAsync();
    }

    internal static DbContextOptions<PlatformDbContext> FailingOptions(PostgreSqlFixture database, string action, string? targetId = null) =>
        new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(database.ConnectionString)
            .AddInterceptors(new RejectAuditCommit(action, targetId)).Options;

    internal static void AssertEvent(AuditEvent audit, TenantId tenant, UserId actor, AuditActorKind kind, DateTimeOffset at, string result)
    {
        Assert.Equal(tenant, audit.TenantId);
        Assert.Equal(actor, audit.ActorUserId);
        Assert.Equal(kind, audit.ActorKind);
        Assert.Equal(at, audit.At);
        using var details = JsonDocument.Parse(audit.DetailsJson);
        Assert.Equal(result, details.RootElement.GetProperty("result").GetString());
    }

    internal sealed class AuditCommitFailure : Exception { }

    private sealed class RejectAuditCommit(string action, string? targetId) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AuditEvent>().Any(entry => entry.Entity.Action == action &&
                (targetId is null || entry.Entity.TargetId == targetId)))
                throw new AuditCommitFailure();
            return ValueTask.FromResult(result);
        }
    }
}
