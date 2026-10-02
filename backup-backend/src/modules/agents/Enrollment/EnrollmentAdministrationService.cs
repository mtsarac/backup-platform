using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Agents.Enrollment;

public enum CreateEnrollmentTokenStatus
{
    Created = 1,
    EnrollmentDisabled = 2,
    InvalidLifetime = 3
}

public sealed record CreateEnrollmentTokenResult(CreateEnrollmentTokenStatus Status, NewEnrollmentToken? Issued);

/// <summary>Application-level facts about a policy change, shaped for later auditing; no event bus.</summary>
public sealed record EnrollmentPolicyChanged(TenantId TenantId, bool PreviousEnabled, bool IsEnabled, UserId ChangedBy, DateTimeOffset At)
{
    public bool Changed => PreviousEnabled != IsEnabled;
}

public sealed record EnrollmentTokenRevoked(TenantId TenantId, Guid TokenId, UserId RevokedBy, DateTimeOffset At);

/// <summary>
/// Policy and token administration. Authorization is the caller's responsibility: platform administrator for
/// <see cref="SetEnrollmentEnabledAsync"/>, tenant administrator of the same tenant for the token operations.
/// </summary>
public sealed class EnrollmentAdministrationService(IAgentEnrollmentStore store, TimeProvider timeProvider, AgentSecurityOptions options)
{
    public Task<EnrollmentPolicyState?> GetPolicyAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        return store.GetPolicyAsync(tenantId, cancellationToken);
    }

    public Task<EnrollmentPolicyChanged?> SetEnrollmentEnabledAsync(
        TenantId tenantId, bool isEnabled, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(actor);
        return store.SetPolicyAsync(tenantId, isEnabled, actor, timeProvider.GetUtcNow(), cancellationToken);
    }

    /// <summary>Creates a token only while the tenant's enrollment permission is enabled. The plaintext is returned once.</summary>
    public async Task<CreateEnrollmentTokenResult> CreateTokenAsync(
        TenantId tenantId, UserId createdBy, TimeSpan? lifetime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(createdBy);
        var effective = lifetime ?? options.DefaultEnrollmentTokenLifetime;
        if (effective < options.MinimumEnrollmentTokenLifetime || effective > options.MaximumEnrollmentTokenLifetime)
        {
            return new CreateEnrollmentTokenResult(CreateEnrollmentTokenStatus.InvalidLifetime, null);
        }
        var policy = await store.GetPolicyAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (policy is not { IsEnabled: true })
        {
            return new CreateEnrollmentTokenResult(CreateEnrollmentTokenStatus.EnrollmentDisabled, null);
        }
        var issued = EnrollmentToken.Issue(tenantId, createdBy, timeProvider.GetUtcNow(), effective);
        await store.AddTokenAsync(issued.Token, cancellationToken).ConfigureAwait(false);
        return new CreateEnrollmentTokenResult(CreateEnrollmentTokenStatus.Created, issued);
    }

    public Task<IReadOnlyList<EnrollmentToken>> ListActiveTokensAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        return store.ListActiveTokensAsync(tenantId, timeProvider.GetUtcNow(), cancellationToken);
    }

    /// <summary>Null when the token is unknown to the tenant or can no longer be revoked.</summary>
    public async Task<EnrollmentTokenRevoked?> RevokeTokenAsync(
        TenantId tenantId, Guid tokenId, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(actor);
        var now = timeProvider.GetUtcNow();
        return await store.RevokeTokenAsync(tenantId, tokenId, actor, now, cancellationToken).ConfigureAwait(false)
            ? new EnrollmentTokenRevoked(tenantId, tokenId, actor, now)
            : null;
    }
}
