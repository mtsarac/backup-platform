using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Api.Notifications;

/// <summary>
/// Emails security notifications to users with a verified address when the platform relay is available. The in-app notification is the record:
/// this only adds an email and marks each notification handled, so a failure here never removes it. Rows are claimed with
/// FOR UPDATE SKIP LOCKED, so several nodes do not send the same message; a crash mid-send repeats it (at-least-once).
/// </summary>
public sealed class SecurityNotificationMailWorker(DbContextOptions<PlatformDbContext> options, IAccountMailer mailer, TimeProvider time, ILogger<SecurityNotificationMailWorker> logger) : BackgroundService
{
    private static readonly TimeSpan RetryWindow = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await ProcessAsync(stoppingToken) == BatchSize) { }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("Security notification mail pass failed ({ExceptionType}); it will retry.", exception.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private const int BatchSize = 20;

    private sealed record Pending(Guid Id, string Type, DateTimeOffset OccurredAt, string? Email, DateTimeOffset? EmailVerifiedAt);

    /// <summary>Handles one batch and returns how many notifications it looked at.</summary>
    public async Task<int> ProcessAsync(CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var pending = await context.Database.SqlQuery<Pending>($"""
            SELECT n."Id", e."Type", e."OccurredAt", c."Email", c."EmailVerifiedAt"
            FROM user_notifications n
            JOIN notification_events e ON e."Id" = n."EventId"
            LEFT JOIN password_credentials c ON c."UserId" = n."UserId"
            WHERE n."EmailHandledAt" IS NULL AND e."Type" LIKE 'security\_%'
            ORDER BY e."OccurredAt"
            LIMIT {BatchSize}
            FOR UPDATE OF n SKIP LOCKED
            """).ToListAsync(cancellationToken);
        if (pending.Count == 0) return 0;
        var available = await mailer.IsAvailableAsync(cancellationToken);
        var now = time.GetUtcNow();
        var handled = new List<Guid>();
        foreach (var item in pending)
        {
            // No verified address or no relay: no email is attempted.
            bool? sent = available && item is { Email: not null, EmailVerifiedAt: not null }
                ? await mailer.SendSecurityNoticeAsync(item.Email, item.Type, item.OccurredAt, cancellationToken)
                : false;
            if (sent is not null || item.OccurredAt < now - RetryWindow) handled.Add(item.Id);
        }
        await context.UserNotifications.Where(row => handled.Contains(row.Id)).ExecuteUpdateAsync(set => set.SetProperty(row => row.EmailHandledAt, now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return pending.Count;
    }
}
