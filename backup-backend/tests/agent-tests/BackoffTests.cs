using BackupPlatform.Agent.Core.Connectivity;

namespace BackupPlatform.AgentTests;

public sealed class BackoffTests
{
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(60);

    [Fact]
    public void Ceiling_grows_exponentially_from_the_base_and_stops_at_the_cap()
    {
        var backoff = new AgentBackoff(Base, Max, () => 0.5);

        Assert.Equal(TimeSpan.FromSeconds(1), backoff.GetCeiling(0));
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.GetCeiling(1));
        Assert.Equal(TimeSpan.FromSeconds(32), backoff.GetCeiling(5));
        Assert.Equal(Max, backoff.GetCeiling(6));
        Assert.Equal(Max, backoff.GetCeiling(1_000));
        Assert.Equal(Max, backoff.GetCeiling(int.MaxValue));
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 2_000)]
    [InlineData(0.25, 1_000)]
    public void Delay_is_the_ceiling_scaled_by_the_random_sample(double sample, int expectedMilliseconds)
    {
        var backoff = new AgentBackoff(Base, Max, () => sample);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), backoff.GetDelay(2));
    }

    [Fact]
    public void Delay_stays_within_zero_and_the_ceiling_for_any_sample()
    {
        var random = new Random(1234);
        var backoff = new AgentBackoff(Base, Max, random.NextDouble);

        for (var retry = 0; retry < 40; retry++)
        {
            for (var i = 0; i < 200; i++)
            {
                var delay = backoff.GetDelay(retry);
                Assert.InRange(delay, TimeSpan.Zero, backoff.GetCeiling(retry));
                Assert.True(delay <= Max);
            }
        }
    }

    [Fact]
    public void Delay_is_jittered_not_constant()
    {
        var random = new Random(7);
        var backoff = new AgentBackoff(Base, Max, random.NextDouble);

        var distinct = Enumerable.Range(0, 50).Select(_ => backoff.GetDelay(6)).Distinct().Count();

        Assert.True(distinct > 40, $"expected spread, saw {distinct} distinct delays");
    }

    [Fact]
    public void Same_random_sequence_gives_the_same_delays()
    {
        var first = new AgentBackoff(Base, Max, new Random(99).NextDouble);
        var second = new AgentBackoff(Base, Max, new Random(99).NextDouble);

        Assert.Equal(
            Enumerable.Range(0, 20).Select(first.GetDelay),
            Enumerable.Range(0, 20).Select(second.GetDelay));
    }

    [Fact]
    public void Misbehaving_random_source_cannot_push_the_delay_outside_its_bounds()
    {
        Assert.Equal(TimeSpan.Zero, new AgentBackoff(Base, Max, () => -3).GetDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(8), new AgentBackoff(Base, Max, () => 42).GetDelay(3));
    }

    [Fact]
    public void Interval_jitter_only_lengthens_by_at_most_ten_percent()
    {
        var interval = TimeSpan.FromSeconds(30);
        Assert.Equal(interval, new AgentBackoff(Base, Max, () => 0).AddJitter(interval));
        Assert.Equal(TimeSpan.FromSeconds(33), new AgentBackoff(Base, Max, () => 1).AddJitter(interval));

        var random = new Random(5);
        var backoff = new AgentBackoff(Base, Max, random.NextDouble);
        for (var i = 0; i < 1_000; i++)
        {
            Assert.InRange(backoff.AddJitter(interval), interval, TimeSpan.FromSeconds(33));
        }
    }

    [Fact]
    public void Constructor_rejects_unusable_delays()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentBackoff(TimeSpan.Zero, Max));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentBackoff(Max, Base));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentBackoff(Base, Max).GetCeiling(-1));
    }
}
