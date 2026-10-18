using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BackupPlatform.IntegrationTests.RestoreApiScenario;

namespace BackupPlatform.IntegrationTests;

/// <summary>POST /api/agent/restore-tasks/{attemptId}/artifact-key: fenced like the restore storage relay, no body, answers only for the operation's own encrypted artifact.</summary>
public sealed class ArtifactKeyEndpointApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static Task<HttpResponseMessage> CallAsync(RestoreApiScenario scenario, RestoreTask task, EnrolledAgent caller, Guid? lease = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/agent/restore-tasks/{task.AttemptId}/artifact-key");
        request.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(caller.CredentialId, caller.Secret));
        request.Headers.Add("X-Backup-Agent-Protocol", "1.0");
        request.Headers.Add(TaskStorageProtocol.LeaseHeader, (lease ?? task.LeaseToken).ToString("D"));
        request.Headers.Add(TaskStorageProtocol.RestoreHeader, task.OperationId.ToString("D"));
        return scenario.World.Anonymous.SendAsync(request);
    }

    private static async Task<(RestoreApiScenario Scenario, RestoreTask Task, Guid ArtifactId)> ClaimedAsync(PostgreSqlFixture fixture)
    {
        var scenario = await CreateAsync(fixture);
        var backup = await scenario.AcceptBackupAsync();
        await scenario.StartAsync(backup.ArtifactId);
        var task = await scenario.MustPollRestoreAsync();
        using var started = await scenario.StartAttemptRawAsync(task);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        return (scenario, task, backup.ArtifactId);
    }

    private static string Error(HttpResponseMessage response) => AgentWorld.ReadJson(response).GetProperty("error").GetString()!;

    [Fact]
    public async Task APlainArtifactHasNoKeyToUnwrap()
    {
        var (scenario, task, _) = await ClaimedAsync(fixture);
        await using var _ = scenario;
        using var response = await CallAsync(scenario, task, scenario.Agent);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("artifact_not_encrypted", Error(response));
    }

    [Fact]
    public async Task WrongLeaseAnotherAgentAndAnUnknownAttemptAreRefused()
    {
        var (scenario, task, _) = await ClaimedAsync(fixture);
        await using var _ = scenario;
        var sibling = await scenario.AddAgentAsync();
        using var wrongLease = await CallAsync(scenario, task, scenario.Agent, Guid.NewGuid());
        using var otherAgent = await CallAsync(scenario, task, sibling);
        using var unknown = await CallAsync(scenario, task with { AttemptId = Guid.NewGuid() }, scenario.Agent);
        Assert.All(new[] { wrongLease, otherAgent, unknown }, response => Assert.Equal(HttpStatusCode.Conflict, response.StatusCode));
        Assert.All(new[] { wrongLease, otherAgent, unknown }, response => Assert.Equal("lease_conflict", Error(response)));
    }

    [Fact]
    public async Task AnEncryptedArtifactYieldsItsDekAndTheAuditNeverContainsIt()
    {
        var (scenario, task, artifactId) = await ClaimedAsync(fixture);
        await using var _ = scenario;
        await using var scope = scenario.World.Factory.Services.CreateAsyncScope();
        var keys = scope.ServiceProvider.GetRequiredService<ITenantWrapKeyStore>();
        var tenant = new TenantId(scenario.TenantId);
        var key = await keys.EnsureActiveAsync(tenant, default);
        var dek = RandomNumberGenerator.GetBytes(32);
        using (var rsa = RSA.Create())
        {
            rsa.ImportSubjectPublicKeyInfo(key.PublicKeySpki, out var read);
            var wrapped = Convert.ToBase64String(rsa.Encrypt(dek, RSAEncryptionPadding.OaepSHA256));
            await fixture.ExecuteAsync($"UPDATE backup_artifacts SET \"Format\" = 'v2-bpe', \"EncryptionKeyId\" = '{key.KeyId}', \"EncryptionKeyVersion\" = {key.Version}, " +
                $"\"EncryptionWrappedDekBase64\" = '{wrapped}' WHERE \"Id\" = '{artifactId}'");
        }

        using var response = await CallAsync(scenario, task, scenario.Agent);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = AgentWorld.ReadJson(response);
        Assert.Equal(Convert.ToBase64String(dek), body.GetProperty("dekBase64").GetString());
        await using var context = fixture.CreatePlatformContext();
        var audit = await context.AuditEvents.AsNoTracking().Where(e => e.TenantId == tenant && e.Action == AuditActions.BackupArtifactKeyUnwrapped).SingleAsync();
        Assert.Contains(task.AttemptId.ToString("D"), audit.DetailsJson);
        Assert.DoesNotContain(Convert.ToBase64String(dek), audit.DetailsJson);
    }
}
