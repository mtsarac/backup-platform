using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.UnitTests.Backup;

public sealed class UploadBandwidthDomainTests
{
    [Theory]
    [InlineData(UploadBandwidth.MinimumBytesPerSecond - 1, false)]
    [InlineData(UploadBandwidth.MinimumBytesPerSecond, true)]
    [InlineData(UploadBandwidth.MaximumBytesPerSecond, true)]
    [InlineData(UploadBandwidth.MaximumBytesPerSecond + 1, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void Limits_are_bounded_on_both_sides(long bytesPerSecond, bool valid)
    {
        Assert.Equal(valid, UploadBandwidth.IsValid(bytesPerSecond));
        if (!valid) Assert.Throws<ArgumentOutOfRangeException>(() => UploadBandwidth.Validate(bytesPerSecond));
    }

    [Fact]
    public void A_tenant_limit_belongs_to_its_own_tenant_and_a_scope_needs_an_identity()
    {
        var tenant = TenantId.New();
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => new UploadBandwidthLimit(tenant, UploadBandwidthScope.Tenant, Guid.NewGuid(), 1_000_000, now));
        Assert.Throws<ArgumentException>(() => new UploadBandwidthLimit(tenant, UploadBandwidthScope.Agent, Guid.Empty, 1_000_000, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UploadBandwidthLimit(tenant, UploadBandwidthScope.Job, Guid.NewGuid(), 1, now));
        Assert.Equal(1_000_000, new UploadBandwidthLimit(tenant, UploadBandwidthScope.Tenant, tenant.Value, 1_000_000, now).BytesPerSecond);
    }

    // 0 = wait: a lone upload gets the whole limit; the incumbent still holding the whole limit leaves nothing free for a newcomer;
    // a slice below the minimum is a wait, not a crawl.
    [Theory]
    [InlineData(1_000_000L, 0, 0L, 1_000_000L)]
    [InlineData(1_000_000L, 1, 500_000L, 500_000L)]
    [InlineData(1_000_000L, 3, 750_000L, 250_000L)]
    [InlineData(1_000_000L, 1, 1_000_000L, 0L)]
    [InlineData(16_000L, 31, 0L, 0L)]
    [InlineData(16_000L, 15, 0L, UploadBandwidth.MinimumShareBytesPerSecond)]
    public void Share_splits_the_limit_equally_and_waits_when_nothing_usable_is_free(long limit, int running, long taken, long expected)
    {
        Assert.Equal(expected, UploadBandwidth.Share(limit, running, taken));
    }

    // Replays the real protocol (every upload renews in turn) over arbitrary arrivals, departures and limits:
    // the granted slices may never add up to more than the tenant limit, at any step, and must settle on an equal split.
    [Theory]
    [InlineData(1, 16_000L)]
    [InlineData(2, 1_250_000L)]
    [InlineData(3, 7_000_001L)]
    [InlineData(4, 100_000_000_000L)]
    public void Granted_slices_never_exceed_the_tenant_limit_through_arrivals_and_departures(int seed, long limit)
    {
        var random = new Random(seed);
        var grants = new Dictionary<int, long>();
        var next = 0;
        for (var step = 0; step < 2_000; step++)
        {
            var roll = random.Next(10);
            if (roll < 3 && grants.Count < 5) grants[next++] = 0;
            else if (roll < 5 && grants.Count > 0) grants.Remove(grants.Keys.ElementAt(random.Next(grants.Count)));
            else if (grants.Count > 0)
            {
                var renewing = grants.Keys.ElementAt(random.Next(grants.Count));
                var others = grants.Where(pair => pair.Key != renewing).ToList();
                grants[renewing] = UploadBandwidth.Share(limit, others.Count, others.Sum(pair => pair.Value));
            }
            Assert.True(grants.Values.Sum() <= limit, $"step {step}: {string.Join(",", grants.Values)} exceeds {limit}");
        }

        for (var round = 0; round < 3; round++)
            foreach (var key in grants.Keys.ToList())
            {
                var others = grants.Where(pair => pair.Key != key).ToList();
                grants[key] = UploadBandwidth.Share(limit, others.Count, others.Sum(pair => pair.Value));
            }
        Assert.True(grants.Values.Sum() <= limit);
        if (grants.Count > 0) Assert.All(grants.Values, share => Assert.True(share == 0 || share >= limit / grants.Count - 1));
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData(5_000_000L, 2_000_000L, 9_000_000L, 2_000_000L)]
    [InlineData(5_000_000L, null, 300_000L, 300_000L)]
    [InlineData(5_000_000L, null, 0L, 0L)]
    public void The_lowest_ceiling_wins_and_a_slice_not_yet_granted_holds_the_transfer(long? job, long? agent, long? tenantShare, long? expected)
    {
        Assert.Equal(expected, new UploadPolicyResponse { JobBytesPerSecond = job, AgentBytesPerSecond = agent, TenantShareBytesPerSecond = tenantShare }.EffectiveBytesPerSecond);
    }

    [Fact]
    public void The_deadline_allowance_is_the_size_over_the_lowest_rate_and_is_capped()
    {
        Assert.Equal(TimeSpan.Zero, UploadPolicyResponse.DeadlineAllowance(1L << 40, null));
        Assert.Equal(TimeSpan.FromSeconds(100), UploadPolicyResponse.DeadlineAllowance(100_000_000, 1_000_000));
        Assert.Equal(UploadPolicyResponse.MaximumDeadlineAllowance, UploadPolicyResponse.DeadlineAllowance(1L << 40, 16_000));
    }
}
