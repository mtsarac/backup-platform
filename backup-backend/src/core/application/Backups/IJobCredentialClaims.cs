namespace BackupPlatform.Application.Backups;

/// <summary>
/// Ledger of database-login credentials minted for a job creation that has not (yet) produced a job. A creation that crashes after the credential was
/// written and before the job was saved leaves its claim behind; <see cref="TakeStaleUnreferencedAsync"/> finds those so the credential can be removed
/// if no retry ever arrives. A retry of the same request re-claims (refreshing the age) and completes with the same credential.
/// </summary>
public interface IJobCredentialClaims
{
    Task ClaimAsync(string reference, DateTimeOffset now, CancellationToken cancellationToken);

    Task ReleaseAsync(string reference, CancellationToken cancellationToken);

    /// <summary>
    /// Claims older than <paramref name="olderThan"/>: those some job already references are dropped (the credential is in use), the rest are returned and
    /// stay in the ledger until <see cref="ReleaseAsync"/> confirms their credential is gone.
    /// </summary>
    Task<IReadOnlyList<string>> TakeStaleUnreferencedAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken);
}
