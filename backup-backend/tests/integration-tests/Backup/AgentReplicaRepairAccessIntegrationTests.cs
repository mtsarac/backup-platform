using System.Net;
using BackupPlatform.Agents;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BackupPlatform.IntegrationTests.Backup;

public sealed class AgentReplicaRepairAccessIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Theory]
    [InlineData("source", "wrong-lease")]
    [InlineData("target", "wrong-lease")]
    [InlineData("source", "other-agent")]
    [InlineData("target", "other-agent")]
    [InlineData("source", "unpinned")]
    [InlineData("source", "expired")]
    [InlineData("target", "expired")]
    public async Task AccessIsRefusedBeforeResolvingCredentialsUnlessTheAgentHoldsThePinnedLease(string side, string failure)
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var job = await scenario.CreateJobAsync();
        await scenario.StartRunAsync(job);
        var claim = await scenario.ClaimAsync();
        var now = scenario.Clock.GetUtcNow();
        var lease = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        await using (var db = fixture.CreatePlatformContext())
        {
            var artifact = new BackupArtifact(Guid.NewGuid(), scenario.Setup.Tenant.Id, claim.AttemptId, 1024,
                new string('a', 64), new string('b', 64), "sha256", now, "{}", "{}");
            db.BackupArtifacts.Add(artifact);
            var sourceConnectionId = Guid.NewGuid();
            var targetConnectionId = Guid.NewGuid();
            db.StorageConnections.Add(new StorageConnection(sourceConnectionId, artifact.TenantId, "Repair source", "sftp",
                StorageOwnership.TenantManaged, StorageQuotaScopes.Tenant, new Dictionary<string, string>(), false, now));
            db.StorageConnections.Add(new StorageConnection(targetConnectionId, artifact.TenantId, "Repair target", "sftp",
                StorageOwnership.TenantManaged, StorageQuotaScopes.Tenant, new Dictionary<string, string>(), false, now));
            var source = new BackupReplica(Guid.NewGuid(), artifact.TenantId, artifact.Id, "sftp", sourceConnectionId.ToString("D"),
                "scope/source.bak", 1024, artifact.ArtifactChecksum, "sha256", "Verified", "ReadBack", now, now,
                BackupStorageExecutionLocation.Backend, storageConnectionId: sourceConnectionId);
            artifact.AddReplica(source);
            db.BackupReplicas.Add(source);
            var operation = new ReplicaRepairOperation(operationId, artifact.TenantId, artifact.Id, targetConnectionId, "sftp", source.Id, now, totalBytes: 1024);
            var startedAt = failure == "expired" ? now.AddMinutes(-11) : now;
            operation.Claim(lease, ReplicaRepairOperation.AgentLeaseOwner(scenario.Agent.AgentId), startedAt, TimeSpan.FromMinutes(10));
            if (failure != "unpinned") operation.SetTargetIntent(source.Id, $"scope/{ManagedBackupLayout.RepairFileName(operationId, lease)}");
            db.ReplicaRepairOperations.Add(operation);
            await db.SaveChangesAsync();
        }
        var caller = failure == "other-agent" ? await scenario.World.EnrollAsync(scenario.Setup) : scenario.Agent;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/agent/replica-repairs/{operationId}/storage/{side}/access");
        request.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(caller.CredentialId, caller.Secret));
        request.Headers.Add("X-Backup-Agent-Protocol", "1.0");
        request.Headers.Add(TaskStorageProtocol.LeaseHeader, (failure == "wrong-lease" ? Guid.NewGuid() : lease).ToString("D"));

        using var response = await scenario.World.Anonymous.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("lease_conflict", AgentWorld.ReadJson(response).GetProperty("error").GetString());
    }
}
