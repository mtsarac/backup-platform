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

    [Fact]
    public void A_lone_upload_gets_the_whole_limit_and_equal_uploads_get_equal_slices()
    {
        Assert.Equal(1_000_000, UploadBandwidth.Share(1_000_000, 0, 0));
        Assert.Equal(500_000, UploadBandwidth.Share(1_000_000, 1, 500_000));
        Assert.Equal(250_000, UploadBandwidth.Share(1_000_000, 3, 750_000));
    }

    [Fact]
    public void A_newcomer_waits_until_the_running_uploads_have_taken_their_reduced_slice()
    {
        // The incumbent still holds the whole limit: nothing is free, so the newcomer is told to wait (0), not given an overlapping slice.
        Assert.Equal(0, UploadBandwidth.Share(1_000_000, 1, 1_000_000));
        // The incumbent renewed and was cut to its fair half; now the newcomer's half is free.
        Assert.Equal(500_000, UploadBandwidth.Share(1_000_000, 1, 500_000));
    }

    [Fact]
    public void A_slice_below_the_minimum_is_a_wait_not_a_crawl()
    {
        Assert.Equal(0, UploadBandwidth.Share(16_000, 31, 0));
        Assert.Equal(UploadBandwidth.MinimumShareBytesPerSecond, UploadBandwidth.Share(16_000, 15, 0));
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

    [Fact]
    public void The_lowest_ceiling_wins_and_a_slice_not_yet_granted_holds_the_transfer()
    {
        Assert.Null(new UploadPolicyResponse().EffectiveBytesPerSecond);
        Assert.Equal(2_000_000, new UploadPolicyResponse { JobBytesPerSecond = 5_000_000, AgentBytesPerSecond = 2_000_000, TenantShareBytesPerSecond = 9_000_000 }.EffectiveBytesPerSecond);
        Assert.Equal(300_000, new UploadPolicyResponse { JobBytesPerSecond = 5_000_000, TenantShareBytesPerSecond = 300_000 }.EffectiveBytesPerSecond);
        Assert.Equal(0, new UploadPolicyResponse { JobBytesPerSecond = 5_000_000, TenantShareBytesPerSecond = 0 }.EffectiveBytesPerSecond);
    }

    [Fact]
    public void The_deadline_allowance_is_the_size_over_the_lowest_rate_and_is_capped()
    {
        Assert.Equal(TimeSpan.Zero, UploadPolicyResponse.DeadlineAllowance(1L << 40, null));
        Assert.Equal(TimeSpan.FromSeconds(100), UploadPolicyResponse.DeadlineAllowance(100_000_000, 1_000_000));
        Assert.Equal(UploadPolicyResponse.MaximumDeadlineAllowance, UploadPolicyResponse.DeadlineAllowance(1L << 40, 16_000));
    }
}
