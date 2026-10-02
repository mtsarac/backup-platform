using System.Text;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Core.Identity;

/// <summary>One credential generation as the agent keeps it. The secret never appears in <see cref="object.ToString"/> or logs.</summary>
public sealed record AgentStoredCredential
{
    public AgentStoredCredential(Guid credentialId, string secret, int generation)
    {
        if (credentialId == Guid.Empty)
        {
            throw new ArgumentException("A credential identifier must not be empty.", nameof(credentialId));
        }
        ArgumentNullException.ThrowIfNull(secret);
        if (!AgentSecretReference.IsWellFormedSecret(secret))
        {
            throw new ArgumentException("The credential secret is malformed.", nameof(secret));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        CredentialId = credentialId;
        Secret = secret;
        Generation = generation;
    }

    public Guid CredentialId { get; }
    public string Secret { get; }
    public int Generation { get; }

    /// <summary>The <c>Authorization</c> header value for the backend's agent scheme. Send only over TLS.</summary>
    public string ToAuthorizationHeaderValue() => AgentAuthorizationScheme.FormatHeaderValue(CredentialId, Secret);

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"CredentialId = {CredentialId}, Generation = {Generation}, Secret = [redacted]");
        return true;
    }
}
