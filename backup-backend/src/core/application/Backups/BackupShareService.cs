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

    public Task<IReadOnlyList<DownloadAttemptView>> ListTenantDownloadAttemptsAsync(
        TenantId tenantId,
        DownloadAttemptFilter filter,
        CancellationToken cancellationToken)
    {
        if (filter.TenantId is not null && filter.TenantId != tenantId)
        {
            return Task.FromResult<IReadOnlyList<DownloadAttemptView>>([]);
        }

        return store.ListDownloadAttemptsAsync(
            filter with { TenantId = tenantId },
            tenantScoped: true,
            cancellationToken);
    }

    public Task<IReadOnlyList<DownloadAttemptView>> ListPlatformDownloadAttemptsAsync(
        DownloadAttemptFilter filter,
        CancellationToken cancellationToken)
    {
        return store.ListDownloadAttemptsAsync(
            filter,
            tenantScoped: false,
            cancellationToken);
    }

    public Task<AuditRetentionSettingsView> GetAuditRetentionAsync(
        CancellationToken cancellationToken)
    {
        return store.GetAuditRetentionAsync(cancellationToken);
    }

    public async Task<SaveAuditRetentionResult> UpdateAuditRetentionAsync(
        UserId actor,
        int retentionDays,
        CancellationToken cancellationToken)
    {
        var profile = await userStore.GetUserAsync(actor, cancellationToken);
        if (profile is null || profile.Role != UserRole.PlatformAdmin)
        {
            return new SaveAuditRetentionResult(
                SaveAuditRetentionOutcome.Forbidden,
                ErrorCode: "forbidden",
                ErrorMessage: "Only a PlatformAdmin can change the audit retention setting.");
        }

        if (retentionDays is < PlatformAuditRetentionSettings.MinimumRetentionDays or > PlatformAuditRetentionSettings.MaximumRetentionDays)
        {
            return new SaveAuditRetentionResult(
                SaveAuditRetentionOutcome.InvalidRetentionDays,
                ErrorCode: "invalid_retention_days",
                ErrorMessage: $"Retention days must be between {PlatformAuditRetentionSettings.MinimumRetentionDays} and {PlatformAuditRetentionSettings.MaximumRetentionDays}.");
        }

        var now = timeProvider.GetUtcNow();
        var settings = await store.SaveAuditRetentionAsync(retentionDays, actor, now, cancellationToken);
        return new SaveAuditRetentionResult(SaveAuditRetentionOutcome.Success, settings);
    }

    public Task<AuditHistoryPruneResult> PruneHistoryAsync(
        CancellationToken cancellationToken,
        DateTimeOffset? nowOverride = null,
        int? retentionDaysOverride = null)
    {
        var now = nowOverride ?? timeProvider.GetUtcNow();
        return store.PruneHistoryAsync(now, cancellationToken, retentionDaysOverride);
    }
}

public sealed class AuditAndShareHistoryPruningService(
    IBackupShareStore store,
    TimeProvider timeProvider)
{
    public Task<AuditHistoryPruneResult> PruneAsync(
        CancellationToken cancellationToken,
        DateTimeOffset? nowOverride = null,
        int? retentionDaysOverride = null)
    {
        var now = nowOverride ?? timeProvider.GetUtcNow();
        return store.PruneHistoryAsync(now, cancellationToken, retentionDaysOverride);
    }
}
