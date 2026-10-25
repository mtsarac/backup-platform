using BackupPlatform.Api.Security;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class LoginThrottleIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static string NewIdentifier() => $"throttle-{Guid.NewGuid():N}";

    private LoginThrottle Instance(TimeProvider clock) =>
        new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(fixture.ConnectionString).Options, clock);

    [Fact]
    public async Task Locks_after_max_failures_until_the_window_passes_and_a_success_clears()
    {
        var clock = new MutableClock();
        var throttle = Instance(clock);
        var identifier = NewIdentifier();
        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++) await throttle.FailedAsync(identifier, default);
        Assert.Null(await throttle.RetryAfterAsync(identifier, default));
        await throttle.FailedAsync(identifier.ToUpperInvariant(), default);
        var wait = await throttle.RetryAfterAsync($"  {identifier}  ", default);
        Assert.Equal(LoginThrottle.Window, wait);
        Assert.Null(await throttle.RetryAfterAsync(NewIdentifier(), default));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(LoginThrottle.Window - TimeSpan.FromMinutes(1), await throttle.RetryAfterAsync(identifier, default));
        clock.Advance(LoginThrottle.Window);
        Assert.Null(await throttle.RetryAfterAsync(identifier, default));

        for (var i = 0; i < LoginThrottle.MaxFailures; i++) await throttle.FailedAsync(identifier, default);
        Assert.NotNull(await throttle.RetryAfterAsync(identifier, default));
        await throttle.SucceededAsync(identifier, default);
        Assert.Null(await throttle.RetryAfterAsync(identifier, default));
    }

    [Fact]
    public async Task A_missing_identifier_is_counted_like_an_empty_one_and_never_throws()
    {
        var throttle = Instance(new MutableClock());

        await throttle.FailedAsync(null!, default);

        Assert.Null(await throttle.RetryAfterAsync(null!, default));
    }

    [Fact]
    public async Task Several_instances_share_one_counter_even_when_failures_arrive_together()
    {
        var clock = new MutableClock();
        var instances = Enumerable.Range(0, 3).Select(_ => Instance(clock)).ToArray();
        var identifier = NewIdentifier();

        await Task.WhenAll(Enumerable.Range(0, 12).Select(index => instances[index % instances.Length].FailedAsync(identifier, default)));

        await using var db = fixture.CreatePlatformContext();
        Assert.Equal(12, (await db.LoginThrottleEntries.SingleAsync(entry => entry.Identifier == identifier.ToUpperInvariant())).Failures);
        foreach (var instance in instances) Assert.NotNull(await instance.RetryAfterAsync(identifier, default));
    }

    [Fact]
    public async Task Expired_rows_are_purged_by_the_next_failure_and_new_identifiers_stop_being_tracked_when_the_table_is_full()
    {
        var clock = new MutableClock();
        var throttle = Instance(clock);
        await using (var db = fixture.CreatePlatformContext())
            await db.Database.ExecuteSqlRawAsync("""DELETE FROM login_throttle_entries""");
        var stale = NewIdentifier();
        await throttle.FailedAsync(stale, default);
        clock.Advance(LoginThrottle.Window + TimeSpan.FromSeconds(1));
        await throttle.FailedAsync(NewIdentifier(), default);
        await using (var db = fixture.CreatePlatformContext())
            Assert.False(await db.LoginThrottleEntries.AnyAsync(entry => entry.Identifier == stale.ToUpperInvariant()));

        var tracked = NewIdentifier();
        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++) await throttle.FailedAsync(tracked, default);
        await using (var db = fixture.CreatePlatformContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO login_throttle_entries ("Identifier", "Failures", "WindowStart")
                SELECT 'FILLER-' || n, 1, {clock.GetUtcNow()} FROM generate_series(1, 10000) n
                """);
        var untracked = NewIdentifier();
        await throttle.FailedAsync(untracked, default);
        await throttle.FailedAsync(tracked, default);

        await using var verify = fixture.CreatePlatformContext();
        Assert.False(await verify.LoginThrottleEntries.AnyAsync(entry => entry.Identifier == untracked.ToUpperInvariant()));
        Assert.NotNull(await throttle.RetryAfterAsync(tracked, default));
    }
}
