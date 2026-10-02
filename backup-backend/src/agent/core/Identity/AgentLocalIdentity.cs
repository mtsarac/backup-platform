namespace BackupPlatform.Agent.Core.Identity;

/// <summary>
/// The minimum identity an agent must keep across restarts. <see cref="Pending"/> is a rotation credential the agent
/// has persisted but not yet proven to the backend; keeping it beside <see cref="Current"/> lets an agent that crashed
/// mid-rotation resume by confirming, or fall back to <see cref="Current"/>, instead of being locked out.
/// </summary>
public sealed record AgentLocalIdentity
{
    public AgentLocalIdentity(Guid agentId, Uri serverUri, Guid installationId, AgentStoredCredential current, AgentStoredCredential? pending = null)
    {
        if (agentId == Guid.Empty)
        {
            throw new ArgumentException("An agent identifier must not be empty.", nameof(agentId));
        }
        ArgumentNullException.ThrowIfNull(serverUri);
        if (!serverUri.IsAbsoluteUri || serverUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The backend address must be an absolute HTTPS URI.", nameof(serverUri));
        }
        if (installationId == Guid.Empty)
        {
            throw new ArgumentException("An installation identifier must not be empty.", nameof(installationId));
        }
        ArgumentNullException.ThrowIfNull(current);
        if (pending is not null && pending.Generation != current.Generation + 1)
        {
            throw new ArgumentException("A pending credential must be the generation after the current credential.", nameof(pending));
        }
        AgentId = agentId;
        ServerUri = serverUri;
        InstallationId = installationId;
        Current = current;
        Pending = pending;
    }

    public Guid AgentId { get; }
    public Uri ServerUri { get; }
    public Guid InstallationId { get; }
    public AgentStoredCredential Current { get; }
    public AgentStoredCredential? Pending { get; }

    /// <summary>Records a newly issued rotation credential after it has been durably stored, replacing any earlier pending one.</summary>
    public AgentLocalIdentity WithPending(AgentStoredCredential pending) => new(AgentId, ServerUri, InstallationId, Current, pending);

    /// <summary>Promotes the pending credential once the backend confirmed it.</summary>
    public AgentLocalIdentity WithPendingPromoted() =>
        new(AgentId, ServerUri, InstallationId, Pending ?? throw new InvalidOperationException("There is no pending credential to promote."));

    /// <summary>Drops the pending credential, for example when the backend no longer recognizes it.</summary>
    public AgentLocalIdentity WithoutPending() => new(AgentId, ServerUri, InstallationId, Current);
}
