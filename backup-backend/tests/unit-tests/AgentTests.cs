using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.UnitTests;

public sealed class AgentTests
{
    [Theory]
    [InlineData(AgentPlatform.Windows)]
    [InlineData(AgentPlatform.Linux)]
    public void LifecycleKeepsIdentityAndTracksCredentialGeneration(AgentPlatform platform)
    {
        var agent = CreateAgent(platform);
        var tenantId = agent.TenantId;
        var agentId = agent.Id;
        Assert.Equal(AgentStatus.PendingEnrollment, agent.Status);
        Assert.Equal(0, agent.CredentialGeneration);
        agent.RecordEnrollment();
        agent.Disable();
        agent.RecordCredentialRotation();
        Assert.Equal(AgentStatus.Disabled, agent.Status);
        Assert.Equal(2, agent.CredentialGeneration);
        agent.Enable();
        Assert.Equal(AgentStatus.Active, agent.Status);
        agent.Revoke();
        Assert.Equal(AgentStatus.Revoked, agent.Status);
        Assert.Equal(tenantId, agent.TenantId);
        Assert.Equal(agentId, agent.Id);
    }

    [Fact]
    public void PendingAgentCannotActAsEnrolled()
    {
        var agent = CreateAgent();
        Assert.Throws<InvalidOperationException>(agent.Disable);
        Assert.Throws<InvalidOperationException>(agent.Enable);
        Assert.Throws<InvalidOperationException>(agent.RecordCredentialRotation);
        Assert.Throws<InvalidOperationException>(() => agent.ReplaceCapabilities(CapabilitySet.Empty));
        Assert.Throws<InvalidOperationException>(() => agent.UpdateSoftware(Software()));
        Assert.Equal(0, agent.CredentialGeneration);
        agent.RecordEnrollment();
        Assert.Throws<InvalidOperationException>(agent.RecordEnrollment);
        Assert.Throws<InvalidOperationException>(agent.Enable);
        Assert.Equal(1, agent.CredentialGeneration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevocationIsTerminal(bool enrolled)
    {
        var agent = CreateAgent();
        if (enrolled)
        {
            agent.RecordEnrollment();
        }
        agent.Revoke();
        Assert.Throws<InvalidOperationException>(agent.RecordEnrollment);
        Assert.Throws<InvalidOperationException>(agent.RecordCredentialRotation);
        Assert.Throws<InvalidOperationException>(agent.Enable);
        Assert.Throws<InvalidOperationException>(agent.Disable);
        Assert.Throws<InvalidOperationException>(agent.Revoke);
        Assert.Throws<InvalidOperationException>(() => agent.UpdateSoftware(Software()));
        Assert.Throws<InvalidOperationException>(() => agent.ReplaceCapabilities(CapabilitySet.Empty));
        Assert.Equal(AgentStatus.Revoked, agent.Status);
    }

    [Fact]
    public void SoftwareAndCapabilitiesCanChangeWithoutChangingPlatformIdentity()
    {
        var agent = CreateAgent();
        agent.RecordEnrollment();
        var capability = new Capability(new CapabilityIdentifier("agent.example"), CapabilitySupport.Supported);
        agent.ReplaceCapabilities(new CapabilitySet([capability]));
        agent.UpdateSoftware(new AgentSoftware(AgentPlatform.Windows, new Version(9, 0), new AgentProtocolVersion(1, 2)));
        Assert.Equal(new Version(9, 0), agent.Software.Version);
        Assert.Equal(new AgentProtocolVersion(1, 2), agent.Software.ProtocolVersion);
        Assert.True(agent.Capabilities.TryGet(capability.Identifier, out var reported));
        Assert.Same(capability, reported);
        Assert.Throws<InvalidOperationException>(() => agent.UpdateSoftware(Software(AgentPlatform.Linux)));
        Assert.Equal(AgentPlatform.Windows, agent.Software.Platform);
    }

    [Fact]
    public void InvalidModelDataIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new Agent(default!, TenantId.New(), Software()));
        Assert.Throws<ArgumentNullException>(() => new Agent(AgentId.New(), default!, Software()));
        Assert.Throws<ArgumentNullException>(() => new Agent(AgentId.New(), TenantId.New(), null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => Software((AgentPlatform)0));
        Assert.Throws<ArgumentNullException>(() => new AgentSoftware(AgentPlatform.Windows, null!, new AgentProtocolVersion(1, 0)));
        Assert.Throws<ArgumentNullException>(() => new AgentSoftware(AgentPlatform.Windows, new Version(1, 0), null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentProtocolVersion(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentProtocolVersion(1, -1));
        var agent = CreateAgent();
        Assert.Throws<ArgumentNullException>(() => agent.UpdateSoftware(null!));
        Assert.Throws<ArgumentNullException>(() => agent.ReplaceCapabilities(null!));
    }

    private static Agent CreateAgent(AgentPlatform platform = AgentPlatform.Windows) =>
        new(AgentId.New(), TenantId.New(), Software(platform));

    private static AgentSoftware Software(AgentPlatform platform = AgentPlatform.Windows) =>
        new(platform, new Version(1, 0), new AgentProtocolVersion(1, 0));
}
