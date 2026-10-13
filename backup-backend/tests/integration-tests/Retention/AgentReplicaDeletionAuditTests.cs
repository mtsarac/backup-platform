using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Backups;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.AgentReplicaDeletionTestKit;
using static BackupPlatform.IntegrationTests.RetentionAuditIntegrationTests;

namespace BackupPlatform.IntegrationTests;

public sealed class AgentReplicaDeletionAuditTests
{
    [Fact]
    public async Task AgentOutcomeAuditRejectsWrongTenantExpiredFencingAndUnverifiedCompletionAndDeduplicatesCallbacks()
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seed = await SeedAsync(world);
            var clock = new MutableClock(Due);
            var task = await ClaimAsync(world, seed, clock);
            var identity = AgentReplicaDeletionTestKit.Identity(seed.Agent);
            var stranger = AgentReplicaDeletionTestKit.Identity(await world.Harness.AddAgentAsync());
            var service = Service(world, clock);
            Assert.Equal(BackupMutationResult.NotFound, await service.CompleteAsync(stranger, task.ReplicaId, Success(task), default));
            Assert.Equal(BackupMutationResult.Conflict, await service.CompleteAsync(identity, task.ReplicaId, Success(task) with { LeaseToken = Guid.NewGuid() }, default));
            Assert.Equal(BackupMutationResult.Conflict, await service.CompleteAsync(identity, task.ReplicaId, Success(task) with { AbsenceVerified = false }, default));
            Assert.Equal(BackupMutationResult.Conflict, await service.CompleteAsync(identity, task.ReplicaId, Failure(task, "provider-secret-path"), default));
            clock.Advance(Options.DeleteLeaseDuration);
            Assert.Equal(BackupMutationResult.Conflict, await service.CompleteAsync(identity, task.ReplicaId, Success(task), default));
            var replacement = (await service.ClaimAsync(identity, default))!;
            Assert.Equal(BackupMutationResult.Conflict, await service.CompleteAsync(identity, task.ReplicaId, Success(task), default));
            await using (var context = world.Harness.Database.CreatePlatformContext())
                Assert.False(await context.AuditEvents.AnyAsync(e => e.TargetId == task.ReplicaId.ToString("D")));

            var failedAt = clock.GetUtcNow();
            var failure = Failure(replacement, DeleteReplicaFailureCodes.DeleteNotVerified);
            Assert.Equal(BackupMutationResult.Accepted, await service.CompleteAsync(identity, replacement.ReplicaId, failure, default));
            Assert.Equal(BackupMutationResult.Accepted, await service.CompleteAsync(identity, replacement.ReplicaId, failure, default));
            Assert.Equal(BackupReplicaStatus.DeleteFailed, (await world.ReplicaAsync(task.ReplicaId)).Status);
            clock.Advance(Options.DeleteRetryDelay(2));
            await DispatchAsync(world, clock);
            var retry = (await service.ClaimAsync(identity, default))!;
            Assert.Equal(BackupMutationResult.Conflict, await service.CompleteAsync(identity, retry.ReplicaId, Success(replacement), default));
            var completedAt = clock.GetUtcNow();
            Assert.Equal(BackupMutationResult.Accepted, await service.CompleteAsync(identity, retry.ReplicaId, Success(retry), default));
            Assert.Equal(BackupMutationResult.Accepted, await service.CompleteAsync(identity, retry.ReplicaId, Success(retry), default));
            Assert.Equal(BackupReplicaStatus.Deleted, (await world.ReplicaAsync(task.ReplicaId)).Status);
            await using var verified = world.Harness.Database.CreatePlatformContext();
            var events = await verified.AuditEvents.Where(e => e.TargetId == task.ReplicaId.ToString("D")).ToListAsync();
            Assert.Equal(2, events.Count);
            AssertEvent(Assert.Single(events, e => e.Action == AuditActions.BackupReplicaPhysicalDeleteFailed), seed.Backup.TenantId, UserId.System, AuditActorKind.System, failedAt, "retry_scheduled");
            AssertEvent(Assert.Single(events, e => e.Action == AuditActions.BackupReplicaPhysicalDeleteCompleted), seed.Backup.TenantId, UserId.System, AuditActorKind.System, completedAt, "deleted");
            Assert.All(events, audit =>
            {
                Assert.DoesNotContain(seed.Backup.Key, audit.DetailsJson);
                Assert.DoesNotContain("provider-secret-path", audit.DetailsJson);
            });
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AgentOutcomeAuditCommitFailureRollsBackReplicaAndCompletionIdempotencyToken(bool absenceVerified)
    {
        await RetentionServiceWorld.RunAsync(async world =>
        {
            var seed = await SeedAsync(world);
            var clock = new MutableClock(Due);
            var task = await ClaimAsync(world, seed, clock);
            var action = absenceVerified ? AuditActions.BackupReplicaPhysicalDeleteCompleted : AuditActions.BackupReplicaPhysicalDeleteFailed;
            var store = new PostgreSqlAgentReplicaDeletionStore(FailingOptions(world.Harness.Database, action), Options);
            var request = absenceVerified ? Success(task) : Failure(task, DeleteReplicaFailureCodes.IoError);
            await Assert.ThrowsAsync<AuditCommitFailure>(() => store.CompleteAsync(AgentReplicaDeletionTestKit.Identity(seed.Agent), task.ReplicaId, request, Due, default));
            var replica = await world.ReplicaAsync(task.ReplicaId);
            Assert.Equal(BackupReplicaStatus.Deleting, replica.Status);
            Assert.Equal(task.LeaseToken, replica.DeleteLeaseToken);
            Assert.Null(replica.DeleteCompletionToken);
            Assert.Null(replica.DeletedAt);
            await using var context = world.Harness.Database.CreatePlatformContext();
            Assert.False(await context.AuditEvents.AnyAsync(e => e.TargetId == task.ReplicaId.ToString("D")));
        });
    }
}
