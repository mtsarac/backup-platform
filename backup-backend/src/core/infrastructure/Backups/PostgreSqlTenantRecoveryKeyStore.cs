using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>A tenant's optional recovery recipient: the public key only, one row per tenant, every change audited with the key id (never the key) in the same transaction.</summary>
public sealed class PostgreSqlTenantRecoveryKeyStore(DbContextOptions<PlatformDbContext> options, TimeProvider time) : ITenantRecoveryKeyStore
{
    public async Task<TenantRecoveryKeyInfo?> GetAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var key = await context.TenantRecoveryKeys.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId, cancellationToken);
        return key is null ? null : new TenantRecoveryKeyInfo(key.Version, key.KeyId, key.PublicKeySpki, key.CreatedAt);
    }

    public async Task<TenantRecoveryKeyInfo> SetAsync(TenantId tenantId, UserId actor, string publicKeyPem, CancellationToken cancellationToken)
    {
        var (spki, keyId) = RecoveryPublicKeyParser.Parse(publicKeyPem);
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        // One writer per tenant: the unique TenantId index would otherwise turn two first-time sets into an error instead of an ordered replace.
        var lockName = $"trk:{tenantId.Value:N}";
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockName}, 0))", cancellationToken);
        var now = time.GetUtcNow();
        var key = await context.TenantRecoveryKeys.SingleOrDefaultAsync(row => row.TenantId == tenantId, cancellationToken);
        if (key is null)
        {
            key = new TenantRecoveryKey(Guid.NewGuid(), tenantId, 1, keyId, spki, actor, now);
            context.TenantRecoveryKeys.Add(key);
        }
        else key.Replace(keyId, spki, actor, now);
        context.AuditEvents.Add(AuditEvent.Record(now, actor, AuditActorKind.TenantAdministrator, AuditActions.TenantRecoveryKeySet, tenantId, "tenant_recovery_key",
            key.Id.ToString("D"), new Dictionary<string, object?> { ["result"] = "succeeded", ["keyId"] = keyId, ["keyVersion"] = key.Version }));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TenantRecoveryKeyInfo(key.Version, key.KeyId, key.PublicKeySpki, key.CreatedAt);
    }

    public async Task<bool> RemoveAsync(TenantId tenantId, UserId actor, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var lockName = $"trk:{tenantId.Value:N}";
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockName}, 0))", cancellationToken);
        var key = await context.TenantRecoveryKeys.SingleOrDefaultAsync(row => row.TenantId == tenantId, cancellationToken);
        if (key is null) return false;
        context.TenantRecoveryKeys.Remove(key);
        context.AuditEvents.Add(AuditEvent.Record(time.GetUtcNow(), actor, AuditActorKind.TenantAdministrator, AuditActions.TenantRecoveryKeyRemoved, tenantId, "tenant_recovery_key",
            key.Id.ToString("D"), new Dictionary<string, object?> { ["result"] = "succeeded", ["keyId"] = key.KeyId, ["keyVersion"] = key.Version }));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
