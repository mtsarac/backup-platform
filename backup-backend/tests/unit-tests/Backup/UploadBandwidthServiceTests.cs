using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.UnitTests.Backup;

public sealed class UploadBandwidthServiceTests
{
    private readonly FakeStore _store = new();
    private readonly TenantId _tenant = TenantId.New();
    private readonly AgentId _agent = AgentId.New();

    private UploadBandwidthService Service(int tenantSlots = 5) => new(_store, new BackupExecutionOptions { TenantMaxConcurrentBackups = tenantSlots }, TimeProvider.System);

    private BackupLeaseCommand Lease() => new(_tenant, _agent, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);

    [Theory]
    [InlineData(UploadBandwidth.MinimumBytesPerSecond - 1)]
    [InlineData(UploadBandwidth.MaximumBytesPerSecond + 1)]
    public async Task A_limit_outside_the_range_is_refused_before_it_reaches_the_store(long bytesPerSecond)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Service().SetAsync(_tenant, UploadBandwidthScope.Agent, Guid.NewGuid(), bytesPerSecond, UserId.New(), default));

        Assert.Equal(0, _store.Sets);
    }

    [Fact]
    public async Task Unlimited_is_null_and_reaches_the_store_as_a_removal()
    {
        Assert.True(await Service().SetAsync(_tenant, UploadBandwidthScope.Job, Guid.NewGuid(), null, UserId.New(), default));

        Assert.Equal(1, _store.Sets);
        Assert.Null(_store.LastSet);
    }

    [Fact]
    public async Task A_policy_without_limits_is_unlimited_and_polled_rarely()
    {
        _store.Limits = new AttemptUploadLimits(null, null, null);

        var policy = await Service().PolicyAsync(Lease(), false, default);

        Assert.Null(policy!.EffectiveBytesPerSecond);
        Assert.Null(policy.MinimumBytesPerSecond);
        Assert.Null(policy.TenantShareBytesPerSecond);
        Assert.Equal((int)UploadBandwidthService.IdleRenewalInterval.TotalSeconds, policy.RefreshAfterSeconds);
    }

    [Fact]
    public async Task A_tenant_limit_without_a_granted_slice_is_a_wait_and_renews_quickly()
    {
        _store.Limits = new AttemptUploadLimits(null, null, 1_000_000, TenantShare: null);

        var policy = await Service().PolicyAsync(Lease(), false, default);

        Assert.Equal(0, policy!.TenantShareBytesPerSecond);
        Assert.Equal(0, policy.EffectiveBytesPerSecond);
        Assert.Equal((int)UploadBandwidthService.RenewalInterval.TotalSeconds, policy.RefreshAfterSeconds);
        Assert.Equal((int)UploadBandwidthService.GrantTtl.TotalSeconds, policy.ValidForSeconds);
    }

    [Fact]
    public async Task The_minimum_rate_is_the_lowest_the_upload_can_be_held_to()
    {
        // Job and agent limits apply whole; the tenant limit may be split across every upload the tenant may run at once (5 here).
        _store.Limits = new AttemptUploadLimits(Job: 900_000, Agent: 800_000, Tenant: 2_000_000, TenantShare: 1_000_000);
        Assert.Equal(400_000, (await Service(5).PolicyAsync(Lease(), false, default))!.MinimumBytesPerSecond);

        _store.Limits = new AttemptUploadLimits(Job: 300_000, Agent: null, Tenant: 2_000_000, TenantShare: 1_000_000);
        Assert.Equal(300_000, (await Service(5).PolicyAsync(Lease(), false, default))!.MinimumBytesPerSecond);

        _store.Limits = new AttemptUploadLimits(null, null, 16_000, 16_000);
        Assert.Equal(UploadBandwidth.MinimumShareBytesPerSecond, (await Service(100).PolicyAsync(Lease(), false, default))!.MinimumBytesPerSecond);
    }

    [Fact]
    public async Task A_lease_that_holds_no_running_attempt_gets_no_policy()
    {
        _store.Limits = null;

        Assert.Null(await Service().PolicyAsync(Lease(), false, default));
    }

    [Fact]
    public async Task The_relay_allowance_is_size_over_the_minimum_rate_and_zero_when_unlimited()
    {
        _store.Limits = new AttemptUploadLimits(null, null, null);
        Assert.Equal(TimeSpan.Zero, await Service().DeadlineAllowanceAsync(_tenant, _agent, Guid.NewGuid(), 1L << 40, default));

        _store.Limits = new AttemptUploadLimits(1_000_000, null, null);
        Assert.Equal(TimeSpan.FromSeconds(50), await Service().DeadlineAllowanceAsync(_tenant, _agent, Guid.NewGuid(), 50_000_000, default));

        _store.Limits = null;
        Assert.Equal(TimeSpan.Zero, await Service().DeadlineAllowanceAsync(_tenant, _agent, Guid.NewGuid(), 50_000_000, default));
    }

    private sealed class FakeStore : IUploadBandwidthStore
    {
        public AttemptUploadLimits? Limits { get; set; }
        public int Sets;
        public long? LastSet;

        public Task<UploadBandwidthSettings> GetAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            Task.FromResult(new UploadBandwidthSettings(null, new Dictionary<Guid, long>(), new Dictionary<Guid, long>()));

        public Task<bool> SetAsync(TenantId tenantId, UploadBandwidthScope scope, Guid scopeId, long? bytesPerSecond, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Sets++;
            LastSet = bytesPerSecond;
            return Task.FromResult(true);
        }

        public Task<AttemptUploadLimits?> GrantAsync(BackupLeaseCommand lease, bool release, TimeSpan grantTtl, CancellationToken cancellationToken) => Task.FromResult(Limits);

        public Task<AttemptUploadLimits?> ReadAsync(TenantId tenantId, AgentId agentId, Guid attemptId, CancellationToken cancellationToken) => Task.FromResult(Limits);
    }
}
