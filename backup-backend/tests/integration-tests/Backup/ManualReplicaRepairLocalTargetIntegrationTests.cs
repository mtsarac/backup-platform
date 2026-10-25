using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.IntegrationTests.Quota;
using BackupPlatform.ManagedService.Quota;
using BackupPlatform.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Completion = BackupPlatform.Application.Backups.BackupCompletion;

namespace BackupPlatform.IntegrationTests.Backup;

public sealed partial class ManualReplicaRepairIntegrationTests
{
    [Fact]
    public async Task A_missing_local_copy_is_rebuilt_by_the_agent_that_ran_the_backup_from_an_external_copy_and_never_from_a_central_one()
    {
        var policy = await World.CreatePolicyAsync(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All, QuotaEnforcementMode.WarnOnly);
        var agent = await World.AgentAsync(100 * QuotaWorld.Gb, policy.Key);
        var (local, central) = await _connections.DefaultsAsync(agent.TenantId);
        var sftp = await _connections.AddAsync(agent.TenantId, "sftp-1", "sftp");
        var configuration = BackupStoreHarness.Configuration() with
        {
            Destinations = [new BackupDestination("local", local.Id, true), new BackupDestination("central", central.Id), new BackupDestination("sftp", sftp.Id)],
            RequiredVerifiedReplicaCount = 2
        };
        var job = (await World.RunStore().CreateJobAsync(new BackupJob(Guid.NewGuid(), agent.TenantId, "Local repair", agent.AgentId, configuration, T0, null, BackupOverlapPolicy.Queue), default))!;
        var runs = World.RunStore();
        var started = await runs.StartRunAsync(agent.TenantId, job.Id, $"key-{Guid.NewGuid():N}", T0, default);
        var claim = (await BackupStoreHarness.ClaimAsync(runs, agent, T0))!;
        await BackupStoreHarness.StartAttemptAsync(runs, claim, T0);
        var scope = ManagedBackupLayout.AttemptScope(agent.TenantId.Value, job.Id, started.Run!.Id, claim.Attempt.Id);
        var sftpKey = $"{scope}/db_sftp_FULL.bak";
        await BackupStoreHarness.CompleteAsync(runs, claim, new Completion("Succeeded", null,
            new BackupArtifactResult(Content.Length, Checksum, Checksum, "sha256", T0, "{}", "{}",
            [
                new BackupReplicaResult("central", "central", $"{scope}/db_central_FULL.bak", Content.Length, Checksum, "sha256", "Verified", "ReadBack", T0, T0, central.Id),
                new BackupReplicaResult("sftp", sftp.Id.ToString("D"), sftpKey, Content.Length, Checksum, "sha256", "Verified", "ReadBack", T0, T0, sftp.Id)
            ]),
            [
                new BackupDestinationReport("local", local.Id, "Failed", "Failed", "NotRun", "StorageUnavailable", null, false, T0, T0),
                new BackupDestinationReport("central", central.Id, "Verified", "Succeeded", "Verified", null, $"{scope}/db_central_FULL.bak", false, T0, T0),
                new BackupDestinationReport("sftp", sftp.Id, "Verified", "Succeeded", "Verified", null, sftpKey, false, T0, T0)
            ]), T0);
        await using (var context = fixture.CreatePlatformContext())
        {
            context.AgentCapabilities.Add(new AgentCapabilityRecord(agent.AgentId, agent.TenantId, ReplicaLifecycleProtocol.Capability, CapabilitySupport.Supported, null, "repair-tests", T0));
            await context.SaveChangesAsync();
        }
        var artifact = await fixture.CreatePlatformContext().BackupArtifacts.SingleAsync(a => a.TenantId == agent.TenantId && a.AttemptId == claim.Attempt.Id);
        var store = new PostgreSqlReplicaRepairStore(World.Options());
        var service = new ReplicaRepairService(store, null!, TimeProvider.System, NullLogger<ReplicaRepairService>.Instance);

        var start = await service.StartRepairAsync(agent.TenantId, artifact.Id, new(StorageConnectionId: local.Id), null, default);
        Assert.True(start.Success, start.Message);
        var operation = start.Operation!;
        Assert.Null(await store.ClaimDueRepairAsync("backend", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), default, agent.TenantId, backendExecutedOnly: true));
        var other = await AdmissionTestKit.AddAgentAsync(World, agent.TenantId);
        Assert.Null(await service.ClaimForAgentAsync(new AgentIdentity(other.AgentId, other.TenantId, Guid.NewGuid(), 1), default));

        var identity = new AgentIdentity(agent.AgentId, agent.TenantId, Guid.NewGuid(), 1);
        var task = (await service.ClaimForAgentAsync(identity, default))!;
        Assert.Equal(("local", local.Id), (task.Target.StorageProvider, task.Target.StorageConnectionId));
        var volume = task.Target.StorageTarget;
        var from = Assert.Single(task.Sources);
        Assert.Equal(("sftp", sftp.Id, sftpKey), (from.StorageProvider, from.StorageConnectionId, from.StorageKey));
        var targetKey = $"{scope}/{ManagedBackupLayout.RepairFileName(task.OperationId, task.LeaseToken)}";
        Assert.Equal(BackupMutationResult.Accepted, await service.PinForAgentAsync(identity, task.OperationId,
            new ReplicaRepairPinRequest { LeaseToken = task.LeaseToken, SourceReplicaId = from.ReplicaId, TargetKey = targetKey }, default));
        Assert.Equal(BackupMutationResult.Accepted, await service.CompleteForAgentAsync(identity, task.OperationId,
            new ReplicaRepairCompletionRequest { LeaseToken = task.LeaseToken, Outcome = ReplicaRepairOutcomes.Succeeded, VerificationMethod = "ReadBack" },
            (_, _, _, _) => Task.CompletedTask, default));

        await using var verify = fixture.CreatePlatformContext();
        var repaired = await verify.BackupReplicas.SingleAsync(r => r.ArtifactId == artifact.Id && r.StorageConnectionId == local.Id && r.VerificationState == "Verified");
        Assert.Equal(("local", volume, BackupStorageExecutionLocation.Agent, agent.AgentId.Value, targetKey),
            (repaired.StorageProvider, repaired.StorageTarget, repaired.ExecutionLocation, repaired.OwningAgentId, repaired.Key));
        Assert.Equal(ReplicaRepairStatus.Succeeded, (await verify.ReplicaRepairOperations.SingleAsync(r => r.Id == operation.Id)).Status);
    }

    [Fact]
    public async Task A_local_destination_is_not_repairable_from_a_central_copy_alone()
    {
        var policy = await World.CreatePolicyAsync(QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All, QuotaEnforcementMode.WarnOnly);
        var agent = await World.AgentAsync(100 * QuotaWorld.Gb, policy.Key);
        var (local, central) = await _connections.DefaultsAsync(agent.TenantId);
        var configuration = BackupStoreHarness.Configuration() with
        {
            Destinations = [new BackupDestination("local", local.Id, true), new BackupDestination("central", central.Id)]
        };
        var job = (await World.RunStore().CreateJobAsync(new BackupJob(Guid.NewGuid(), agent.TenantId, "Central only", agent.AgentId, configuration, T0, null, BackupOverlapPolicy.Queue), default))!;
        var runs = World.RunStore();
        var started = await runs.StartRunAsync(agent.TenantId, job.Id, $"key-{Guid.NewGuid():N}", T0, default);
        var claim = (await BackupStoreHarness.ClaimAsync(runs, agent, T0))!;
        await BackupStoreHarness.StartAttemptAsync(runs, claim, T0);
        var key = $"{ManagedBackupLayout.AttemptScope(agent.TenantId.Value, job.Id, started.Run!.Id, claim.Attempt.Id)}/db_central_FULL.bak";
        await BackupStoreHarness.CompleteAsync(runs, claim, new Completion("Succeeded", null,
            new BackupArtifactResult(Content.Length, Checksum, Checksum, "sha256", T0, "{}", "{}",
                [new BackupReplicaResult("central", "central", key, Content.Length, Checksum, "sha256", "Verified", "ReadBack", T0, T0, central.Id)]),
            [
                new BackupDestinationReport("local", local.Id, "Failed", "Failed", "NotRun", "StorageUnavailable", null, false, T0, T0),
                new BackupDestinationReport("central", central.Id, "Verified", "Succeeded", "Verified", null, key, false, T0, T0)
            ]), T0);
        var artifact = await fixture.CreatePlatformContext().BackupArtifacts.SingleAsync(a => a.TenantId == agent.TenantId && a.AttemptId == claim.Attempt.Id);

        var start = await new ReplicaRepairService(new PostgreSqlReplicaRepairStore(World.Options()), null!, TimeProvider.System, NullLogger<ReplicaRepairService>.Instance)
            .StartRepairAsync(agent.TenantId, artifact.Id, new(StorageConnectionId: local.Id), null, default);

        Assert.False(start.Success);
        Assert.Equal(ReplicaRepairFailureCodes.NoVerifiedSourceAvailable, start.Code);
    }
}
