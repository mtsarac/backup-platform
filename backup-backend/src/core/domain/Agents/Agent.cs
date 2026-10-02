using BackupPlatform.Domain.Capabilities;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Agents;

/// <summary>Tenant-bound agent identity. Lifecycle methods record facts; they do not issue or validate credentials.</summary>
/// <remarks>
/// Future application flows must authorize enrollment and rotation before recording them here.
/// CredentialGeneration tracks replacement without storing credential material.
/// Revocation is terminal; re-enrollment requires a new agent identity.
/// Software/protocol versions are independent; this model does not decide protocol compatibility.
/// </remarks>
public sealed class Agent : ITenantOwned
{
    public Agent(AgentId id, TenantId tenantId, AgentSoftware software)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(software);
        Id = id;
        TenantId = tenantId;
        Software = software;
    }

    public AgentId Id { get; }
    public TenantId TenantId { get; }
    public AgentSoftware Software { get; private set; }
    public AgentStatus Status { get; private set; } = AgentStatus.PendingEnrollment;
    public CapabilitySet Capabilities { get; private set; } = CapabilitySet.Empty;
    public int CredentialGeneration { get; private set; }

    public void RecordEnrollment()
    {
        RequireStatus(AgentStatus.PendingEnrollment);
        CredentialGeneration = 1;
        Status = AgentStatus.Active;
    }

    public void Disable()
    {
        RequireStatus(AgentStatus.Active);
        Status = AgentStatus.Disabled;
    }

    public void Enable()
    {
        RequireStatus(AgentStatus.Disabled);
        Status = AgentStatus.Active;
    }

    public void Revoke()
    {
        EnsureNotRevoked();
        Status = AgentStatus.Revoked;
    }

    public void RecordCredentialRotation()
    {
        EnsureEnrolled();
        CredentialGeneration = checked(CredentialGeneration + 1);
    }

    public void UpdateSoftware(AgentSoftware software)
    {
        ArgumentNullException.ThrowIfNull(software);
        EnsureEnrolled();
        if (software.Platform != Software.Platform)
        {
            throw new InvalidOperationException("An agent cannot change its operating system identity.");
        }
        Software = software;
    }

    public void ReplaceCapabilities(CapabilitySet capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        EnsureEnrolled();
        Capabilities = capabilities;
    }

    private void EnsureEnrolled()
    {
        if (Status is not (AgentStatus.Active or AgentStatus.Disabled))
        {
            throw new InvalidOperationException("The agent must be enrolled and not revoked.");
        }
    }

    private void EnsureNotRevoked()
    {
        if (Status == AgentStatus.Revoked)
        {
            throw new InvalidOperationException("Revocation is terminal for this agent identity.");
        }
    }

    private void RequireStatus(AgentStatus status)
    {
        if (Status != status)
        {
            throw new InvalidOperationException($"This operation requires status {status}.");
        }
    }
}
