using BackupPlatform.Domain.Notifications;
using BackupPlatform.Notifications;
using BackupPlatform.UnitTests.Notifications.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static BackupPlatform.UnitTests.Notifications.Support.NotificationTestData;

namespace BackupPlatform.UnitTests.Notifications;

/// <summary>What happens to a claimed delivery: retry with backoff, permanent failure, give-up, lost lease, and a sender that misbehaves.</summary>
public sealed class NotificationDeliveryProcessorTests
{
    private static readonly NotificationOptions Options = new() { MaxAttempts = 4, BackoffBase = TimeSpan.FromSeconds(30), BackoffMax = TimeSpan.FromMinutes(10) };

    private readonly FakeTimeProvider _time = new(Start);
    private readonly ClaimingStore _store = new();

    private sealed class ClaimingStore : FakeNotificationStore
    {
        public List<DeliveryWork> Due { get; } = [];
        public List<(Guid Id, int Attempt, DeliveryResult Result)> Completed { get; } = [];
        public bool LeaseLost { get; set; }

        public override Task<IReadOnlyList<DeliveryWork>> ClaimDueAsync(string owner, DateTimeOffset now, int batchSize, TimeSpan lease, int maxAttempts, CancellationToken cancellationToken)
        {
            var batch = Due.Take(batchSize).ToList();
            Due.RemoveRange(0, batch.Count);
            return Task.FromResult<IReadOnlyList<DeliveryWork>>(batch);
        }

        public override Task<bool> CompleteAsync(Guid deliveryId, string owner, int attempt, DeliveryResult result, DateTimeOffset now, CancellationToken cancellationToken)
        {
            // The processor completes deliveries concurrently; an unsynchronised List.Add loses entries.
            lock (Completed) Completed.Add((deliveryId, attempt, result));
            return Task.FromResult(!LeaseLost);
        }
    }

    private sealed class ScriptedSender(Func<DeliveryWork, Task<SendOutcome>> send) : INotificationSender
    {
        public NotificationChannelKind Kind => NotificationChannelKind.Webhook;
        public Task<SendOutcome> SendAsync(DeliveryWork work, CancellationToken cancellationToken) => send(work);
    }

    private NotificationDeliveryProcessor Processor(Func<DeliveryWork, Task<SendOutcome>> send, NotificationOptions? options = null) =>
        new(_store, [new ScriptedSender(send)], options ?? Options, _time, NullLogger<NotificationDeliveryProcessor>.Instance);

    private static DeliveryWork Hook(int attempt = 1) => Work(NotificationChannelKind.Webhook, "https://hooks.example.com/n", attempt: attempt);

    [Fact]
    public async Task A_sent_delivery_is_recorded_as_sent()
    {
        _store.Due.Add(Hook());

        await Processor(_ => Task.FromResult(SendOutcome.Sent)).ProcessAsync("w1", CancellationToken.None);

        Assert.Equal(NotificationDeliveryStatus.Sent, Assert.Single(_store.Completed).Result.Status);
    }

    [Fact]
    public async Task A_transient_failure_is_rescheduled_with_a_growing_bounded_backoff()
    {
        var delays = new List<TimeSpan>();
        foreach (var attempt in new[] { 1, 2, 3 })
        {
            _store.Due.Add(Hook(attempt));
            await Processor(_ => Task.FromResult(SendOutcome.Transient("http_503"))).ProcessAsync("w1", CancellationToken.None);
            var result = _store.Completed[^1].Result;
            Assert.Equal(NotificationDeliveryStatus.Pending, result.Status);
            Assert.Equal("http_503", result.Code);
            delays.Add(result.NextAttemptAt!.Value - _time.GetUtcNow());
        }

        // 30 s, 60 s, 120 s with +-20% jitter.
        Assert.InRange(delays[0].TotalSeconds, 24, 36);
        Assert.InRange(delays[1].TotalSeconds, 48, 72);
        Assert.InRange(delays[2].TotalSeconds, 96, 144);
    }

    [Fact]
    public async Task The_provider_asked_wait_is_honoured_but_never_beyond_the_ceiling()
    {
        _store.Due.Add(Hook());
        await Processor(_ => Task.FromResult(SendOutcome.Transient("http_429", TimeSpan.FromMinutes(5)))).ProcessAsync("w1", CancellationToken.None);
        _store.Due.Add(Hook());
        await Processor(_ => Task.FromResult(SendOutcome.Transient("http_429", TimeSpan.FromDays(2)))).ProcessAsync("w1", CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(5), _store.Completed[0].Result.NextAttemptAt - _time.GetUtcNow());
        Assert.Equal(Options.BackoffMax, _store.Completed[1].Result.NextAttemptAt - _time.GetUtcNow());
    }

    [Fact]
    public async Task The_last_allowed_attempt_ends_in_failure_and_a_permanent_error_does_not_retry_at_all()
    {
        _store.Due.Add(Hook(attempt: Options.MaxAttempts));
        _store.Due.Add(Hook(attempt: 1));

        await Processor(work => Task.FromResult(work.Attempt == 1 ? SendOutcome.Permanent("http_400") : SendOutcome.Transient("http_503"))).ProcessAsync("w1", CancellationToken.None);

        Assert.All(_store.Completed, c => Assert.Equal(NotificationDeliveryStatus.Failed, c.Result.Status));
        Assert.Equal(["http_503", "http_400"], _store.Completed.Select(c => c.Result.Code).Order(StringComparer.Ordinal).Reverse());
    }

    [Fact]
    public async Task A_sender_that_throws_or_hangs_is_a_retryable_failure_and_never_escapes_the_worker()
    {
        var options = new NotificationOptions { SendTimeout = TimeSpan.FromMilliseconds(200), LeaseDuration = TimeSpan.FromSeconds(5) };
        _store.Due.Add(Hook());
        _store.Due.Add(Hook());

        var calls = 0;
        await Processor(_ => Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("boom with a secret value token=abc") : Hang(), options)
            .ProcessAsync("w1", CancellationToken.None);

        Assert.Equal(2, _store.Completed.Count);
        Assert.Equal(["send_failed", "timeout"], _store.Completed.Select(c => c.Result.Code).Order(StringComparer.Ordinal));
        Assert.All(_store.Completed, c => Assert.Equal(NotificationDeliveryStatus.Pending, c.Result.Status));
    }

    private static async Task<SendOutcome> Hang()
    {
        await Task.Delay(Timeout.Infinite, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        return SendOutcome.Sent;
    }

    [Fact]
    public async Task A_delivery_whose_lease_was_lost_is_not_overwritten_and_does_not_fail_the_pass()
    {
        _store.LeaseLost = true;
        _store.Due.Add(Hook());

        var claimed = await Processor(_ => Task.FromResult(SendOutcome.Sent)).ProcessAsync("w1", CancellationToken.None);

        Assert.Equal(1, claimed);
    }

    [Fact]
    public async Task A_delivery_the_store_marked_skipped_is_never_sent()
    {
        var sent = 0;
        var skipped = Hook() with { Skip = new DeliverySkip(NotificationDeliveryStatus.Suppressed, NotificationDeliveryCodes.ChannelDisabled), Target = null };
        _store.Due.Add(skipped);

        await Processor(_ => { sent++; return Task.FromResult(SendOutcome.Sent); }).ProcessAsync("w1", CancellationToken.None);

        Assert.Equal(0, sent);
        var result = Assert.Single(_store.Completed).Result;
        Assert.Equal((NotificationDeliveryStatus.Suppressed, NotificationDeliveryCodes.ChannelDisabled), (result.Status, result.Code));
    }

    [Fact]
    public async Task Sends_run_concurrently_but_never_beyond_the_configured_limit()
    {
        var options = new NotificationOptions { MaxConcurrentDeliveries = 3, DeliveryBatchSize = 50 };
        for (var i = 0; i < 12; i++) _store.Due.Add(Hook());
        int running = 0, peak = 0;

        await Processor(async _ =>
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            while (now > (seen = Volatile.Read(ref peak))) Interlocked.CompareExchange(ref peak, now, seen);
            await Task.Delay(40);
            Interlocked.Decrement(ref running);
            return SendOutcome.Sent;
        }, options).ProcessAsync("w1", CancellationToken.None);

        Assert.Equal(12, _store.Completed.Count);
        Assert.InRange(peak, 2, 3);
    }
}
