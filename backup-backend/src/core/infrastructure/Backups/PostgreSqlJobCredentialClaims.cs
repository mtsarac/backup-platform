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

    public async Task<IReadOnlyList<string>> TakeStaleUnreferencedAsync(DateTimeOffset olderThan, int limit, CancellationToken cancellationToken)
    {
        await using var context = Persistence.PlatformDbContext.CreateForPlatformOperations(options);
        var cutoff = olderThan.ToUniversalTime();
        // A job that references the credential makes the claim moot: the creation did finish (a loser of a race may claim after the winner committed).
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM job_credential_claims c WHERE c."ClaimedAt" < {cutoff}
              AND EXISTS (SELECT 1 FROM backup_jobs j WHERE j."Configuration"->>'DatabaseCredentialReference' = c."Reference")
            """, cancellationToken);
        return await context.Set<JobCredentialClaimRecord>().AsNoTracking().Where(claim => claim.ClaimedAt < cutoff)
            .OrderBy(claim => claim.ClaimedAt).Take(limit).Select(claim => claim.Reference).ToListAsync(cancellationToken);
    }
}
