using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Agents.Credentials;

public enum AgentCredentialStatus
{
    /// <summary>Issued by a rotation but not yet proven; authenticates only the confirmation call.</summary>
    Pending = 1,
    Active = 2,
    Revoked = 3
}

public enum AgentCredentialRevocationReason
{
    /// <summary>Replaced by a confirmed rotation.</summary>
    Superseded = 1,
    /// <summary>A pending credential replaced by a newer rotation attempt.</summary>
    RotationReplaced = 2,
    AgentRevoked = 3
}

/// <summary>
/// A verifiable agent secret generation. Only a hash of the secret exists; plaintext is returned once at issuance.
/// </summary>
/// <remarks>
/// Deliberately unrelated to user password credentials. At most one Active and one Pending credential exist per agent
/// (enforced by persistence). A Pending credential has generation Agent.CredentialGeneration + 1 and becomes Active only
/// through confirmation, which also revokes the previous Active credential.
/// </remarks>
public sealed class AgentCredential : ITenantOwned
{
    private AgentCredential()
    {
        Id = Guid.Empty;
        AgentId = null!;
        TenantId = null!;
        SecretHash = [];
    }

    private AgentCredential(
        Guid id, AgentId agentId, TenantId tenantId, int generation, byte[] secretHash,
        AgentCredentialStatus status, DateTimeOffset issuedAt, DateTimeOffset? expiresAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A credential identifier must not be empty.", nameof(id));
        }
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        SecretTokens.RequireValidHash(secretHash);
        Id = id;
        AgentId = agentId;
        TenantId = tenantId;
        Generation = generation;
        SecretHash = (byte[])secretHash.Clone();
        Status = status;
        IssuedAt = issuedAt.ToUniversalTime();
        ExpiresAt = expiresAt?.ToUniversalTime();
        ActivatedAt = status == AgentCredentialStatus.Active ? IssuedAt : null;
    }

    /// <summary>The public identifier presented alongside the secret. Not secret.</summary>
    public Guid Id { get; private set; }
    public AgentId AgentId { get; private set; }
    public TenantId TenantId { get; private set; }
    public int Generation { get; private set; }
    public byte[] SecretHash { get; private set; }
    public AgentCredentialStatus Status { get; private set; }
    public DateTimeOffset IssuedAt { get; private set; }
    /// <summary>Only pending credentials expire; an active credential lives until superseded or revoked.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public AgentCredentialRevocationReason? RevocationReason { get; private set; }

    /// <summary>Creates the first active credential for a newly enrolled agent (generation 1).</summary>
    public static NewAgentCredential IssueInitial(AgentId agentId, TenantId tenantId, DateTimeOffset now)
    {
        var secret = SecretTokens.NewSecret();
        var credential = new AgentCredential(Guid.NewGuid(), agentId, tenantId, 1,
            SecretTokens.Hash(SecretPurpose.AgentCredential, secret), AgentCredentialStatus.Active, now, null);
        return new NewAgentCredential(credential, secret);
    }

    /// <summary>Creates the pending credential of a rotation with a bounded confirmation window.</summary>
    public static NewAgentCredential IssuePending(AgentId agentId, TenantId tenantId, int generation, DateTimeOffset now, TimeSpan lifetime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        var secret = SecretTokens.NewSecret();
        var credential = new AgentCredential(Guid.NewGuid(), agentId, tenantId, generation,
            SecretTokens.Hash(SecretPurpose.AgentCredential, secret), AgentCredentialStatus.Pending, now, now + lifetime);
        return new NewAgentCredential(credential, secret);
    }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expiresAt && now >= expiresAt;

    /// <summary>Constant-time verification of a presented secret against the stored hash.</summary>
    public bool VerifySecret(string presentedSecret)
    {
        ArgumentNullException.ThrowIfNull(presentedSecret);
        return SecretTokens.Verify(SecretPurpose.AgentCredential, SecretHash, presentedSecret);
    }

    /// <summary>Burns comparable verification work for an unknown credential identifier.</summary>
    public static void BurnVerification(string presentedSecret) =>
        SecretTokens.BurnVerification(SecretPurpose.AgentCredential, presentedSecret);

    public void Activate(DateTimeOffset now)
    {
        if (Status != AgentCredentialStatus.Pending)
        {
            throw new InvalidOperationException("Only a pending credential can be activated.");
        }
        if (IsExpired(now))
        {
            throw new InvalidOperationException("An expired pending credential cannot be activated.");
        }
        Status = AgentCredentialStatus.Active;
        ActivatedAt = now.ToUniversalTime();
        ExpiresAt = null;
    }

    public void Revoke(AgentCredentialRevocationReason reason, DateTimeOffset now)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }
        if (Status == AgentCredentialStatus.Revoked)
        {
            throw new InvalidOperationException("Revocation is terminal for a credential.");
        }
        Status = AgentCredentialStatus.Revoked;
        RevokedAt = now.ToUniversalTime();
        RevocationReason = reason;
    }
}

/// <summary>A freshly issued credential and its one-time plaintext secret. The secret is excluded from diagnostics.</summary>
public sealed record NewAgentCredential(AgentCredential Credential, string Secret)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"CredentialId = {Credential.Id}, Generation = {Credential.Generation}, Secret = [redacted]");
        return true;
    }
}
