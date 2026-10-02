using BackupPlatform.Agents.Credentials;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Protocol;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Agents.Enrollment;

public enum AgentEnrollmentStatus
{
    Enrolled = 1,
    /// <summary>Unknown, malformed, expired, consumed or revoked; deliberately indistinguishable.</summary>
    InvalidToken = 2,
    InvalidRequest = 3,
    EnrollmentDisabled = 4,
    ProtocolIncompatible = 5,
    InstallationConflict = 6
}

/// <summary>Facts about a completed enrollment, shaped for later auditing; carries the one-time credential.</summary>
public sealed record AgentEnrolled(AgentId AgentId, TenantId TenantId, Guid TokenId, NewAgentCredential Credential, DateTimeOffset At);

public sealed record AgentEnrollmentResult(
    AgentEnrollmentStatus Status, AgentEnrolled? Enrolled, string? InvalidField, ProtocolNegotiationContract? ServerProtocol);

/// <summary>
/// First-contact enrollment authorized only by an enrollment token. The token is verified before any other input is
/// trusted, and consumed atomically with agent creation so a concurrent second use cannot also succeed.
/// </summary>
public sealed class AgentEnrollmentService(IAgentEnrollmentStore store, TimeProvider timeProvider, AgentSecurityOptions options)
{
    private const int MaximumCapabilities = 128;
    private const int MaximumCapabilityLength = 128;
    private const int MaximumVersionLength = 64;

    public async Task<AgentEnrollmentResult> EnrollAsync(AgentEnrollmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = timeProvider.GetUtcNow();
        var token = await FindUsableTokenAsync(request.EnrollmentToken, now, cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return Failure(AgentEnrollmentStatus.InvalidToken);
        }
        if (!TryReadSoftware(request, out var software, out var hostName, out var invalidField, out var protocol))
        {
            return Failure(AgentEnrollmentStatus.InvalidRequest, invalidField);
        }
        if (!options.Protocol.SupportedVersions.Contains(protocol))
        {
            return new AgentEnrollmentResult(AgentEnrollmentStatus.ProtocolIncompatible, null, null, options.Protocol);
        }
        var policy = await store.GetPolicyAsync(token.TenantId, cancellationToken).ConfigureAwait(false);
        if (policy is not { IsEnabled: true })
        {
            return Failure(AgentEnrollmentStatus.EnrollmentDisabled);
        }
        var agent = new Agent(AgentId.New(), token.TenantId, software, hostName, request.InstallationId);
        agent.RecordEnrollment();
        var credential = AgentCredential.IssueInitial(agent.Id, agent.TenantId, now);
        var commit = await store.EnrollAsync(token.Id, token.TenantId, agent, credential.Credential, now, cancellationToken).ConfigureAwait(false);
        return commit switch
        {
            EnrollmentCommit.Enrolled => new AgentEnrollmentResult(AgentEnrollmentStatus.Enrolled,
                new AgentEnrolled(agent.Id, agent.TenantId, token.Id, credential, now), null, null),
            EnrollmentCommit.InstallationConflict => Failure(AgentEnrollmentStatus.InstallationConflict),
            _ => Failure(AgentEnrollmentStatus.InvalidToken)
        };
    }

    private async Task<EnrollmentToken?> FindUsableTokenAsync(string presented, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!AgentSecretReference.TryParse(presented, out var tokenId, out var secret))
        {
            EnrollmentToken.BurnVerification(new string('A', AgentSecretReference.SecretLength));
            return null;
        }
        var token = await store.FindTokenAsync(tokenId, cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            EnrollmentToken.BurnVerification(secret);
            return null;
        }
        return token.VerifySecret(secret) && token.GetState(now) == EnrollmentTokenState.Active ? token : null;
    }

    private static bool TryReadSoftware(
        AgentEnrollmentRequest request, out AgentSoftware software, out string? hostName, out string? invalidField, out ProtocolVersion protocol)
    {
        software = null!;
        hostName = null;
        invalidField = null;
        protocol = null!;
        if (request.InstallationId == Guid.Empty)
        {
            invalidField = nameof(request.InstallationId);
            return false;
        }
        var host = request.HostName?.Trim();
        if (string.IsNullOrEmpty(host) || host.Length > Agent.MaximumHostNameLength || host.Any(char.IsControl))
        {
            invalidField = nameof(request.HostName);
            return false;
        }
        AgentPlatform platform;
        if (string.Equals(request.Platform, AgentPlatformNames.Windows, StringComparison.OrdinalIgnoreCase))
        {
            platform = AgentPlatform.Windows;
        }
        else if (string.Equals(request.Platform, AgentPlatformNames.Linux, StringComparison.OrdinalIgnoreCase))
        {
            platform = AgentPlatform.Linux;
        }
        else
        {
            invalidField = nameof(request.Platform);
            return false;
        }
        if (request.AgentVersion is not { Length: > 0 and <= MaximumVersionLength } || !Version.TryParse(request.AgentVersion, out var version))
        {
            invalidField = nameof(request.AgentVersion);
            return false;
        }
        if (!ProtocolVersion.TryParse(request.ProtocolVersion, out var parsedProtocol))
        {
            invalidField = nameof(request.ProtocolVersion);
            return false;
        }
        if (!AreCapabilitiesWellFormed(request.Capabilities))
        {
            invalidField = nameof(request.Capabilities);
            return false;
        }
        protocol = parsedProtocol;
        hostName = host;
        software = new AgentSoftware(platform, version, new AgentProtocolVersion(parsedProtocol.Major, parsedProtocol.Minor));
        return true;
    }

    private static bool AreCapabilitiesWellFormed(IReadOnlyList<string>? capabilities)
    {
        if (capabilities is null)
        {
            return true;
        }
        if (capabilities.Count > MaximumCapabilities)
        {
            return false;
        }
        foreach (var capability in capabilities)
        {
            if (capability is null || capability.Length > MaximumCapabilityLength)
            {
                return false;
            }
            try
            {
                _ = new CapabilityIdentifier(capability);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
        return true;
    }

    private static AgentEnrollmentResult Failure(AgentEnrollmentStatus status, string? invalidField = null) =>
        new(status, null, invalidField, null);
}
