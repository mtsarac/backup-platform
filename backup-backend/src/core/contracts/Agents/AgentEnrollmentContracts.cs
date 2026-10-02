using System.Text;

namespace BackupPlatform.Contracts.Agents;

/// <summary>
/// First-contact request. The enrollment token is the only credential and is consumed on success.
/// Wire JSON uses camelCase property names. <see cref="InstallationId"/> is a client-generated, persistent
/// installation identifier used for duplicate detection; it is not an authentication factor and no hardware
/// fingerprint is collected.
/// </summary>
public sealed record AgentEnrollmentRequest
{
    public string EnrollmentToken { get; init; } = string.Empty;
    public Guid InstallationId { get; init; }
    /// <summary>Agent software release version, for example <c>1.4.2</c>. Not the protocol version.</summary>
    public string AgentVersion { get; init; } = string.Empty;
    /// <summary>Wire protocol version as <c>major.minor</c>.</summary>
    public string ProtocolVersion { get; init; } = string.Empty;
    /// <summary>One of <see cref="AgentPlatformNames"/>.</summary>
    public string Platform { get; init; } = string.Empty;
    public string HostName { get; init; } = string.Empty;
    /// <summary>
    /// Extension point for dotted capability identifiers. Shape is validated; capability persistence and
    /// reporting are intentionally deferred, so the backend does not store these values yet.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"InstallationId = {InstallationId}, AgentVersion = {AgentVersion}, ProtocolVersion = {ProtocolVersion}, Platform = {Platform}, EnrollmentToken = [redacted]");
        return true;
    }
}

/// <summary>A credential returned in plaintext exactly once, at issuance. It cannot be read back later.</summary>
public sealed record IssuedAgentCredential
{
    public Guid CredentialId { get; init; }
    public string Secret { get; init; } = string.Empty;
    public int Generation { get; init; }
    public DateTimeOffset IssuedAt { get; init; }
    /// <summary>Set only for a pending rotation credential that must be confirmed before this instant.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"CredentialId = {CredentialId}, Generation = {Generation}, Secret = [redacted]");
        return true;
    }
}

public sealed record AgentServerProtocol
{
    public string Current { get; init; } = string.Empty;
    public string MinimumSupported { get; init; } = string.Empty;
    public string MaximumSupported { get; init; } = string.Empty;
}

public sealed record AgentEnrollmentResponse
{
    public Guid AgentId { get; init; }
    public IssuedAgentCredential Credential { get; init; } = new();
    public AgentServerProtocol Protocol { get; init; } = new();
}

/// <summary>Credential presented to the agent API (activation state is established by rotation confirmation).</summary>
public sealed record AgentIdentityResponse
{
    public Guid AgentId { get; init; }
    public Guid TenantId { get; init; }
    public Guid CredentialId { get; init; }
    public int CredentialGeneration { get; init; }
}

public sealed record AgentRotationConfirmationResponse
{
    public int CredentialGeneration { get; init; }
    /// <summary>True when the credential was already active, so a retried confirmation changed nothing.</summary>
    public bool AlreadyConfirmed { get; init; }
}

/// <summary>Machine-readable failure; never echoes credentials or tokens.</summary>
public sealed record AgentApiError
{
    public string Error { get; init; } = string.Empty;
    public string? Message { get; init; }
    public AgentServerProtocol? SupportedProtocol { get; init; }
}
