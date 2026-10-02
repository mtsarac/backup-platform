using BackupPlatform.Agents.Enrollment;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.UnitTests;

public sealed class AgentEnrollmentTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IssuedTokensAreUniqueHighEntropyAndBoundToTheirTenant()
    {
        var tenant = TenantId.New();
        var user = UserId.New();
        var issued = Enumerable.Range(0, 50).Select(_ => EnrollmentToken.Issue(tenant, user, Now, TimeSpan.FromHours(1))).ToArray();
        Assert.Equal(50, issued.Select(item => item.Secret).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(50, issued.Select(item => item.Token.Id).Distinct().Count());
        Assert.All(issued, item =>
        {
            Assert.Equal(tenant, item.Token.TenantId);
            Assert.Equal(user, item.Token.CreatedBy);
            Assert.Equal(AgentSecretReference.SecretLength, item.Secret.Length);
            Assert.True(AgentSecretReference.TryParse(item.Plaintext, out var id, out var secret));
            Assert.Equal(item.Token.Id, id);
            Assert.Equal(item.Secret, secret);
            Assert.Equal(32, item.Token.SecretHash.Length);
            Assert.DoesNotContain(item.Secret, item.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void OnlyTheIssuedSecretVerifiesAndTheStoredHashIsNotTheSecret()
    {
        var issued = EnrollmentToken.Issue(TenantId.New(), UserId.New(), Now, TimeSpan.FromHours(1));
        Assert.True(issued.Token.VerifySecret(issued.Secret));
        Assert.False(issued.Token.VerifySecret(new string('B', AgentSecretReference.SecretLength)));
        Assert.False(issued.Token.VerifySecret(string.Empty));
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes(issued.Secret), issued.Token.SecretHash);
    }

    [Fact]
    public void ExpirationIsEnforcedAtTheBoundary()
    {
        var token = EnrollmentToken.Issue(TenantId.New(), UserId.New(), Now, TimeSpan.FromMinutes(10)).Token;
        Assert.Equal(EnrollmentTokenState.Active, token.GetState(Now.AddMinutes(10).AddTicks(-1)));
        Assert.Equal(EnrollmentTokenState.Expired, token.GetState(Now.AddMinutes(10)));
        Assert.Throws<InvalidOperationException>(() => token.Consume(AgentId.New(), Now.AddMinutes(10)));
    }

    [Fact]
    public void ConsumptionIsTerminalAndExcludesRevocation()
    {
        var token = EnrollmentToken.Issue(TenantId.New(), UserId.New(), Now, TimeSpan.FromHours(1)).Token;
        var agent = AgentId.New();
        token.Consume(agent, Now);
        Assert.Equal(EnrollmentTokenState.Consumed, token.GetState(Now));
        Assert.Equal(agent, token.ConsumedByAgentId);
        Assert.Throws<InvalidOperationException>(() => token.Consume(AgentId.New(), Now));
        Assert.Throws<InvalidOperationException>(() => token.Revoke(UserId.New(), Now));
    }

    [Fact]
    public void RevocationIsTerminalAndBlocksConsumption()
    {
        var token = EnrollmentToken.Issue(TenantId.New(), UserId.New(), Now, TimeSpan.FromHours(1)).Token;
        var revoker = UserId.New();
        token.Revoke(revoker, Now);
        Assert.Equal(EnrollmentTokenState.Revoked, token.GetState(Now));
        Assert.Equal(revoker, token.RevokedBy);
        Assert.Throws<InvalidOperationException>(() => token.Consume(AgentId.New(), Now));
        Assert.Throws<InvalidOperationException>(() => token.Revoke(revoker, Now));
    }

    [Fact]
    public void NonPositiveLifetimesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EnrollmentToken.Issue(TenantId.New(), UserId.New(), Now, TimeSpan.Zero));
    }
}
