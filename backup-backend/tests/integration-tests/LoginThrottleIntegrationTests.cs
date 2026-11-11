using System.Net;
using BackupPlatform.Api.Security;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class LoginThrottleIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string Kind = "login";
    private static string NewIdentifier() => $"throttle-{Guid.NewGuid():N}";
    private static string NewIp() => new IPAddress(Guid.NewGuid().ToByteArray()).ToString();
    private LoginThrottle Instance(TimeProvider clock) =>
        new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(fixture.ConnectionString).Options, clock);

    [Fact]
    public async Task Account_and_ip_waits_increase_are_bounded_and_expire()
    {
        var clock = new MutableClock();
        var throttle = Instance(clock);
        var account = NewIdentifier();
        var ip = NewIp();
        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++) await throttle.FailedAsync(Kind, account, ip, default);
        Assert.Null(await throttle.RetryAfterAsync(Kind, account, ip, default));
        await throttle.FailedAsync(Kind, account.ToUpperInvariant(), ip, default);
        Assert.Equal(LoginThrottle.Window, await throttle.RetryAfterAsync(Kind, $"  {account}  ", null, default));
        Assert.Equal(LoginThrottle.Window, await throttle.RetryAfterAsync(Kind, null, ip, default));
        Assert.Null(await throttle.RetryAfterAsync("totp", account, ip, default));
        Assert.Null(await throttle.RetryAfterAsync(Kind, NewIdentifier(), NewIp(), default));

        clock.Advance(LoginThrottle.Window);
        Assert.Null(await throttle.RetryAfterAsync(Kind, account, ip, default));
        await throttle.FailedAsync(Kind, account, ip, default);
        Assert.Equal(LoginThrottle.Window * 2, await throttle.RetryAfterAsync(Kind, account, ip, default));
        for (var i = 0; i < 20; i++) await throttle.FailedAsync(Kind, account, ip, default);
        Assert.Equal(LoginThrottle.MaxWait, await throttle.RetryAfterAsync(Kind, account, null, default));
        Assert.Equal(LoginThrottle.MaxWait, await throttle.RetryAfterAsync(Kind, null, ip, default));
        clock.Advance(LoginThrottle.MaxWait);
        Assert.Null(await throttle.RetryAfterAsync(Kind, account, ip, default));

        await throttle.SucceededAsync(Kind, account, ip, default);
        await throttle.FailedAsync(Kind, account, null, default);
        Assert.Null(await throttle.RetryAfterAsync(Kind, account, null, default));
        await throttle.FailedAsync(Kind, null, ip, default);
        Assert.Equal(LoginThrottle.MaxWait, await throttle.RetryAfterAsync(Kind, null, ip, default));
    }

    [Fact]
    public async Task Ip_history_is_shared_across_accounts_and_account_history_across_ips()
    {
        var throttle = Instance(new MutableClock());
        var account = NewIdentifier();
        var ip = NewIp();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
            await throttle.FailedAsync(Kind, account, ip, default);

        Assert.NotNull(await throttle.RetryAfterAsync(Kind, account, NewIp(), default));
        Assert.NotNull(await throttle.RetryAfterAsync(Kind, NewIdentifier(), ip, default));
        await throttle.SucceededAsync(Kind, account, ip, default);
        Assert.Null(await throttle.RetryAfterAsync(Kind, account, NewIp(), default));
        Assert.NotNull(await throttle.RetryAfterAsync(Kind, NewIdentifier(), ip, default));
    }

    [Fact]
    public async Task Combined_wait_is_the_longer_wait_and_ipv4_mapped_addresses_share_a_counter()
    {
        var throttle = Instance(new MutableClock());
        var account = NewIdentifier();
        var ip = new IPAddress(Guid.NewGuid().ToByteArray()[..4]);
        var mapped = ip.MapToIPv6().ToString();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
            await throttle.FailedAsync(Kind, account, ip.ToString(), default);
        await throttle.FailedAsync(Kind, null, mapped, default);

        Assert.Equal(LoginThrottle.Window, await throttle.RetryAfterAsync(Kind, account, null, default));
        Assert.Equal(LoginThrottle.Window * 2, await throttle.RetryAfterAsync(Kind, account, mapped, default));
    }

    [Fact]
    public async Task Invalid_accounts_do_not_bypass_ip_throttling()
    {
        var throttle = Instance(new MutableClock());
        var ip = NewIp();
        foreach (var account in new string?[] { null, "", "a\u0000b", "a b", "\ud800", new string('a', 129) })
            await throttle.FailedAsync(Kind, account, ip, default);
        Assert.NotNull(await throttle.RetryAfterAsync(Kind, null, ip, default));
        Assert.Null(await throttle.RetryAfterAsync(Kind, "a\u0000b", "invalid", default));
    }

    [Fact]
    public async Task Concurrent_failures_are_counted_for_both_keys_across_instances()
    {
        var clock = new MutableClock();
        var instances = Enumerable.Range(0, 3).Select(_ => Instance(clock)).ToArray();
        var account = NewIdentifier();
        var ip = NewIp();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => instances[i % instances.Length].FailedAsync(Kind, account, ip, default)));

        await using var db = fixture.CreatePlatformContext();
        var entries = await db.LoginThrottleEntries.Where(entry => entry.Kind == Kind &&
            (entry.Identifier == account.ToUpperInvariant() || entry.Identifier == IPAddress.Parse(ip).MapToIPv6().ToString())).ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal(12, entry.Failures));
        foreach (var instance in instances) Assert.NotNull(await instance.RetryAfterAsync(Kind, account, ip, default));
    }

    [Fact]
    public async Task Inactive_history_is_removed_and_starts_again()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow.AddDays(-2));
        var throttle = Instance(clock);
        var account = NewIdentifier();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++) await throttle.FailedAsync(Kind, account, null, default);
        clock.Advance(LoginThrottle.Retention);
        await throttle.FailedAsync(Kind, account, null, default);
        Assert.Null(await throttle.RetryAfterAsync(Kind, account, null, default));
        await using var db = fixture.CreatePlatformContext();
        Assert.Equal(1, (await db.LoginThrottleEntries.SingleAsync(entry => entry.Kind == Kind && entry.Scope == "account" && entry.Identifier == account.ToUpperInvariant())).Failures);
    }
}
