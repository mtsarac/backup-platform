namespace BackupPlatform.Agent.Core.Identity;

/// <summary>
/// Persists the agent's local identity with platform-appropriate protection. Implementations live in the platform
/// projects; the core never knows how or where the identity is stored.
/// </summary>
/// <remarks>
/// <see cref="SaveAsync"/> must replace the stored identity atomically: after a crash a reader sees either the old or
/// the new identity in full, never a partial one. A store protects against other local users and offline disk access
/// as far as the platform allows; it cannot protect against a compromised administrator or root account.
/// </remarks>
public interface IAgentCredentialStore
{
    /// <summary>Null when no identity has been stored. Throws <see cref="AgentCredentialStoreException"/> when stored data is unusable or insecure.</summary>
    Task<AgentLocalIdentity?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(AgentLocalIdentity identity, CancellationToken cancellationToken);

    /// <summary>Removes the stored identity. Succeeds when nothing is stored.</summary>
    Task DeleteAsync(CancellationToken cancellationToken);
}

/// <summary>The stored identity cannot be trusted or read; the message never contains secret material.</summary>
public sealed class AgentCredentialStoreException : Exception
{
    public AgentCredentialStoreException(string message) : base(message) { }

    public AgentCredentialStoreException(string message, Exception innerException) : base(message, innerException) { }
}
