using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Backups;

/// <summary>Window within a profile for API views and requests.</summary>
public sealed record TransferScheduleWindowDto(DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime, long BytesPerSecond);

/// <summary>Bandwidth profile view as returned by the API.</summary>
public sealed record BandwidthProfileView(Guid Id, string Name, string TimeZoneId, IReadOnlyList<TransferScheduleWindowDto> Windows);

/// <summary>The configured limits of a tenant: static limits and profile assignments.</summary>
public sealed record UploadBandwidthSettings(long? Tenant, IReadOnlyDictionary<Guid, long> Agents, IReadOnlyDictionary<Guid, long> Jobs,
    Guid? TenantProfileId = null, IReadOnlyDictionary<Guid, Guid>? AgentProfiles = null, IReadOnlyDictionary<Guid, Guid>? JobProfiles = null);

/// <summary>Every ceiling that applies to one attempt's upload, as stored. <see cref="TenantShare"/> is only set for a granted slice.</summary>
public sealed record AttemptUploadLimits(long? Job, long? Agent, long? Tenant, long? TenantShare = null, TimeSpan? NextTransition = null)
{
    public bool PauseRequested => Job == 0 || Agent == 0 || Tenant == 0;
}

/// <summary>What a call about a running attempt's upload does to its slice of the tenant limit.</summary>
public enum UploadSliceAction
{
    /// <summary>Takes or renews the slice (when a tenant limit exists).</summary>
    Renew,

    /// <summary>Gives the slice back.</summary>
    Release,

    /// <summary>Only reads the ceilings; no slice is taken.</summary>
    Observe
}

public interface IUploadBandwidthStore
{
    Task<UploadBandwidthSettings> GetAsync(TenantId tenantId, CancellationToken cancellationToken);

    /// <summary>Sets (or, with null, removes) one limit and audits the change in the same transaction. False when the agent or job is not the tenant's.</summary>
    Task<bool> SetAsync(TenantId tenantId, UploadBandwidthScope scope, Guid scopeId, long? bytesPerSecond, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Applies <paramref name="action"/> to this running attempt's slice of the tenant limit and returns the ceilings. A slice is decided in the database, serialized
    /// per tenant, so any number of backend nodes agree; without a tenant limit nothing is locked. Null when the lease does not hold a running attempt of this agent.
    /// </summary>
    Task<AttemptUploadLimits?> GrantAsync(BackupLeaseCommand lease, UploadSliceAction action, TimeSpan grantTtl, CancellationToken cancellationToken);

    Task<IReadOnlyList<BandwidthProfileView>> ListProfilesAsync(TenantId tenantId, CancellationToken cancellationToken);
    Task<BandwidthProfileView?> GetProfileAsync(TenantId tenantId, Guid profileId, CancellationToken cancellationToken);
    Task<BandwidthProfileView> SaveProfileAsync(TenantId tenantId, Guid? profileId, string name, string timeZoneId, IEnumerable<TransferScheduleWindowDto> windows, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> DeleteProfileAsync(TenantId tenantId, Guid profileId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> AssignProfileAsync(TenantId tenantId, UploadBandwidthScope scope, Guid scopeId, Guid? profileId, UserId actor, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// Upload bandwidth policy. The limits are independent ceilings (tenant total, agent, job); the agent applies the most restrictive one to the bytes it
/// uploads, wherever they go. Management is validated here; the share of the tenant ceiling that one upload may use is the store's, decided in the database.
/// </summary>
public sealed class UploadBandwidthService(IUploadBandwidthStore store, BackupExecutionOptions executionLimits, TimeProvider time)
{
    /// <summary>How often a transfer under a tenant limit renews its slice; a limit change reaches it within this time.</summary>
    public static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(3);

    /// <summary>A transfer without a tenant slice only re-reads its static limits this often.</summary>
    public static readonly TimeSpan IdleRenewalInterval = TimeSpan.FromSeconds(15);

    /// <summary>How long a slice survives without renewal (three missed renewals), then it is given to the other uploads.</summary>
    public static readonly TimeSpan GrantTtl = TimeSpan.FromSeconds(10);

    public Task<UploadBandwidthSettings> GetAsync(TenantId tenantId, CancellationToken cancellationToken) => store.GetAsync(tenantId, cancellationToken);

    public Task<IReadOnlyList<BandwidthProfileView>> ListProfilesAsync(TenantId tenantId, CancellationToken cancellationToken) =>
        store.ListProfilesAsync(tenantId, cancellationToken);

    public Task<BandwidthProfileView?> GetProfileAsync(TenantId tenantId, Guid profileId, CancellationToken cancellationToken) =>
        store.GetProfileAsync(tenantId, profileId, cancellationToken);

    public Task<BandwidthProfileView> SaveProfileAsync(TenantId tenantId, Guid? profileId, string name, string timeZoneId, IEnumerable<TransferScheduleWindowDto> windows, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(windows);
        return store.SaveProfileAsync(tenantId, profileId, name, timeZoneId, windows, actor, time.GetUtcNow(), cancellationToken);
    }

    public Task<bool> DeleteProfileAsync(TenantId tenantId, Guid profileId, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return store.DeleteProfileAsync(tenantId, profileId, actor, time.GetUtcNow(), cancellationToken);
    }

    public Task<bool> AssignProfileAsync(TenantId tenantId, UploadBandwidthScope scope, Guid scopeId, Guid? profileId, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (scopeId == Guid.Empty) return Task.FromResult(false);
        return store.AssignProfileAsync(tenantId, scope, scopeId, profileId, actor, time.GetUtcNow(), cancellationToken);
    }

    /// <exception cref="ArgumentOutOfRangeException">The limit is outside <see cref="UploadBandwidth"/>'s range.</exception>
    /// <returns>False when the agent or job is not the tenant's.</returns>
    public Task<bool> SetAsync(TenantId tenantId, UploadBandwidthScope scope, Guid scopeId, long? bytesPerSecond, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (bytesPerSecond is { } value) UploadBandwidth.Validate(value);
        if (scopeId == Guid.Empty) return Task.FromResult(false);
        return store.SetAsync(tenantId, scope, scopeId, bytesPerSecond, actor, time.GetUtcNow(), cancellationToken);
    }

    /// <summary>The answer to an agent's policy call; null when the lease is not a running attempt of this agent.</summary>
    public async Task<UploadPolicyResponse?> PolicyAsync(BackupLeaseCommand lease, UploadSliceAction action, CancellationToken cancellationToken)
    {
        var limits = await store.GrantAsync(lease, action, GrantTtl, cancellationToken);
        if (limits is null) return null;
        var limited = limits is { Job: not null } or { Agent: not null } or { Tenant: not null };
        var refreshSeconds = (int)(limits.Tenant is null ? IdleRenewalInterval : RenewalInterval).TotalSeconds;
        if (limits.NextTransition is { TotalSeconds: > 0 } transition && (int)transition.TotalSeconds < refreshSeconds)
            refreshSeconds = Math.Max(1, (int)transition.TotalSeconds);
        return new UploadPolicyResponse
        {
            JobBytesPerSecond = limits.Job,
            AgentBytesPerSecond = limits.Agent,
            TenantShareBytesPerSecond = limits.Tenant is null ? null : limits.TenantShare ?? 0,
            PauseRequested = limits.PauseRequested,
            MinimumBytesPerSecond = limited ? Floor(lease.TenantId, limits) : null,
            RefreshAfterSeconds = refreshSeconds,
            ValidForSeconds = (int)GrantTtl.TotalSeconds
        };
    }

    /// <summary>
    /// The lowest rate an upload can be held to while the limits stay as they are: a job or agent limit applies whole (an agent uploads one backup at
    /// a time), the tenant limit may be split among as many uploads as the tenant may run at once.
    /// </summary>
    private long? Floor(TenantId tenantId, AttemptUploadLimits limits)
    {
        long? floor = null;
        foreach (var candidate in new long?[]
        {
            limits.Job, limits.Agent,
            limits.Tenant is { } tenant ? Math.Max(UploadBandwidth.MinimumShareBytesPerSecond, tenant / executionLimits.TenantLimit(tenantId)) : null
        })
            if (candidate is { } value && (floor is null || value < floor)) floor = value;
        return floor;
    }
}
