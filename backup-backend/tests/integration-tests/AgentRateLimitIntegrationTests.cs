using System.Net;
using BackupPlatform.Api.Security;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.IntegrationTests;

public sealed class AgentRateLimitIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static AgentRateLimitOptions Tight(
        int heartbeat = 100, int poll = 100, int connect = 100, int enrollmentClient = 100, int enrollmentToken = 100, int authFailures = 100) => new()
    {
        Window = TimeSpan.FromMinutes(1),
        HeartbeatPerAgent = heartbeat,
        PollPerAgent = poll,
        ConnectPerAgent = connect,
        EnrollmentPerClient = enrollmentClient,
        EnrollmentPerToken = enrollmentToken,
        AuthenticationFailuresPerClient = authFailures
    };

    private static async Task AssertRateLimitedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.RetryAfter?.Delta is { } delta && delta >= TimeSpan.FromSeconds(1) && delta <= TimeSpan.FromMinutes(1),
            "Retry-After must be a whole number of seconds within the limiter window.");
        var error = AgentWorld.ReadJson(response);
        Assert.Equal("rate_limited", error.GetProperty("error").GetString());
        Assert.Equal("retry_later", error.GetProperty("action").GetString());
        Assert.Equal((int)response.Headers.RetryAfter!.Delta!.Value.TotalSeconds, error.GetProperty("retryAfterSeconds").GetInt32());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ExcessiveHeartbeatsFromOneAgentGet429WithRetryAfterWhileOtherAgentsAreUnaffected()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, limits: Tight(heartbeat: 3));
        var setup = await world.CreateTenantAsync();
        var noisy = await world.EnrollAsync(setup);
        var quiet = await world.EnrollAsync(setup);
        for (var index = 0; index < 3; index++)
        {
            using var accepted = await world.HeartbeatAsync(noisy);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }
        using var limited = await world.HeartbeatAsync(noisy);
        await AssertRateLimitedAsync(limited);
        Assert.DoesNotContain(noisy.Secret, await limited.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var other = await world.HeartbeatAsync(quiet);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        using var pollIsSeparate = await world.PollAsync(noisy);
        Assert.Equal(HttpStatusCode.OK, pollIsSeparate.StatusCode);
    }

    [Fact]
    public async Task ExcessivePollingFromOneAgentGet429()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, limits: Tight(poll: 2));
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        using (await world.PollAsync(agent)) { }
        using (await world.PollAsync(agent)) { }
        using var limited = await world.PollAsync(agent);
        await AssertRateLimitedAsync(limited);
    }

    [Fact]
    public async Task NormalAgentCadenceAndRestartBurstsAreNotThrottledByTheDefaultLimits()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        // Six heartbeats and six polls are three minutes of normal cadence, or a reconnect burst after an outage.
        for (var index = 0; index < 6; index++)
        {
            using var heartbeat = await world.HeartbeatAsync(agent);
            using var poll = await world.PollAsync(agent);
            Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
            Assert.Equal(HttpStatusCode.OK, poll.StatusCode);
        }
        // A runaway loop still hits the ceiling quickly.
        var limited = 0;
        for (var index = 0; index < 30; index++)
        {
            using var response = await world.HeartbeatAsync(agent);
            limited += response.StatusCode == HttpStatusCode.TooManyRequests ? 1 : 0;
        }
        Assert.True(limited > 0);
    }

    [Fact]
    public async Task FloodOfInvalidCredentialsIsCutOffBeforeAnyVerificationEvenForAValidCredentialFromTheSameClient()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, limits: Tight(authFailures: 3));
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        var wrongSecret = new string('Z', AgentSecretReference.SecretLength);
        for (var index = 0; index < 3; index++)
        {
            using var rejected = await world.SendAgentAsync(HttpMethod.Get, "/api/agent/me", agent.CredentialId, wrongSecret);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
        using var blocked = await world.SendAgentAsync(HttpMethod.Get, "/api/agent/me", agent.CredentialId, wrongSecret);
        await AssertRateLimitedAsync(blocked);
        using var validButSameClient = await world.AgentMeAsync(agent.CredentialId, agent.Secret);
        await AssertRateLimitedAsync(validButSameClient);
        Assert.DoesNotContain(wrongSecret, await blocked.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(wrongSecret, blocked.Headers.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulAuthenticationAndAnonymousProbesDoNotConsumeTheFailureBudget()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, limits: Tight(authFailures: 3));
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        for (var index = 0; index < 10; index++)
        {
            using var ok = await world.AgentMeAsync(agent.CredentialId, agent.Secret);
            using var anonymous = await world.Anonymous.GetAsync("/api/agent/me");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }
    }

    [Fact]
    public async Task EnrollmentAttemptsAreLimitedPerTokenIdentifierWithoutRevealingWhetherTheTokenExists()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, limits: Tight(enrollmentToken: 2));
        var setup = await world.CreateTenantAsync();
        var (tokenId, _) = await world.CreateTokenAsync(setup);
        var guess = AgentSecretReference.Format(tokenId, new string('G', AgentSecretReference.SecretLength));
        for (var index = 0; index < 2; index++)
        {
            using var rejected = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(guess));
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
        using var limited = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(guess));
        await AssertRateLimitedAsync(limited);

        var unknown = AgentSecretReference.Format(Guid.NewGuid(), new string('G', AgentSecretReference.SecretLength));
        using (await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(unknown))) { }
        using (await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(unknown))) { }
        using var unknownLimited = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest(unknown));
        await AssertRateLimitedAsync(unknownLimited);
    }

    [Fact]
    public async Task EnrollmentFloodFromOneNetworkClientIsLimitedRegardlessOfTheTokensUsed()
    {
        await using var world = await AgentWorld.CreateAsync(fixture, limits: Tight(enrollmentClient: 4));
        for (var index = 0; index < 4; index++)
        {
            using var rejected = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest($"garbage-{index}"));
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
        using var limited = await world.PostEnrollAsync(AgentWorld.EnrollmentRequest("garbage-again"));
        await AssertRateLimitedAsync(limited);
    }

    [Fact]
    public async Task OneTokenStillEnrollsOnItsFirstAttemptUnderTheDefaultLimits()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var agent = await world.EnrollAsync(await world.CreateTenantAsync());
        Assert.NotEqual(Guid.Empty, agent.AgentId);
    }

    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9")]
    [InlineData("::ffff:203.0.113.9", "203.0.113.9")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:1111:2222:3333:4444", "2001:db8:1:2::/64")]
    public void ClientKeysGroupIpv6By64AndNormalizeMappedIpv4(string address, string expected) =>
        Assert.Equal(expected, AgentClientKey.From(IPAddress.Parse(address)));

    [Fact]
    public void ClientKeysAreNeverDerivedFromCredentials() =>
        Assert.Equal("unknown", AgentClientKey.From(null));
}
