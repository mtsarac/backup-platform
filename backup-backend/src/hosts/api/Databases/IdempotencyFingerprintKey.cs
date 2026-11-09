using System.Security.Cryptography;
using BackupPlatform.Application.Backups;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.SecretProviders.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Api.Databases;

/// <summary>
/// The HMAC key of job-creation fingerprints: 32 random bytes, created once and kept as a platform secret, so every API node and every restart uses the
/// same one. The first creation is serialised across nodes by a PostgreSQL advisory lock (the secret store itself only knows "replace").
/// </summary>
public sealed class IdempotencyFingerprintKey(ISecretStore secrets, DbContextOptions<PlatformDbContext> database) : IIdempotencyFingerprintKey
{
    public const string SecretName = "platform-job-idempotency-key-v1";
    private const long LockKey = 0x4250_4944_454D_5001; // arbitrary, unique to this lock
    private readonly SemaphoreSlim gate = new(1, 1);
    private byte[]? cached;

    public async ValueTask<byte[]> GetAsync(CancellationToken cancellationToken)
    {
        if (cached is { } known) return known;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return cached ??= await LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<byte[]> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        var reference = new SecretReference(SecretName);
        await using var context = PlatformDbContext.CreateForPlatformOperations(database);
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock({LockKey})", cancellationToken).ConfigureAwait(false);
        try
        {
            try { return Convert.FromHexString((await secrets.ResolveAsync(reference, cancellationToken).ConfigureAwait(false)).Reveal()); }
            catch (SecretUnavailableException) { }
            var created = RandomNumberGenerator.GetBytes(32);
            await secrets.SetAsync(reference, new SecretValue(Convert.ToHexString(created)), cancellationToken).ConfigureAwait(false);
            return created;
        }
        finally
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None).ConfigureAwait(false);
        }
    }
}
