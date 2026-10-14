using BackupPlatform.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Notifications;

public enum SendStatus
{
    Sent,
    /// <summary>Worth trying again later (timeout, 5xx, rate limit, connection refused).</summary>
    Retry,
    /// <summary>Will never work as configured (rejected recipient, bad credentials, forbidden address, 4xx).</summary>
    Failed
}

/// <summary><see cref="Code"/> is a stable, provider-independent code that is stored and shown; it never carries provider output.</summary>
public readonly record struct SendOutcome(SendStatus Status, string? Code = null, TimeSpan? RetryAfter = null)
{
    public static SendOutcome Sent { get; } = new(SendStatus.Sent);
    public static SendOutcome Transient(string code, TimeSpan? retryAfter = null) => new(SendStatus.Retry, code, retryAfter);
    public static SendOutcome Permanent(string code) => new(SendStatus.Failed, code);
}

/// <summary>Sends one claimed delivery over one kind of channel. Implementations never throw for provider failures (they return an outcome) and never log or return a secret.</summary>
public interface INotificationSender
{
    NotificationChannelKind Kind { get; }

    Task<SendOutcome> SendAsync(DeliveryWork work, CancellationToken cancellationToken);
}

/// <summary>
/// Claims due deliveries, sends them with bounded concurrency and records each outcome through a fenced update. A transient failure schedules a
/// retry with exponential backoff and jitter (honouring a provider's Retry-After); a permanent one, or the last attempt, fails the delivery. The
/// processor never touches backups and never throws a send failure upward, so a dead SMTP server or webhook only ever shows up as history.
/// </summary>
public sealed class NotificationDeliveryProcessor(
    INotificationStore store, IEnumerable<INotificationSender> senders, NotificationOptions options, TimeProvider time, ILogger<NotificationDeliveryProcessor> logger)
{
    private readonly Dictionary<NotificationChannelKind, INotificationSender> _senders = senders.ToDictionary(sender => sender.Kind);

    /// <summary>One pass. Returns how many deliveries were claimed.</summary>
    public async Task<int> ProcessAsync(string owner, CancellationToken cancellationToken)
    {
        var work = await store.ClaimDueAsync(owner, time.GetUtcNow(), options.DeliveryBatchSize, options.LeaseDuration, options.MaxAttempts, cancellationToken);
        if (work.Count == 0) return 0;
        using var gate = new SemaphoreSlim(options.MaxConcurrentDeliveries);
        await Task.WhenAll(work.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);
            try { await DeliverAsync(owner, item, cancellationToken); }
            finally { gate.Release(); }
        }));
        return work.Count;
    }

    private async Task DeliverAsync(string owner, DeliveryWork work, CancellationToken cancellationToken)
    {
        DeliveryResult result;
        if (work.Skip is { } skip)
        {
            result = new DeliveryResult(skip.Status, skip.Code);
        }
        else
        {
            var outcome = await SendAsync(work, cancellationToken);
            var now = time.GetUtcNow();
            result = outcome.Status switch
            {
                SendStatus.Sent => new DeliveryResult(NotificationDeliveryStatus.Sent),
                SendStatus.Retry when work.Attempt < options.MaxAttempts => new DeliveryResult(NotificationDeliveryStatus.Pending, outcome.Code, now + Backoff(work.Attempt, outcome.RetryAfter)),
                SendStatus.Retry => new DeliveryResult(NotificationDeliveryStatus.Failed, outcome.Code),
                _ => new DeliveryResult(NotificationDeliveryStatus.Failed, outcome.Code)
            };
        }
        try
        {
            if (!await store.CompleteAsync(work.DeliveryId, owner, work.Attempt, result, time.GetUtcNow(), cancellationToken))
                logger.LogWarning("Notification delivery {DeliveryId} lost its lease before the result was recorded; the new owner's result stands.", work.DeliveryId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The lease expires and another pass takes the delivery over: at-least-once, never lost.
            logger.LogError("Recording the result of notification delivery {DeliveryId} failed ({ExceptionType}).", work.DeliveryId, exception.GetType().Name);
        }
    }

    private async Task<SendOutcome> SendAsync(DeliveryWork work, CancellationToken cancellationToken)
    {
        if (!_senders.TryGetValue(work.Kind, out var sender)) return SendOutcome.Permanent("no_sender");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.SendTimeout);
        try
        {
            // WaitAsync enforces the deadline even for a sender that ignores its token; the lease only has to outlive this call.
            return await sender.SendAsync(work, timeout.Token).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return SendOutcome.Transient("timeout");
        }
        catch (Exception exception)
        {
            // Only the type: provider exception messages can carry hosts, addresses or credentials.
            logger.LogWarning("Notification delivery {DeliveryId} failed unexpectedly ({ExceptionType}).", work.DeliveryId, exception.GetType().Name);
            return SendOutcome.Transient("send_failed");
        }
    }

    /// <summary>Exponential backoff with ±20% jitter, at least the provider's Retry-After, never above <see cref="NotificationOptions.BackoffMax"/>.</summary>
    internal TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
    {
        var exponential = options.BackoffBase.TotalSeconds * Math.Pow(2, Math.Max(0, attempt - 1));
        var seconds = Math.Min(exponential, options.BackoffMax.TotalSeconds) * (0.8 + Random.Shared.NextDouble() * 0.4);
        var delay = TimeSpan.FromSeconds(seconds);
        if (retryAfter is { } requested && requested > delay) delay = requested;
        return delay > options.BackoffMax ? options.BackoffMax : delay;
    }
}
