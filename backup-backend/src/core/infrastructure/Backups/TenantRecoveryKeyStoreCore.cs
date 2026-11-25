using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>The state machine behind the tenant and platform-operations stores. It opens its contexts through <paramref name="open"/>: the tenant store passes the filtered context of the request's tenant, the platform store the unfiltered one.</summary>
internal sealed class TenantRecoveryKeyStoreCore(Func<TenantId?, PlatformDbContext> open, TimeProvider time)
{
    internal async Task<TenantRecoveryKeyInfo?> GetAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        await using var context = open(tenantId);
        var key = await context.TenantRecoveryKeys.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId, cancellationToken);
        return key is null ? null : new TenantRecoveryKeyInfo(key.Version, key.KeyId, key.PublicKeySpki, key.CreatedAt);
    }

    internal async Task<TenantRecoveryKeyInfo> SetAsync(TenantId tenantId, UserId actor, string publicKeyPem, CancellationToken cancellationToken)
    {
        var (spki, keyId) = RecoveryPublicKeyParser.Parse(publicKeyPem);
        await using var context = open(tenantId);
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
        context.RecordAudit(AuditEvent.Record(now, actor, AuditActorKind.UserAdmin, AuditActions.TenantRecoveryKeySet, tenantId, "tenant_recovery_key",
            key.Id.ToString("D"), new Dictionary<string, object?> { ["result"] = "succeeded", ["keyId"] = keyId, ["keyVersion"] = key.Version }));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TenantRecoveryKeyInfo(key.Version, key.KeyId, key.PublicKeySpki, key.CreatedAt);
    }

    internal async Task<bool> RemoveAsync(TenantId tenantId, UserId actor, CancellationToken cancellationToken)
    {
        await using var context = open(tenantId);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var lockName = $"trk:{tenantId.Value:N}";
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockName}, 0))", cancellationToken);
        var key = await context.TenantRecoveryKeys.SingleOrDefaultAsync(row => row.TenantId == tenantId, cancellationToken);
        if (key is null) return false;
        context.TenantRecoveryKeys.Remove(key);
        context.RecordAudit(AuditEvent.Record(time.GetUtcNow(), actor, AuditActorKind.UserAdmin, AuditActions.TenantRecoveryKeyRemoved, tenantId, "tenant_recovery_key",
            key.Id.ToString("D"), new Dictionary<string, object?> { ["result"] = "succeeded", ["keyId"] = key.KeyId, ["keyVersion"] = key.Version }));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
