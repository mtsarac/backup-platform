using BackupPlatform.Application.Identity;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Api.Endpoints;

public static class UserNotificationEndpoints
{
    public static IEndpointRouteBuilder MapUserNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var inbox = endpoints.MapGroup("/api/notifications/in-app").RequireAuthorization();
        inbox.MapGet("", ListAsync);
        inbox.MapPost("/{id:guid}/read", ReadAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(ICurrentUser currentUser, DbContextOptions<PlatformDbContext> options, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var userId = currentUser.UserId!;
        var notifications = await (from item in context.VisibleUserNotifications(userId).AsNoTracking()
            join notificationEvent in context.NotificationEvents.AsNoTracking() on item.EventId equals notificationEvent.Id
            where item.UserId == userId
            orderby notificationEvent.OccurredAt descending, item.Id
            select new { item.Id, item.EventId, notificationEvent.Type, notificationEvent.SubjectName, notificationEvent.OccurredAt, item.ReadAt })
            .ToListAsync(cancellationToken);
        return Results.Ok(notifications);
    }

    private static async Task<IResult> ReadAsync(Guid id, ICurrentUser currentUser, DbContextOptions<PlatformDbContext> options, TimeProvider time, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        var userId = currentUser.UserId!;
        var query = context.VisibleUserNotifications(userId).Where(item => item.Id == id);
        var updated = await query.Where(item => item.ReadAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(item => item.ReadAt, time.GetUtcNow()), cancellationToken);
        return updated != 0 || await query.AnyAsync(cancellationToken) ? Results.NoContent() : Results.NotFound();
    }
}
