using BackupPlatform.Agents.Credentials;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Agents.Enrollment;

public enum EnrollmentTokenState
{
    Active = 1,
    Expired = 2,
    Consumed = 3,
    Revoked = 4
}

/// <summary>
/// A tenant-bound, single-use, expiring enrollment authorization. Only the hash of the secret is retained.
/// </summary>
/// <remarks>
/// Consumption and revocation are terminal and exclusive. Persistence applies them under optimistic concurrency,
/// so of several concurrent consumers exactly one can commit.
/// </remarks>
public sealed class EnrollmentToken : ITenantOwned
{
    private EnrollmentToken()
    {
        TenantId = null!;
        SecretHash = [];
        CreatedBy = null!;
    }

    private EnrollmentToken(Guid id, TenantId tenantId, byte[] secretHash, UserId createdBy, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A token identifier must not be empty.", nameof(id));
        }
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(createdBy);
        SecretTokens.RequireValidHash(secretHash);
        if (expiresAt <= createdAt)
        {
            throw new ArgumentException("A token must expire after it is created.", nameof(expiresAt));
        }
        Id = id;
        TenantId = tenantId;
        SecretHash = (byte[])secretHash.Clone();
        CreatedBy = createdBy;
        CreatedAt = createdAt.ToUniversalTime();
        ExpiresAt = expiresAt.ToUniversalTime();
    }

    /// <summary>The public identifier used to find, list and revoke the token. Not secret.</summary>
    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public byte[] SecretHash { get; private set; }
    public UserId CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }
    public AgentId? ConsumedByAgentId { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public UserId? RevokedBy { get; private set; }

    public static NewEnrollmentToken Issue(TenantId tenantId, UserId createdBy, DateTimeOffset now, TimeSpan lifetime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        var secret = SecretTokens.NewSecret();
        var token = new EnrollmentToken(Guid.NewGuid(), tenantId, SecretTokens.Hash(SecretPurpose.EnrollmentToken, secret),
            createdBy, now, now + lifetime);
        return new NewEnrollmentToken(token, secret);
    }

    public EnrollmentTokenState GetState(DateTimeOffset now)
    {
        if (ConsumedAt is not null)
        {
            return EnrollmentTokenState.Consumed;
        }
        if (RevokedAt is not null)
        {
            return EnrollmentTokenState.Revoked;
        }
        return now >= ExpiresAt ? EnrollmentTokenState.Expired : EnrollmentTokenState.Active;
    }

    public bool VerifySecret(string presentedSecret)
    {
        ArgumentNullException.ThrowIfNull(presentedSecret);
        return SecretTokens.Verify(SecretPurpose.EnrollmentToken, SecretHash, presentedSecret);
    }

    public static void BurnVerification(string presentedSecret) =>
        SecretTokens.BurnVerification(SecretPurpose.EnrollmentToken, presentedSecret);

    public void Consume(AgentId agentId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        RequireActive(now);
        ConsumedAt = now.ToUniversalTime();
        ConsumedByAgentId = agentId;
    }

    public void Revoke(UserId revokedBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(revokedBy);
        if (ConsumedAt is not null || RevokedAt is not null)
        {
            throw new InvalidOperationException("Only an unconsumed, unrevoked token can be revoked.");
        }
        RevokedAt = now.ToUniversalTime();
        RevokedBy = revokedBy;
    }

    private void RequireActive(DateTimeOffset now)
    {
        if (GetState(now) != EnrollmentTokenState.Active)
        {
            throw new InvalidOperationException("The enrollment token is not active.");
        }
    }
}

/// <summary>A freshly issued token and its one-time plaintext. The plaintext is excluded from diagnostics.</summary>
public sealed record NewEnrollmentToken(EnrollmentToken Token, string Secret)
{
    /// <summary>The string handed to the administrator: <c>&lt;token id&gt;.&lt;secret&gt;</c>.</summary>
    public string Plaintext => BackupPlatform.Contracts.Agents.AgentSecretReference.Format(Token.Id, Secret);

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"TokenId = {Token.Id}, Secret = [redacted]");
        return true;
    }
}
