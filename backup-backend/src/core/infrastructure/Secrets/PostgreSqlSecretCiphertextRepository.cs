using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.SecretProviders.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Secrets;

/// <summary>One stored ciphertext. The platform never writes plaintext here: the secret store encrypts before it calls the repository.</summary>
internal sealed class SecretCiphertextRecord
{
    public SecretCiphertextRecord()
    {
        Name = null!;
        Ciphertext = null!;
    }

    public string Name { get; set; }
    public string Ciphertext { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>PostgreSQL persistence of secret ciphertext. Every call is its own short statement; there is no tenant scope because names are unguessable references and never enumerable.</summary>
public sealed class PostgreSqlSecretCiphertextRepository(DbContextOptions<PlatformDbContext> options, TimeProvider time) : ISecretCiphertextRepository
{
    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.Set<SecretCiphertextRecord>().AsNoTracking().Where(record => record.Name == name).Select(record => record.Ciphertext).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task SetAsync(string name, string ciphertext, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(ciphertext);
        var now = time.GetUtcNow().ToUniversalTime();
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO platform_secrets ("Name", "Ciphertext", "CreatedAt", "UpdatedAt") VALUES ({name}, {ciphertext}, {now}, {now})
            ON CONFLICT ("Name") DO UPDATE SET "Ciphertext" = EXCLUDED."Ciphertext", "UpdatedAt" = EXCLUDED."UpdatedAt"
            """, cancellationToken);
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM platform_secrets WHERE \"Name\" = {name}", cancellationToken) > 0;
    }
}
