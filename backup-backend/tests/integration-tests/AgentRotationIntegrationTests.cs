using System.Net;
using BackupPlatform.Agents.Credentials;
using BackupPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class AgentRotationIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string Rotate = "/api/agent/credential/rotate";
    private const string Confirm = "/api/agent/credential/confirm";

    [Fact]
    public async Task BeginIssuesAPendingCredentialWhileTheCurrentOneKeepsWorking()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        var pending = await world.BeginRotationAsync(agent.CredentialId, agent.Secret);
        Assert.Equal(2, pending.Generation);
        Assert.NotEqual(agent.CredentialId, pending.CredentialId);
        Assert.NotEqual(agent.Secret, pending.Secret);

        using var old = await world.AgentMeAsync(agent.CredentialId, agent.Secret);
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        using var pendingMe = await world.AgentMeAsync(pending.CredentialId, pending.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, pendingMe.StatusCode);
        using var pendingRotate = await world.AgentCallAsync(HttpMethod.Post, Rotate, pending.CredentialId, pending.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, pendingRotate.StatusCode);

        var credentials = await world.CredentialsOfAsync(agent.AgentId);
        Assert.Equal([AgentCredentialStatus.Active, AgentCredentialStatus.Pending], credentials.Select(row => row.Status).ToArray());
        Assert.NotNull(credentials[1].ExpiresAt);
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(1, (await context.Agents.SingleAsync(row => row.Id == new AgentId(agent.AgentId))).CredentialGeneration);
    }

    [Fact]
    public async Task ConfirmationActivatesTheNewCredentialAndRevokesTheOldOne()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        var pending = await world.BeginRotationAsync(agent.CredentialId, agent.Secret);

        using var confirm = await world.AgentCallAsync(HttpMethod.Post, Confirm, pending.CredentialId, pending.Secret);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var json = AgentWorld.ReadJson(confirm);
        Assert.Equal(2, json.GetProperty("credentialGeneration").GetInt32());
        Assert.False(json.GetProperty("alreadyConfirmed").GetBoolean());

        using var old = await world.AgentMeAsync(agent.CredentialId, agent.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using var oldRotate = await world.AgentCallAsync(HttpMethod.Post, Rotate, agent.CredentialId, agent.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, oldRotate.StatusCode);
        using var fresh = await world.AgentMeAsync(pending.CredentialId, pending.Secret);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(2, AgentWorld.ReadJson(fresh).GetProperty("credentialGeneration").GetInt32());

        var credentials = await world.CredentialsOfAsync(agent.AgentId);
        Assert.Equal(AgentCredentialStatus.Revoked, credentials[0].Status);
        Assert.Equal(AgentCredentialRevocationReason.Superseded, credentials[0].RevocationReason);
        Assert.Equal(AgentCredentialStatus.Active, credentials[1].Status);
        Assert.Null(credentials[1].ExpiresAt);
        Assert.NotNull(credentials[1].ActivatedAt);
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(2, (await context.Agents.SingleAsync(row => row.Id == new AgentId(agent.AgentId))).CredentialGeneration);
    }

    [Fact]
    public async Task RetriedConfirmationIsIdempotentAfterALostResponse()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        var pending = await world.BeginRotationAsync(agent.CredentialId, agent.Secret);
        using var first = await world.AgentCallAsync(HttpMethod.Post, Confirm, pending.CredentialId, pending.Secret);
        using var retry = await world.AgentCallAsync(HttpMethod.Post, Confirm, pending.CredentialId, pending.Secret);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.True(AgentWorld.ReadJson(retry).GetProperty("alreadyConfirmed").GetBoolean());
        Assert.Equal(2, AgentWorld.ReadJson(retry).GetProperty("credentialGeneration").GetInt32());
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(2, (await context.Agents.SingleAsync(row => row.Id == new AgentId(agent.AgentId))).CredentialGeneration);
    }

    [Fact]
    public async Task InterruptedRotationRecoversByConfirmingTheLostPendingCredentialsReplacement()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        // The response carrying this secret was lost; the agent never stored it and still holds only the old credential.
        var lost = await world.BeginRotationAsync(agent.CredentialId, agent.Secret);
        using var stillWorks = await world.AgentMeAsync(agent.CredentialId, agent.Secret);
        Assert.Equal(HttpStatusCode.OK, stillWorks.StatusCode);
        using var oldCannotConfirmOverAPendingOne = await world.AgentCallAsync(HttpMethod.Post, Confirm, agent.CredentialId, agent.Secret);
        Assert.Equal(HttpStatusCode.Conflict, oldCannotConfirmOverAPendingOne.StatusCode);

        var replacement = await world.BeginRotationAsync(agent.CredentialId, agent.Secret);
        Assert.Equal(2, replacement.Generation);
        using var lostConfirm = await world.AgentCallAsync(HttpMethod.Post, Confirm, lost.CredentialId, lost.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, lostConfirm.StatusCode);
        using var confirm = await world.AgentCallAsync(HttpMethod.Post, Confirm, replacement.CredentialId, replacement.Secret);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var credentials = await world.CredentialsOfAsync(agent.AgentId);
        Assert.Equal(1, credentials.Count(row => row.Status == AgentCredentialStatus.Active));
        Assert.Equal(0, credentials.Count(row => row.Status == AgentCredentialStatus.Pending));
        Assert.Equal(AgentCredentialRevocationReason.RotationReplaced, credentials.Single(row => row.Id == lost.CredentialId).RevocationReason);
    }

    [Fact]
    public async Task ExpiredPendingCredentialCannotConfirmButTheOldCredentialStillRecoversTheAgent()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        var pending = await world.BeginRotationAsync(agent.CredentialId, agent.Secret);
        world.Clock.Advance(TimeSpan.FromHours(25));
        using var expired = await world.AgentCallAsync(HttpMethod.Post, Confirm, pending.CredentialId, pending.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        using var old = await world.AgentMeAsync(agent.CredentialId, agent.Secret);
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);
        var retry = await world.BeginRotationAsync(agent.CredentialId, agent.Secret);
        using var confirm = await world.AgentCallAsync(HttpMethod.Post, Confirm, retry.CredentialId, retry.Secret);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
    }

    [Fact]
    public async Task ConsecutiveRotationsAdvanceGenerationsOneAtATime()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        var (id, secret) = (agent.CredentialId, agent.Secret);
        for (var expected = 2; expected <= 4; expected++)
        {
            var pending = await world.BeginRotationAsync(id, secret);
            Assert.Equal(expected, pending.Generation);
            using var confirm = await world.AgentCallAsync(HttpMethod.Post, Confirm, pending.CredentialId, pending.Secret);
            Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
            (id, secret) = (pending.CredentialId, pending.Secret);
        }
        var credentials = await world.CredentialsOfAsync(agent.AgentId);
        Assert.Equal(4, credentials.Count);
        Assert.Equal([3, 3, 3, 2], credentials.Select(row => (int)row.Status).ToArray());
        Assert.Equal(1, credentials.Count(row => row.Status == AgentCredentialStatus.Active));
    }

    [Fact]
    public async Task ConcurrentBeginRequestsNeverLeaveMoreThanOnePendingCredentialAndOnlyOneCanConfirm()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        var issued = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => world.BeginRotationAsync(agent.CredentialId, agent.Secret))));
        var credentials = await world.CredentialsOfAsync(agent.AgentId);
        Assert.Equal(1, credentials.Count(row => row.Status == AgentCredentialStatus.Pending));
        Assert.Equal(1, credentials.Count(row => row.Status == AgentCredentialStatus.Active));
        var outcomes = new List<HttpStatusCode>();
        foreach (var candidate in issued)
        {
            using var response = await world.AgentCallAsync(HttpMethod.Post, Confirm, candidate.CredentialId, candidate.Secret);
            outcomes.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                break;
            }
        }
        Assert.Equal(1, outcomes.Count(status => status == HttpStatusCode.OK));
        Assert.All(outcomes.Where(status => status != HttpStatusCode.OK), status => Assert.Equal(HttpStatusCode.Unauthorized, status));
        var final = await world.CredentialsOfAsync(agent.AgentId);
        Assert.Equal(1, final.Count(row => row.Status == AgentCredentialStatus.Active && row.Generation == 2));
    }

    [Fact]
    public async Task ConcurrentConfirmationsActivateExactlyOnceAndNeverSkipAGeneration()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        var pending = await world.BeginRotationAsync(agent.CredentialId, agent.Secret);
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            using var response = await world.AgentCallAsync(HttpMethod.Post, Confirm, pending.CredentialId, pending.Secret);
            return (response.StatusCode, Json: AgentWorld.ReadJson(response));
        })));
        Assert.All(responses, item => Assert.Equal(HttpStatusCode.OK, item.StatusCode));
        Assert.Equal(1, responses.Count(item => !item.Json.GetProperty("alreadyConfirmed").GetBoolean()));
        Assert.All(responses, item => Assert.Equal(2, item.Json.GetProperty("credentialGeneration").GetInt32()));
        var credentials = await world.CredentialsOfAsync(agent.AgentId);
        Assert.Equal(1, credentials.Count(row => row.Status == AgentCredentialStatus.Active));
        Assert.Equal(AgentCredentialStatus.Revoked, credentials[0].Status);
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(2, (await context.Agents.SingleAsync(row => row.Id == new AgentId(agent.AgentId))).CredentialGeneration);
    }

    [Fact]
    public async Task ConcurrentBeginAndOldCredentialConfirmAreSerializedWithoutCorruptingState()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        var pending = await world.BeginRotationAsync(agent.CredentialId, agent.Secret);
        var confirm = Task.Run(async () =>
        {
            using var response = await world.AgentCallAsync(HttpMethod.Post, Confirm, pending.CredentialId, pending.Secret);
            return response.StatusCode;
        });
        var begin = Task.Run(async () =>
        {
            using var response = await world.AgentCallAsync(HttpMethod.Post, Rotate, agent.CredentialId, agent.Secret);
            return response.StatusCode;
        });
        await Task.WhenAll(confirm, begin);
        var credentials = await world.CredentialsOfAsync(agent.AgentId);
        Assert.Equal(1, credentials.Count(row => row.Status == AgentCredentialStatus.Active));
        Assert.True(credentials.Count(row => row.Status == AgentCredentialStatus.Pending) <= 1);
        Assert.Contains(await begin, new[] { HttpStatusCode.Created, HttpStatusCode.Unauthorized });
        await using var context = fixture.CreatePlatformContext();
        var generation = (await context.Agents.SingleAsync(row => row.Id == new AgentId(agent.AgentId))).CredentialGeneration;
        Assert.Equal(generation, credentials.Single(row => row.Status == AgentCredentialStatus.Active).Generation);
    }

    [Fact]
    public async Task UnauthenticatedCallersCannotRotateOrConfirm()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        using var rotate = await world.Anonymous.PostAsync(Rotate, null);
        using var confirm = await world.Anonymous.PostAsync(Confirm, null);
        Assert.Equal(HttpStatusCode.Unauthorized, rotate.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, confirm.StatusCode);
    }

    [Fact]
    public async Task OnlyOneActiveAndOnePendingCredentialPerAgentIsEnforcedByTheDatabase()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        await world.BeginRotationAsync(agent.CredentialId, agent.Secret);
        var agentId = new AgentId(agent.AgentId);
        await using var context = fixture.CreatePlatformContext();
        var tenantId = (await context.Agents.SingleAsync(row => row.Id == agentId)).TenantId;
        context.AgentCredentials.Add(AgentCredential.IssuePending(agentId, tenantId, 2, DateTimeOffset.UtcNow, TimeSpan.FromHours(1)).Credential);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, Assert.IsType<Npgsql.PostgresException>(error.InnerException).SqlState);
        await using var second = fixture.CreatePlatformContext();
        second.AgentCredentials.Add(AgentCredential.IssueInitial(agentId, tenantId, DateTimeOffset.UtcNow).Credential);
        var activeError = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, Assert.IsType<Npgsql.PostgresException>(activeError.InnerException).SqlState);
    }
}
