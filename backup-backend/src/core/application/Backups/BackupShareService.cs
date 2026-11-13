using BackupPlatform.Domain.Audit;
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

    public async Task<BackupShareModifyResult> ExtendShareAsync(
        TenantId tenantId,
        Guid shareId,
        UserId actor,
        ExtendBackupShareRequest request,
        CancellationToken cancellationToken)
    {
        var profile = await userStore.GetUserAsync(actor, cancellationToken);
        if (profile is null)
        {
            return new BackupShareModifyResult(
                BackupShareModifyOutcome.Forbidden,
                ErrorCode: "forbidden",
                ErrorMessage: "User profile not found.");
        }

        if (profile.Role != UserRole.PlatformAdmin)
        {
            var membership = await userStore.GetMembershipAsync(actor, tenantId, cancellationToken);
            if (membership is null)
            {
                return new BackupShareModifyResult(
                    BackupShareModifyOutcome.Forbidden,
                    ErrorCode: "forbidden",
                    ErrorMessage: "User is not a member of this tenant.");
            }
        }

        var now = timeProvider.GetUtcNow();
        return await store.ExtendAsync(
            tenantId,
            shareId,
            actor,
            profile.Role,
            request,
            now,
            cancellationToken);
    }

    public async Task<BackupShareModifyResult> RevokeShareAsync(
        TenantId tenantId,
        Guid shareId,
        UserId actor,
        RevokeBackupShareRequest request,
        CancellationToken cancellationToken)
    {
        var profile = await userStore.GetUserAsync(actor, cancellationToken);
        if (profile is null)
        {
            return new BackupShareModifyResult(
                BackupShareModifyOutcome.Forbidden,
                ErrorCode: "forbidden",
                ErrorMessage: "User profile not found.");
        }

        if (profile.Role != UserRole.PlatformAdmin)
        {
            var membership = await userStore.GetMembershipAsync(actor, tenantId, cancellationToken);
            if (membership is null)
            {
                return new BackupShareModifyResult(
                    BackupShareModifyOutcome.Forbidden,
                    ErrorCode: "forbidden",
                    ErrorMessage: "User is not a member of this tenant.");
            }
        }

        var now = timeProvider.GetUtcNow();
        return await store.RevokeAsync(
            tenantId,
            shareId,
            actor,
            profile.Role,
            request.Reason,
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

    public Task<DownloadAttemptPage> ListTenantDownloadAttemptsAsync(
        TenantId tenantId, DownloadAttemptFilter filter, DownloadAttemptCursor? after, int limit, CancellationToken cancellationToken) =>
        store.ListDownloadAttemptsAsync(filter with { TenantId = tenantId }, tenantScoped: true, after, limit, cancellationToken);

    public Task<DownloadAttemptPage> ListPlatformDownloadAttemptsAsync(
        DownloadAttemptFilter filter, DownloadAttemptCursor? after, int limit, CancellationToken cancellationToken) =>
        store.ListDownloadAttemptsAsync(filter, tenantScoped: false, after, limit, cancellationToken);

    public Task<AuditRetentionSettingsView> GetAuditRetentionAsync(
        CancellationToken cancellationToken)
    {
        return store.GetAuditRetentionAsync(cancellationToken);
    }

    /// <summary>Null when the value is outside the allowed range. The caller is a PlatformAdmin (endpoint policy).</summary>
    public async Task<AuditRetentionSettingsView?> UpdateAuditRetentionAsync(UserId actor, int retentionDays, CancellationToken cancellationToken)
    {
        if (retentionDays is < PlatformAuditRetentionSettings.MinimumRetentionDays or > PlatformAuditRetentionSettings.MaximumRetentionDays) return null;
        return await store.SaveAuditRetentionAsync(retentionDays, actor, timeProvider.GetUtcNow(), cancellationToken);
    }
}
