using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Protocol;

namespace BackupPlatform.UnitTests;

public sealed class AgentWireFormatTests
{
    private static readonly string Secret = new('A', AgentSecretReference.SecretLength);

    [Fact]
    public void ReferenceRoundTripsAndHeaderUsesTheExplicitAgentScheme()
    {
        var id = Guid.NewGuid();
        var header = AgentAuthorizationScheme.FormatHeaderValue(id, Secret);
        Assert.StartsWith("BackupAgent ", header, StringComparison.Ordinal);
        Assert.True(AgentAuthorizationScheme.TryParseHeaderValue(header, out var parsedId, out var parsedSecret));
        Assert.Equal(id, parsedId);
        Assert.Equal(Secret, parsedSecret);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer abcdef")]
    [InlineData("BackupAgent")]
    [InlineData("BackupAgent  00000000000000000000000000000001.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("BackupAgent 00000000000000000000000000000000.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("BackupAgent 00000000000000000000000000000001.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("BackupAgent 00000000000000000000000000000001.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("BackupAgent 0000000000000000000000000000000z.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void MalformedHeadersAreRejectedWithoutThrowing(string? header)
    {
        Assert.False(AgentAuthorizationScheme.TryParseHeaderValue(header, out var id, out var secret));
        Assert.Equal(Guid.Empty, id);
        Assert.Equal(string.Empty, secret);
    }

    [Fact]
    public void DiagnosticOutputNeverContainsTheEnrollmentTokenOrCredentialSecret()
    {
        var request = new AgentEnrollmentRequest { EnrollmentToken = $"{Guid.NewGuid():N}.{Secret}", HostName = "host" };
        var credential = new IssuedAgentCredential { CredentialId = Guid.NewGuid(), Secret = Secret, Generation = 1 };
        var response = new AgentEnrollmentResponse { AgentId = Guid.NewGuid(), Credential = credential };
        Assert.DoesNotContain(Secret, request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, response.ToString(), StringComparison.Ordinal);
        Assert.Contains("[redacted]", credential.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.0", 1, 0)]
    [InlineData("2.14", 2, 14)]
    public void ProtocolVersionParsesItsOwnStringForm(string text, int major, int minor)
    {
        Assert.True(ProtocolVersion.TryParse(text, out var version));
        Assert.Equal(new ProtocolVersion(major, minor), version);
        Assert.Equal(text, version.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData(".1")]
    [InlineData("1.")]
    [InlineData("0.1")]
    [InlineData("1.-1")]
    [InlineData("+1.0")]
    [InlineData("1.0.0")]
    [InlineData("1.x")]
    [InlineData("99999999999.0")]
    public void ProtocolVersionRejectsAnythingButMajorDotMinor(string? text)
    {
        Assert.False(ProtocolVersion.TryParse(text, out var version));
        Assert.Null(version);
    }
}
