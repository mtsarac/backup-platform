using BackupPlatform.Application.Backups;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

internal sealed class JobCredentialClaimRecord
{
    public string Reference { get; set; } = null!;
    public DateTimeOffset ClaimedAt { get; set; }
}

public sealed class PostgreSqlJobCredentialClaims(DbContextOptions<Persistence.PlatformDbContext> options) : IJobCredentialClaims
{
    public async Task ClaimAsync(string reference, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = Persistence.PlatformDbContext.CreateForPlatformOperations(options);
        var at = now.ToUniversalTime();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO job_credential_claims ("Reference", "ClaimedAt") VALUES ({reference}, {at})
            ON CONFLICT ("Reference") DO UPDATE SET "ClaimedAt" = EXCLUDED."ClaimedAt"
            """, cancellationToken);
    }

    public async Task ReleaseAsync(string reference, CancellationToken cancellationToken)
    {
        await using var context = Persistence.PlatformDbContext.CreateForPlatformOperations(options);
        await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM job_credential_claims WHERE \"Reference\" = {reference}", cancellationToken);
    }

    public async Task<int> SweepStaleAsync(DateTimeOffset olderThan, int limit, Func<string, CancellationToken, Task> deleteCredential, CancellationToken cancellationToken)
    {
        await using var context = Persistence.PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var cutoff = olderThan.ToUniversalTime();
        // SKIP LOCKED: another node sweeping the same row, or a retry refreshing it right now, is simply left alone this round.
        var stale = await context.Database.SqlQuery<string>($"""
            SELECT "Reference" AS "Value" FROM job_credential_claims WHERE "ClaimedAt" < {cutoff} ORDER BY "ClaimedAt" LIMIT {limit} FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);
        var removed = 0;
        foreach (var reference in stale)
        {
            // A job that references the credential makes the claim moot: the creation did finish.
            var referenced = await context.Database.SqlQuery<bool>($"""
                SELECT EXISTS (SELECT 1 FROM backup_jobs WHERE "Configuration"->>'DatabaseCredentialReference' = {reference}) AS "Value"
                """).SingleAsync(cancellationToken);
            if (!referenced)
            {
                await deleteCredential(reference, cancellationToken);
                removed++;
            }
            await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM job_credential_claims WHERE \"Reference\" = {reference}", cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }
}
