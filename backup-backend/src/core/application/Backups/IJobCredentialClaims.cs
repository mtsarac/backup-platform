namespace BackupPlatform.Application.Backups;

/// <summary>
/// Ledger of database-login credentials minted for a job creation that has not (yet) produced a job. A creation that crashes after the credential was
/// written and before the job was saved leaves its claim behind; <see cref="SweepStaleAsync"/> finds those so the credential can be removed
/// if no retry ever arrives. A retry of the same request re-claims (refreshing the age) and completes with the same credential.
/// </summary>
public interface IJobCredentialClaims
{
    Task ClaimAsync(string reference, DateTimeOffset now, CancellationToken cancellationToken);

    Task ReleaseAsync(string reference, CancellationToken cancellationToken);

    /// <summary>
    /// Removes the credentials of claims older than <paramref name="olderThan"/> that no job references, at most <paramref name="limit"/> per call, and returns how many.
    /// Each claim row stays locked (FOR UPDATE SKIP LOCKED) from selection until its credential is deleted and the claim row removed, so a concurrent
    /// <see cref="ClaimAsync"/> (a retry on any node) waits for the removal and then claims and rewrites the credential afresh, and a claim refreshed before the
    /// selection is not stale at all. A claim whose credential some job references is dropped without touching the credential.
    /// <paramref name="deleteCredential"/> runs while the row is locked and must not touch the claim table.
    /// </summary>
    Task<int> SweepStaleAsync(DateTimeOffset olderThan, int limit, Func<string, CancellationToken, Task> deleteCredential, CancellationToken cancellationToken);
}
