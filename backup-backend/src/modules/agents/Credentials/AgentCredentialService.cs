using BackupPlatform.Application.Agents;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Agents.Credentials;

public enum AgentAuthenticationFailure
{
    None = 0,
    UnknownCredential = 1,
    InvalidSecret = 2,
    CredentialRevoked = 3,
    CredentialExpired = 4,
    PendingCredentialNotAllowed = 5,
    AgentNotActive = 6,
    StaleGeneration = 7,
    TenantMismatch = 8
}

public sealed record AgentAuthenticationResult(AgentIdentity? Identity, AgentAuthenticationFailure Failure)
{
    public bool Succeeded => Identity is not null;
}

public enum BeginRotationStatus
{
    Started = 1,
    NotAuthorized = 2,
    AgentNotActive = 3
}

/// <summary>Facts about a started rotation, shaped for later auditing; carries the one-time pending credential.</summary>
public sealed record CredentialRotationStarted(
    AgentId AgentId, TenantId TenantId, NewAgentCredential Pending, Guid? ReplacedPendingCredentialId, DateTimeOffset At);

public sealed record BeginRotationResult(BeginRotationStatus Status, CredentialRotationStarted? Started);

public enum ConfirmRotationStatus
{
    Confirmed = 1,
    AlreadyConfirmed = 2,
    PendingRotationExists = 3,
    NotAuthorized = 4,
    AgentNotActive = 5
}

public sealed record CredentialRotationConfirmed(AgentId AgentId, TenantId TenantId, int CredentialGeneration, Guid? SupersededCredentialId, DateTimeOffset At);

public sealed record ConfirmRotationResult(ConfirmRotationStatus Status, int CredentialGeneration, CredentialRotationConfirmed? Confirmed);

/// <summary>Agent credential authentication and rotation. Independent of user password authentication.</summary>
public sealed class AgentCredentialService(IAgentRegistryStore store, TimeProvider timeProvider, AgentSecurityOptions options)
{
    /// <summary>
    /// Validates the secret in constant time, then the credential and agent state. Pending credentials authenticate
    /// only when <paramref name="allowPending"/> is set, which is reserved for the rotation confirmation call.
    /// </summary>
    public async Task<AgentAuthenticationResult> AuthenticateAsync(
        Guid credentialId, string secret, bool allowPending, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var record = await store.FindCredentialAsync(credentialId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            AgentCredential.BurnVerification(secret);
            return Fail(AgentAuthenticationFailure.UnknownCredential);
        }
        var credential = record.Credential;
        if (!credential.VerifySecret(secret))
        {
            return Fail(AgentAuthenticationFailure.InvalidSecret);
        }
        if (record.AgentStatus != AgentStatus.Active)
        {
            return Fail(AgentAuthenticationFailure.AgentNotActive);
        }
        if (credential.TenantId != record.AgentTenantId)
        {
            return Fail(AgentAuthenticationFailure.TenantMismatch);
        }
        switch (credential.Status)
        {
            case AgentCredentialStatus.Revoked:
                return Fail(AgentAuthenticationFailure.CredentialRevoked);
            case AgentCredentialStatus.Pending:
                if (!allowPending)
                {
                    return Fail(AgentAuthenticationFailure.PendingCredentialNotAllowed);
                }
                if (credential.IsExpired(timeProvider.GetUtcNow()))
                {
                    return Fail(AgentAuthenticationFailure.CredentialExpired);
                }
                if (credential.Generation != record.AgentCredentialGeneration + 1)
                {
                    return Fail(AgentAuthenticationFailure.StaleGeneration);
                }
                break;
            case AgentCredentialStatus.Active:
                if (credential.Generation != record.AgentCredentialGeneration)
                {
                    return Fail(AgentAuthenticationFailure.StaleGeneration);
                }
                break;
            default:
                return Fail(AgentAuthenticationFailure.CredentialRevoked);
        }
        return new AgentAuthenticationResult(
            new AgentIdentity(credential.AgentId, credential.TenantId, credential.Id, credential.Generation), AgentAuthenticationFailure.None);
    }

    public async Task<BeginRotationResult> BeginRotationAsync(AgentIdentity caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var now = timeProvider.GetUtcNow();
        var commit = await store.BeginRotationAsync(caller, now, options.PendingCredentialLifetime, cancellationToken).ConfigureAwait(false);
        return commit.Outcome switch
        {
            RotationBeginOutcome.Ready => new BeginRotationResult(BeginRotationStatus.Started,
                new CredentialRotationStarted(caller.AgentId, caller.TenantId, commit.Issued!, commit.ReplacedPendingCredentialId, now)),
            RotationBeginOutcome.AgentNotActive => new BeginRotationResult(BeginRotationStatus.AgentNotActive, null),
            _ => new BeginRotationResult(BeginRotationStatus.NotAuthorized, null)
        };
    }

    public async Task<ConfirmRotationResult> ConfirmRotationAsync(AgentIdentity caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var now = timeProvider.GetUtcNow();
        var commit = await store.ConfirmRotationAsync(caller, now, cancellationToken).ConfigureAwait(false);
        return commit.Outcome switch
        {
            RotationConfirmOutcome.Activate => new ConfirmRotationResult(ConfirmRotationStatus.Confirmed, commit.CredentialGeneration,
                new CredentialRotationConfirmed(caller.AgentId, caller.TenantId, commit.CredentialGeneration, commit.SupersededCredentialId, now)),
            RotationConfirmOutcome.AlreadyConfirmed => new ConfirmRotationResult(ConfirmRotationStatus.AlreadyConfirmed, commit.CredentialGeneration, null),
            RotationConfirmOutcome.PendingRotationExists => new ConfirmRotationResult(ConfirmRotationStatus.PendingRotationExists, 0, null),
            RotationConfirmOutcome.AgentNotActive => new ConfirmRotationResult(ConfirmRotationStatus.AgentNotActive, 0, null),
            _ => new ConfirmRotationResult(ConfirmRotationStatus.NotAuthorized, 0, null)
        };
    }

    private static AgentAuthenticationResult Fail(AgentAuthenticationFailure failure) => new(null, failure);
}
