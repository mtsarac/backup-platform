namespace BackupPlatform.Agent.Core.Restore;

/// <summary>
/// Asks the backend for the data key of the restore attempt's own encrypted artifact. The request carries no key material: the backend resolves
/// the artifact from the fenced attempt and opens only the wrapped key it stored, so an agent can never use it to decrypt anything else.
/// </summary>
public interface IArtifactKeyClient
{
    /// <summary>The 32-byte DEK, for this restore only; the caller zeroes it and never logs or persists it.</summary>
    /// <exception cref="ArtifactKeyUnavailableException">The backend refused, could not be reached, or answered with an unusable key.</exception>
    Task<byte[]> UnwrapAsync(Guid attemptId, Guid leaseToken, Guid restoreOperationId, CancellationToken cancellationToken);
}

/// <summary>The key service did not hand out a key. Carries no detail on purpose: a refusal and an outage both end the restore without decrypting.</summary>
public sealed class ArtifactKeyUnavailableException(string message) : Exception(message);
