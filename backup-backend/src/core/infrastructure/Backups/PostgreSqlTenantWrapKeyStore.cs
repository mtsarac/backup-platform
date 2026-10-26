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
/// Every change of the key set (first use, rotation, retirement) runs under a per-tenant advisory lock so two nodes never mint (and overwrite) the same version.
/// </summary>
public sealed class PostgreSqlTenantWrapKeyStore(DbContextOptions<PlatformDbContext> options, ISecretStore secrets, TimeProvider time) : ITenantWrapKeyStore
{
    private const int KeyBits = 4096;

    public async Task<TenantWrapPublicKey> EnsureActiveAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await LockTenantKeysAsync(context, tenantId, cancellationToken);
        var keys = await context.TenantWrapKeys.Where(key => key.TenantId == tenantId).ToListAsync(cancellationToken);
        var active = keys.Where(key => key.State == TenantWrapKeyState.Active).MaxBy(key => key.Version);
        if (active is not null) return new(active.Version, active.KeyId, active.PublicKeySpki);

        var now = time.GetUtcNow();
        var created = await MintAsync(context, tenantId, keys, now, cancellationToken);
        context.AuditEvents.Add(AuditEvent.Record(now, UserId.System, AuditActorKind.System, AuditActions.TenantWrapKeyCreated, tenantId, "tenant_wrap_key",
            created.Id.ToString("D"), new Dictionary<string, object?> { ["result"] = "succeeded", ["keyId"] = created.KeyId, ["keyVersion"] = created.Version }));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(created.Version, created.KeyId, created.PublicKeySpki);
    }

    public async Task<bool> OwnsKeyAsync(TenantId tenantId, string keyId, int version, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await context.TenantWrapKeys.AsNoTracking().AnyAsync(key => key.TenantId == tenantId && key.KeyId == keyId && key.Version == version &&
            key.State != TenantWrapKeyState.Retired, cancellationToken);
    }

    public async Task<byte[]> UnwrapDekAsync(TenantId tenantId, string targetType, Guid targetId, string keyId, byte[] wrappedDek, CancellationToken cancellationToken)
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
        context.AuditEvents.Add(AuditEvent.Record(time.GetUtcNow(), UserId.System, AuditActorKind.System, AuditActions.BackupArtifactKeyUnwrapped, tenantId, targetType,
            targetId.ToString("D"), new Dictionary<string, object?> { ["result"] = "succeeded", ["targetId"] = targetId.ToString("D"), ["keyId"] = key.KeyId }));
        await context.SaveChangesAsync(cancellationToken);
        return dek;
    }

    public async Task<IReadOnlyList<TenantWrapKeyView>> ListAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        return await ViewsAsync(context, tenantId, cancellationToken);
    }

    public async Task<TenantWrapKeyRotation> RotateAsync(TenantId tenantId, UserId actor, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await LockTenantKeysAsync(context, tenantId, cancellationToken);
        var keys = await context.TenantWrapKeys.Where(key => key.TenantId == tenantId).ToListAsync(cancellationToken);
        var previous = keys.SingleOrDefault(key => key.State == TenantWrapKeyState.Active);
        previous?.DemoteToDecryptOnly();
        // The demotion must reach the database before the new Active row: a partial unique index allows only one Active key per tenant.
        if (previous is not null) await context.SaveChangesAsync(cancellationToken);
        var now = time.GetUtcNow();
        var created = await MintAsync(context, tenantId, keys, now, cancellationToken);
        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.PlatformAdmin, AuditActions.TenantWrapKeyRotated, tenantId, "tenant_wrap_key",
            created.Id.ToString("D"), new Dictionary<string, object?>
            {
                ["result"] = "succeeded", ["keyId"] = created.KeyId, ["keyVersion"] = created.Version,
                ["previousKeyId"] = previous?.KeyId, ["previousKeyVersion"] = previous?.Version
            }));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var views = await ViewsAsync(context, tenantId, cancellationToken);
        return new TenantWrapKeyRotation(views.Single(view => view.Version == created.Version), previous is null ? null : views.Single(view => view.Version == previous.Version));
    }

    public async Task<TenantWrapKeyRetirement> RetireAsync(TenantId tenantId, int version, UserId actor, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await LockTenantKeysAsync(context, tenantId, cancellationToken);
        var key = await context.TenantWrapKeys.SingleOrDefaultAsync(candidate => candidate.TenantId == tenantId && candidate.Version == version, cancellationToken);
        if (key is null) return new(TenantWrapKeyRetireOutcome.NotFound);
        if (key.State == TenantWrapKeyState.Active) return new(TenantWrapKeyRetireOutcome.ActiveKey);
        if (key.State == TenantWrapKeyState.Retired)
        {
            // A retirement interrupted after the commit (before the secret was removed) is finished here; deleting an absent secret is not an error.
            await secrets.DeleteAsync(new SecretReference(key.SecretReference), cancellationToken);
            return new(TenantWrapKeyRetireOutcome.AlreadyRetired);
        }
        key.Retire();
        var affected = await context.BackupArtifacts.CountAsync(artifact => artifact.TenantId == tenantId && artifact.EncryptionKeyId == key.KeyId, cancellationToken);
        context.AuditEvents.Add(AuditEvent.Record(time.GetUtcNow(), actor, AuditActorKind.PlatformAdmin, AuditActions.TenantWrapKeyRetired, tenantId, "tenant_wrap_key",
            key.Id.ToString("D"), new Dictionary<string, object?> { ["result"] = "succeeded", ["keyId"] = key.KeyId, ["keyVersion"] = key.Version, ["artifactsAffected"] = affected }));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        // Retired is committed first, so a failure here leaves a key that already opens nothing; the private half is destroyed so it cannot be revived.
        await secrets.DeleteAsync(new SecretReference(key.SecretReference), cancellationToken);
        return new(TenantWrapKeyRetireOutcome.Retired, (await ViewsAsync(context, tenantId, cancellationToken)).Single(view => view.Version == version));
    }

    private static Task LockTenantKeysAsync(PlatformDbContext context, TenantId tenantId, CancellationToken cancellationToken)
    {
        var lockName = $"twk:{tenantId.Value:N}";
        return context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockName}, 0))", cancellationToken);
    }

    /// <summary>Generates the next version's key pair, stores the private half and adds the Active row; the caller commits.</summary>
    private async Task<TenantWrapKey> MintAsync(PlatformDbContext context, TenantId tenantId, IReadOnlyCollection<TenantWrapKey> existing, DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var rsa = RSA.Create(KeyBits);
        var spki = rsa.ExportSubjectPublicKeyInfo();
        var version = existing.Count == 0 ? 1 : existing.Max(key => key.Version) + 1;
        var reference = $"twk-{tenantId.Value:N}-{version}";
        var created = new TenantWrapKey(Guid.NewGuid(), tenantId, version, BpeKeyId.FromSpki(spki), spki, reference, now);
        await secrets.SetAsync(new SecretReference(reference), new SecretValue(rsa.ExportPkcs8PrivateKeyPem()), cancellationToken);
        context.TenantWrapKeys.Add(created);
        return created;
    }

    private static async Task<IReadOnlyList<TenantWrapKeyView>> ViewsAsync(PlatformDbContext context, TenantId tenantId, CancellationToken cancellationToken)
    {
        var keys = await context.TenantWrapKeys.AsNoTracking().Where(key => key.TenantId == tenantId).OrderByDescending(key => key.Version).ToListAsync(cancellationToken);
        var counts = await context.BackupArtifacts.AsNoTracking().Where(artifact => artifact.TenantId == tenantId && artifact.EncryptionKeyId != null)
            .GroupBy(artifact => artifact.EncryptionKeyId!).Select(group => new { KeyId = group.Key, Count = group.Count() }).ToDictionaryAsync(row => row.KeyId, row => row.Count, cancellationToken);
        return [.. keys.Select(key => new TenantWrapKeyView(key.Version, key.KeyId, key.State, key.CreatedAt, counts.GetValueOrDefault(key.KeyId)))];
    }
}
