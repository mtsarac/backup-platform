using BackupPlatform.Agents;
using BackupPlatform.Agents.Credentials;
using BackupPlatform.Agents.Enrollment;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Protocol;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.UnitTests;

// BackupPlatform.Agent is also a namespace (Agent.Core); the alias keeps the domain type addressable.
using Agent = BackupPlatform.Domain.Agents.Agent;

public sealed class AgentEnrollmentServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CompatibleProtocolEnrollsAndReturnsAOneTimeCredential()
    {
        var world = World.Create(enabled: true);
        var result = await world.Service.EnrollAsync(world.Request(), CancellationToken.None);
        Assert.Equal(AgentEnrollmentStatus.Enrolled, result.Status);
        var enrolled = result.Enrolled!;
        Assert.Equal(world.TenantId, enrolled.TenantId);
        Assert.Equal(1, enrolled.Credential.Credential.Generation);
        var stored = Assert.Single(world.Store.Agents);
        Assert.Equal(AgentStatus.Active, stored.Status);
        Assert.Equal(1, stored.CredentialGeneration);
        Assert.Equal(AgentPlatform.Linux, stored.Software.Platform);
        Assert.Equal(new Version(1, 4, 2), stored.Software.Version);
        Assert.Equal("build-01", stored.HostName);
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("0.9")]
    [InlineData("1.1")]
    public async Task ProtocolsOutsideTheSupportedWindowAreRejectedBeforeAnythingIsConsumed(string protocol)
    {
        var world = World.Create(enabled: true);
        var result = await world.Service.EnrollAsync(world.Request(protocolVersion: protocol), CancellationToken.None);
        Assert.Equal(protocol == "0.9" ? AgentEnrollmentStatus.InvalidRequest : AgentEnrollmentStatus.ProtocolIncompatible, result.Status);
        Assert.Empty(world.Store.Agents);
        Assert.Null(world.Store.Token.ConsumedAt);
    }

    [Fact]
    public async Task AWiderMinorWindowAcceptsMinorVersionsInsideItButNotAnotherMajor()
    {
        var options = new AgentSecurityOptions
        {
            Protocol = new ProtocolNegotiationContract(new ProtocolVersion(1, 2),
                new ProtocolVersionRange(new ProtocolVersion(1, 0), new ProtocolVersion(1, 2)))
        };
        var world = World.Create(enabled: true, options);
        Assert.Equal(AgentEnrollmentStatus.Enrolled, (await world.Service.EnrollAsync(world.Request(protocolVersion: "1.2"), CancellationToken.None)).Status);
        var second = World.Create(enabled: true, options);
        Assert.Equal(AgentEnrollmentStatus.ProtocolIncompatible, (await second.Service.EnrollAsync(second.Request(protocolVersion: "2.0"), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task SoftwareVersionIsIndependentOfProtocolVersion()
    {
        var world = World.Create(enabled: true);
        var result = await world.Service.EnrollAsync(world.Request(agentVersion: "9.8.7", protocolVersion: "1.0"), CancellationToken.None);
        Assert.Equal(AgentEnrollmentStatus.Enrolled, result.Status);
        var stored = Assert.Single(world.Store.Agents);
        Assert.Equal(new Version(9, 8, 7), stored.Software.Version);
        Assert.Equal(new AgentProtocolVersion(1, 0), stored.Software.ProtocolVersion);
    }

    [Theory]
    [InlineData("installation")]
    [InlineData("host")]
    [InlineData("platform")]
    [InlineData("version")]
    [InlineData("capability")]
    public async Task InvalidMetadataIsRejectedWithoutConsumingTheToken(string field)
    {
        var world = World.Create(enabled: true);
        var request = field switch
        {
            "installation" => world.Request(installationId: Guid.Empty),
            "host" => world.Request(hostName: "  "),
            "platform" => world.Request(platform: "macos"),
            "version" => world.Request(agentVersion: "not-a-version"),
            _ => world.Request(capabilities: ["Not A Capability"])
        };
        var result = await world.Service.EnrollAsync(request, CancellationToken.None);
        Assert.Equal(AgentEnrollmentStatus.InvalidRequest, result.Status);
        Assert.Null(world.Store.Token.ConsumedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("00000000000000000000000000000001.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task MalformedOrUnknownTokensAreRejectedUniformly(string token)
    {
        var world = World.Create(enabled: true);
        var result = await world.Service.EnrollAsync(world.Request() with { EnrollmentToken = token }, CancellationToken.None);
        Assert.Equal(AgentEnrollmentStatus.InvalidToken, result.Status);
        Assert.Empty(world.Store.Agents);
    }

    [Fact]
    public async Task WrongSecretForARealTokenIsRejectedAndLeavesTheTokenUsable()
    {
        var world = World.Create(enabled: true);
        var forged = AgentSecretReference.Format(world.Store.Token.Id, new string('Z', AgentSecretReference.SecretLength));
        var result = await world.Service.EnrollAsync(world.Request() with { EnrollmentToken = forged }, CancellationToken.None);
        Assert.Equal(AgentEnrollmentStatus.InvalidToken, result.Status);
        Assert.Equal(AgentEnrollmentStatus.Enrolled, (await world.Service.EnrollAsync(world.Request(), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task ConsumedExpiredAndRevokedTokensCannotBeReused()
    {
        var consumed = World.Create(enabled: true);
        Assert.Equal(AgentEnrollmentStatus.Enrolled, (await consumed.Service.EnrollAsync(consumed.Request(), CancellationToken.None)).Status);
        Assert.Equal(AgentEnrollmentStatus.InvalidToken, (await consumed.Service.EnrollAsync(consumed.Request(), CancellationToken.None)).Status);
        Assert.Single(consumed.Store.Agents);

        var expired = World.Create(enabled: true);
        expired.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(AgentEnrollmentStatus.InvalidToken, (await expired.Service.EnrollAsync(expired.Request(), CancellationToken.None)).Status);

        var revoked = World.Create(enabled: true);
        revoked.Store.Token.Revoke(UserId.New(), Now);
        Assert.Equal(AgentEnrollmentStatus.InvalidToken, (await revoked.Service.EnrollAsync(revoked.Request(), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task DisablingEnrollmentAfterTokenCreationBlocksEnrollment()
    {
        var world = World.Create(enabled: false);
        var result = await world.Service.EnrollAsync(world.Request(), CancellationToken.None);
        Assert.Equal(AgentEnrollmentStatus.EnrollmentDisabled, result.Status);
        Assert.Null(world.Store.Token.ConsumedAt);
    }

    private sealed class World
    {
        public required FakeStore Store { get; init; }
        public required AgentEnrollmentService Service { get; init; }
        public required TestClock Clock { get; init; }
        public required string TokenPlaintext { get; init; }
        public TenantId TenantId => Store.Token.TenantId;

        public static World Create(bool enabled, AgentSecurityOptions? options = null)
        {
            var clock = new TestClock(Now);
            var issued = EnrollmentToken.Issue(TenantId.New(), UserId.New(), Now, TimeSpan.FromHours(1));
            var store = new FakeStore(issued.Token, enabled);
            return new World
            {
                Store = store,
                Clock = clock,
                TokenPlaintext = issued.Plaintext,
                Service = new AgentEnrollmentService(store, clock, options ?? new AgentSecurityOptions())
            };
        }

        public AgentEnrollmentRequest Request(
            Guid? installationId = null, string hostName = "build-01", string platform = "linux", string agentVersion = "1.4.2",
            string protocolVersion = "1.0", IReadOnlyList<string>? capabilities = null) => new()
        {
            EnrollmentToken = TokenPlaintext,
            InstallationId = installationId ?? Guid.Parse("11111111-2222-3333-4444-555555555555"),
            HostName = hostName,
            Platform = platform,
            AgentVersion = agentVersion,
            ProtocolVersion = protocolVersion,
            Capabilities = capabilities ?? ["backup.file-system"]
        };
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeStore(EnrollmentToken token, bool enabled) : IAgentEnrollmentStore
    {
        public EnrollmentToken Token { get; } = token;
        public List<Agent> Agents { get; } = [];

        public Task<EnrollmentPolicyState?> GetPolicyAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<EnrollmentPolicyState?>(new EnrollmentPolicyState(tenantId, enabled, null));

        public Task<EnrollmentPolicyChanged?> SetPolicyAsync(TenantId tenantId, bool isEnabled, UserId actor, AuditActorKind actorKind, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddTokenAsync(EnrollmentToken token, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<EnrollmentToken>> ListActiveTokensAsync(TenantId tenantId, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> RevokeTokenAsync(TenantId tenantId, Guid tokenId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnrollmentToken?> FindTokenAsync(Guid tokenId, CancellationToken cancellationToken) =>
            Task.FromResult(tokenId == Token.Id ? Token : null);

        public Task<EnrollmentCommit> EnrollAsync(
            Guid tokenId, TenantId tenantId, Agent agent, AgentCredential credential, DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (tokenId != Token.Id || Token.GetState(now) != EnrollmentTokenState.Active)
            {
                return Task.FromResult(EnrollmentCommit.TokenUnavailable);
            }
            Token.Consume(agent.Id, now);
            Agents.Add(agent);
            return Task.FromResult(EnrollmentCommit.Enrolled);
        }
    }
}
