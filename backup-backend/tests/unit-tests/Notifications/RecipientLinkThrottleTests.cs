using BackupPlatform.Api.Notifications;
using BackupPlatform.Notifications;

namespace BackupPlatform.UnitTests.Notifications;

/// <summary>The anonymous recipient endpoints are limited without ever refusing a genuine one-click unsubscribe.</summary>
public sealed class RecipientLinkThrottleTests
{
    private static RecipientLinkThrottle Throttle(int perToken = 3, int invalidPerClient = 3) =>
        new(new NotificationOptions { RecipientLinkAttemptsPerToken = perToken, RecipientLinkInvalidPerClient = invalidPerClient });

    [Fact]
    public void A_token_is_held_to_its_share_of_the_window_and_other_tokens_are_unaffected()
    {
        using var throttle = Throttle(perToken: 3);

        Assert.All(Enumerable.Range(0, 3), _ => Assert.True(throttle.TryAcquire("token-a")));
        Assert.False(throttle.TryAcquire("token-a"));
        Assert.True(throttle.TryAcquire("token-b"));
    }

    [Fact]
    public void A_mail_providers_single_one_click_request_per_token_is_never_refused_however_many_other_requests_arrive()
    {
        using var throttle = Throttle(invalidPerClient: 2);
        const string client = "10.0.0.1"; // behind a reverse proxy every caller shares this address

        for (var i = 0; i < 50; i++) throttle.RecordInvalid(client);
        Assert.True(throttle.IsBlocked(client));

        // Valid tokens are never limited by address: the flood above cannot starve genuine unsubscribes arriving through the same address.
        Assert.All(Enumerable.Range(0, 20), i => Assert.True(throttle.TryAcquire($"genuine-token-{i}")));
    }

    [Fact]
    public void Invalid_requests_block_only_the_client_that_made_them_and_only_after_their_budget()
    {
        using var throttle = Throttle(invalidPerClient: 3);

        Assert.False(throttle.IsBlocked("1.1.1.1"));
        for (var i = 0; i < 3; i++) throttle.RecordInvalid("1.1.1.1");

        Assert.True(throttle.IsBlocked("1.1.1.1"));
        Assert.False(throttle.IsBlocked("2.2.2.2"));
    }

    [Fact]
    public void The_limits_are_configuration_checked_at_startup()
    {
        Assert.NotEmpty(new NotificationOptions { RecipientLinkAttemptsPerToken = 0 }.Validate());
        Assert.NotEmpty(new NotificationOptions { RecipientLinkInvalidPerClient = 0 }.Validate());
        Assert.NotEmpty(new NotificationOptions { RecipientLinkWindow = TimeSpan.Zero }.Validate());
        Assert.Empty(new NotificationOptions().Validate());
    }
}
