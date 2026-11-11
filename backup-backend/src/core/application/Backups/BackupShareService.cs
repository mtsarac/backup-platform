using BackupPlatform.Application.Identity;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Application.Backups;

public sealed class BackupShareService(
    IBackupShareStore store,
    IUserAccessStore userStore,
    TimeProvider timeProvider)
{
    public async Task<BackupShareCreateResult> CreateShareAsync(
        TenantId tenantId,
        Guid artifactId,
        UserId actor,
        TimeSpan duration,
        string? reason,
        CancellationToken cancellationToken)
    {
        var profile = await userStore.GetUserAsync(actor, cancellationToken);
        if (profile is null || profile.Role != UserRole.UserAdmin)
        {
            return new BackupShareCreateResult(
                BackupShareCreateOutcome.Forbidden,
                ErrorCode: "forbidden",
                ErrorMessage: "Only a UserAdmin of the tenant can create a share.");
        }

        var now = timeProvider.GetUtcNow();
        return await store.CreateAsync(
            tenantId,
            artifactId,
            actor,
            profile.Role,
            duration,
            reason,
            now,
            cancellationToken);
    }

    public async Task<BackupShareView?> GetShareAsync(
        TenantId tenantId,
        Guid shareId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await store.GetAsync(tenantId, shareId, now, cancellationToken);
    }

    public async Task<IReadOnlyList<BackupShareView>> ListSharesAsync(
        TenantId tenantId,
        Guid? artifactId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await store.ListAsync(tenantId, artifactId, now, cancellationToken);
    }

    public async Task<BackupShareCreateResult> CreatePlatformShareAsync(
        TenantId tenantId,
        Guid artifactId,
        UserId actor,
        TimeSpan duration,
        string? reason,
        CancellationToken cancellationToken)
    {
        var profile = await userStore.GetUserAsync(actor, cancellationToken);
        if (profile is null || profile.Role != UserRole.PlatformAdmin)
        {
            return new BackupShareCreateResult(
                BackupShareCreateOutcome.Forbidden,
                ErrorCode: "forbidden",
                ErrorMessage: "Only a PlatformAdmin can create a platform share.");
        }

        var now = timeProvider.GetUtcNow();
        return await store.CreateAsync(
            tenantId,
            artifactId,
            actor,
            UserRole.PlatformAdmin,
            duration,
            reason,
            now,
            cancellationToken);
    }

    public async Task<IReadOnlyList<BackupShareView>> ListPlatformSharesAsync(
        TenantId? tenantId,
        BackupShareStatus? status,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await store.ListPlatformAsync(tenantId, status, now, cancellationToken);
    }

    public async Task<BackupShareView?> GetPlatformShareAsync(
        Guid shareId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await store.GetPlatformAsync(shareId, now, cancellationToken);
    }
}
