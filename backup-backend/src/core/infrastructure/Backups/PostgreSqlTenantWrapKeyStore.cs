using System.Security.Cryptography;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Encryption;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.SecretProviders.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>
/// Tenant wrap keys: metadata and the public key in <c>tenant_wrap_keys</c>, the private key (PKCS#8 PEM) only in the secret store.
/// A key is created on first use, under a per-tenant advisory lock so two nodes never mint (and overwrite) the same version.
/// </summary>
public sealed class PostgreSqlTenantWrapKeyStore(DbContextOptions<PlatformDbContext> options, ISecretStore secrets, TimeProvider time) : ITenantWrapKeyStore
{
    private const int KeyBits = 4096;

    public async Task<TenantWrapPublicKey> EnsureActiveAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var lockName = $"twk:{tenantId.Value:N}";
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockName}, 0))", cancellationToken);
        var keys = await context.TenantWrapKeys.Where(key => key.TenantId == tenantId).ToListAsync(cancellationToken);
        var active = keys.Where(key => key.State == TenantWrapKeyState.Active).MaxBy(key => key.Version);
        if (active is not null) return new(active.Version, active.KeyId, active.PublicKeySpki);

        using var rsa = RSA.Create(KeyBits);
        var spki = rsa.ExportSubjectPublicKeyInfo();
        var version = keys.Count == 0 ? 1 : keys.Max(key => key.Version) + 1;
        var reference = $"twk-{tenantId.Value:N}-{version}";
        var now = time.GetUtcNow();
        var created = new TenantWrapKey(Guid.NewGuid(), tenantId, version, BpeKeyId.FromSpki(spki), spki, reference, now);
        await secrets.SetAsync(new SecretReference(reference), new SecretValue(rsa.ExportPkcs8PrivateKeyPem()), cancellationToken);
        context.TenantWrapKeys.Add(created);
        context.AuditEvents.Add(AuditEvent.Record(now, UserId.System, AuditActorKind.System, AuditActions.TenantWrapKeyCreated, tenantId, "tenant_wrap_key",
            created.Id.ToString("D"), new Dictionary<string, object?> { ["result"] = "succeeded", ["keyId"] = created.KeyId, ["keyVersion"] = version }));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(version, created.KeyId, spki);
    }

    public async Task<bool> OwnsKeyAsync(TenantId tenantId, string keyId, int version, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.TenantWrapKeys.AsNoTracking().AnyAsync(key => key.TenantId == tenantId && key.KeyId == keyId && key.Version == version, cancellationToken);
    }

    public async Task<byte[]> UnwrapDekAsync(TenantId tenantId, Guid restoreAttemptId, string keyId, byte[] wrappedDek, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        // The tenant is part of the lookup: another tenant's key id is simply not found.
        var key = await context.TenantWrapKeys.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.TenantId == tenantId && candidate.KeyId == keyId, cancellationToken);
        if (key is not { CanDecrypt: true }) throw new TenantWrapKeyUnavailableException();
        byte[] dek;
        try
        {
            var pem = (await secrets.ResolveAsync(new SecretReference(key.SecretReference), cancellationToken)).Reveal();
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            dek = rsa.Decrypt(wrappedDek, RSAEncryptionPadding.OaepSHA256);
        }
        catch (Exception exception) when (exception is CryptographicException or SecretUnavailableException or ArgumentException)
        {
            throw new TenantWrapKeyUnavailableException();
        }
        context.AuditEvents.Add(AuditEvent.Record(time.GetUtcNow(), UserId.System, AuditActorKind.System, AuditActions.BackupArtifactKeyUnwrapped, tenantId, "restore_attempt",
            restoreAttemptId.ToString("D"), new Dictionary<string, object?> { ["result"] = "succeeded", ["attemptId"] = restoreAttemptId.ToString("D"), ["keyId"] = key.KeyId }));
        await context.SaveChangesAsync(cancellationToken);
        return dek;
    }
}
