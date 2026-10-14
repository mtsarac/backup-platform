using BackupPlatform.Api.Security;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Notifications;
using BackupPlatform.Notifications;

namespace BackupPlatform.Api.Endpoints;

public sealed class NotificationPolicyRequest
{
    public bool? Enabled { get; init; }
    public string[]? EventTypes { get; init; }
    public string? MinimumSeverity { get; init; }
    public string[]? ChannelKinds { get; init; }
    public bool? RecoveryNotifications { get; init; }
    public int? StaleAfterHours { get; init; }
    public int? AgentOfflineAfterMinutes { get; init; }
    public int? ReminderIntervalHours { get; init; }
    public int? MaxNotificationsPerHour { get; init; }
}

public sealed class CreateNotificationChannelRequest
{
    public string? Kind { get; init; }
    public string? Name { get; init; }
    public string? Target { get; init; }
    public string? Authentication { get; init; }
    public string? Secret { get; init; }
}

public sealed class UpdateNotificationChannelRequest
{
    public string? Name { get; init; }
    public string? Target { get; init; }
    public string? Authentication { get; init; }
}

public sealed class NotificationSecretRequest
{
    public string? Value { get; init; }
}

public sealed class EmailTransportRequest
{
    public bool? Enabled { get; init; }
    public string? Host { get; init; }
    public int? Port { get; init; }
    public string? Security { get; init; }
    public string? Username { get; init; }
    public string? FromAddress { get; init; }
    public string? FromName { get; init; }
}

public sealed class SmsTransportRequest
{
    public bool? Enabled { get; init; }
    public string? Provider { get; init; }
    public string? Endpoint { get; init; }
    public string? Authentication { get; init; }
    public string? Username { get; init; }
    public string? HeaderName { get; init; }
    public string? Sender { get; init; }
}

/// <summary>
/// Notification administration. Tenant routes are tenant-administrator only and bind every lookup to the member's own tenant, so another tenant's id is
/// not found; platform routes (the SMTP relay and the SMS gateway) are PlatformAdmin only. A response never contains a secret value or a full target:
/// only whether a credential is set and a masked display of the destination.
/// </summary>
public static class NotificationEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaximumPageSize = 100;

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tenant = endpoints.MapGroup("/api/tenants/{tenantId:guid}/notifications").WithMetadata(new TenantScopedEndpoint())
            .RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        tenant.MapGet("/policy", GetPolicyAsync);
        tenant.MapPut("/policy", PutPolicyAsync);
        tenant.MapGet("/channels", ListChannelsAsync);
        tenant.MapPost("/channels", CreateChannelAsync);
        tenant.MapGet("/channels/{id:guid}", GetChannelAsync);
        tenant.MapPut("/channels/{id:guid}", UpdateChannelAsync);
        tenant.MapPut("/channels/{id:guid}/secret", SetSecretAsync);
        tenant.MapDelete("/channels/{id:guid}/secret", ClearSecretAsync);
        tenant.MapPost("/channels/{id:guid}/enable", (Guid id, ITenantContext t, ICurrentUser u, NotificationAdministrationService s, CancellationToken c) => ToggleAsync(id, true, t, u, s, c));
        tenant.MapPost("/channels/{id:guid}/disable", (Guid id, ITenantContext t, ICurrentUser u, NotificationAdministrationService s, CancellationToken c) => ToggleAsync(id, false, t, u, s, c));
        tenant.MapDelete("/channels/{id:guid}", DeleteChannelAsync);
        tenant.MapPost("/channels/{id:guid}/test", TestChannelAsync);
        tenant.MapGet("/deliveries", ListDeliveriesAsync);

        var platform = endpoints.MapGroup("/api/platform/notification-transports").RequireAuthorization(AuthorizationPolicies.PlatformAdmin);
        platform.MapGet("", ListTransportsAsync);
        platform.MapPut("/email", PutEmailTransportAsync);
        platform.MapPut("/sms", PutSmsTransportAsync);
        platform.MapPut("/{key}/secret", SetTransportSecretAsync);
        platform.MapDelete("/{key}/secret", ClearTransportSecretAsync);
        return endpoints;
    }

    // ---- shared -------------------------------------------------------------------------------------------------------------------------

    private static NotificationActor TenantActor(ICurrentUser user) => new(user.UserId!, AuditActorKind.TenantAdministrator);

    private static NotificationActor PlatformActor(ICurrentUser user) => new(user.UserId!, AuditActorKind.PlatformAdmin);

    private static async Task<IResult> GuardAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (InvalidNotificationException exception)
        {
            return Results.BadRequest(new { error = "invalid_request", field = exception.Field, code = exception.Code });
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "invalid_request", field = "request", code = "invalid_value" });
        }
    }

    private static IResult Failure(NotificationOutcome outcome) => outcome switch
    {
        NotificationOutcome.NotFound => Results.NotFound(new { error = "notification_channel_not_found" }),
        NotificationOutcome.TenantNotFound => Results.NotFound(new { error = "tenant_not_found" }),
        NotificationOutcome.NameTaken => Results.Conflict(new { error = "notification_channel_name_taken" }),
        NotificationOutcome.TargetTaken => Results.Conflict(new { error = "notification_channel_target_taken" }),
        NotificationOutcome.LimitReached => Results.Conflict(new { error = "notification_channel_limit_reached" }),
        NotificationOutcome.TestRateLimited => Results.Conflict(new { error = "notification_test_rate_limited" }),
        NotificationOutcome.TransportNotConfigured => Results.Conflict(new { error = "transport_not_configured" }),
        NotificationOutcome.ConcurrentUpdate => Results.Conflict(new { error = "concurrent_update" }),
        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
    };

    private static IResult Respond(NotificationResult<NotificationChannelView> result, bool created = false) =>
        !result.Succeeded ? Failure(result.Outcome)
        : created ? Results.Json(ToResponse(result.Value!), statusCode: StatusCodes.Status201Created)
        : Results.Ok(ToResponse(result.Value!));

    private static object ToResponse(NotificationChannelView view) => new
    {
        id = view.Id, kind = view.Kind, name = view.Name, targetDisplay = view.TargetDisplay, enabled = view.Enabled, authentication = view.Authentication,
        hasSecret = view.HasSecret, secretUpdatedAt = view.SecretUpdatedAt, createdAt = view.CreatedAt, updatedAt = view.UpdatedAt, lastDelivery = view.LastDelivery
    };

    // ---- policy -------------------------------------------------------------------------------------------------------------------------

    private static async Task<IResult> GetPolicyAsync(ITenantContext tenant, NotificationAdministrationService service, CancellationToken cancellationToken) =>
        await service.GetPolicyAsync(tenant.TenantId!, cancellationToken) is { } view ? Results.Ok(ToResponse(view)) : Failure(NotificationOutcome.TenantNotFound);

    private static Task<IResult> PutPolicyAsync(NotificationPolicyRequest request, ITenantContext tenant, ICurrentUser user, NotificationAdministrationService service, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var command = new PolicyCommand(
                request.Enabled ?? throw new InvalidNotificationException("enabled", "required"), request.EventTypes, request.MinimumSeverity, request.ChannelKinds,
                request.RecoveryNotifications ?? throw new InvalidNotificationException("recoveryNotifications", "required"),
                request.StaleAfterHours ?? throw new InvalidNotificationException("staleAfterHours", "required"),
                request.AgentOfflineAfterMinutes ?? throw new InvalidNotificationException("agentOfflineAfterMinutes", "required"),
                request.ReminderIntervalHours ?? throw new InvalidNotificationException("reminderIntervalHours", "required"),
                request.MaxNotificationsPerHour ?? throw new InvalidNotificationException("maxNotificationsPerHour", "required"));
            var result = await service.UpdatePolicyAsync(tenant.TenantId!, command, TenantActor(user), cancellationToken);
            return result.Succeeded ? Results.Ok(ToResponse(result.Value!)) : Failure(result.Outcome);
        });

    private static object ToResponse(NotificationPolicyView view) => new
    {
        enabled = view.Enabled, eventTypes = view.EventTypes, minimumSeverity = view.MinimumSeverity, channelKinds = view.ChannelKinds,
        recoveryNotifications = view.RecoveryNotifications, staleAfterHours = view.StaleAfterHours, agentOfflineAfterMinutes = view.AgentOfflineAfterMinutes,
        reminderIntervalHours = view.ReminderIntervalHours, maxNotificationsPerHour = view.MaxNotificationsPerHour, updatedAt = view.UpdatedAt,
        transports = new { email = new { available = view.EmailAvailable }, sms = new { available = view.SmsAvailable } }
    };

    // ---- channels -----------------------------------------------------------------------------------------------------------------------

    private static async Task<IResult> ListChannelsAsync(ITenantContext tenant, NotificationAdministrationService service, CancellationToken cancellationToken) =>
        await service.ListChannelsAsync(tenant.TenantId!, cancellationToken) is { } items ? Results.Ok(new { items = items.Select(ToResponse).ToList() }) : Failure(NotificationOutcome.TenantNotFound);

    private static async Task<IResult> GetChannelAsync(Guid id, ITenantContext tenant, NotificationAdministrationService service, CancellationToken cancellationToken) =>
        await service.GetChannelAsync(tenant.TenantId!, id, cancellationToken) is { } view ? Results.Ok(ToResponse(view)) : Failure(NotificationOutcome.NotFound);

    private static Task<IResult> CreateChannelAsync(CreateNotificationChannelRequest request, ITenantContext tenant, ICurrentUser user, NotificationAdministrationService service, CancellationToken cancellationToken) =>
        GuardAsync(async () => Respond(await service.CreateChannelAsync(tenant.TenantId!,
            new CreateChannelCommand(request.Kind, request.Name, request.Target, request.Authentication, request.Secret), TenantActor(user), cancellationToken), created: true));

    private static Task<IResult> UpdateChannelAsync(Guid id, UpdateNotificationChannelRequest request, ITenantContext tenant, ICurrentUser user, NotificationAdministrationService service, CancellationToken cancellationToken) =>
        GuardAsync(async () => Respond(await service.UpdateChannelAsync(tenant.TenantId!, id,
            new UpdateChannelCommand(request.Name, request.Target, request.Authentication), TenantActor(user), cancellationToken)));

    private static Task<IResult> SetSecretAsync(Guid id, NotificationSecretRequest request, ITenantContext tenant, ICurrentUser user, NotificationAdministrationService service, CancellationToken cancellationToken) =>
        GuardAsync(async () => Respond(await service.SetSecretAsync(tenant.TenantId!, id, request.Value, TenantActor(user), cancellationToken)));

    private static Task<IResult> ClearSecretAsync(Guid id, ITenantContext tenant, ICurrentUser user, NotificationAdministrationService service, CancellationToken cancellationToken) =>
        GuardAsync(async () => Respond(await service.ClearSecretAsync(tenant.TenantId!, id, TenantActor(user), cancellationToken)));

    private static Task<IResult> ToggleAsync(Guid id, bool enabled, ITenantContext tenant, ICurrentUser user, NotificationAdministrationService service, CancellationToken cancellationToken) =>
        GuardAsync(async () => Respond(await service.SetEnabledAsync(tenant.TenantId!, id, enabled, TenantActor(user), cancellationToken)));

    private static Task<IResult> DeleteChannelAsync(Guid id, ITenantContext tenant, ICurrentUser user, NotificationAdministrationService service, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var outcome = await service.DeleteChannelAsync(tenant.TenantId!, id, TenantActor(user), cancellationToken);
            return outcome == NotificationOutcome.Changed ? Results.NoContent() : Failure(outcome);
        });

    private static Task<IResult> TestChannelAsync(Guid id, ITenantContext tenant, ICurrentUser user, NotificationAdministrationService service, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var result = await service.RequestTestAsync(tenant.TenantId!, id, TenantActor(user), cancellationToken);
            return result.Succeeded ? Results.Json(new { deliveryId = result.Value!.DeliveryId }, statusCode: StatusCodes.Status202Accepted) : Failure(result.Outcome);
        });

    // ---- history ------------------------------------------------------------------------------------------------------------------------

    private static Task<IResult> ListDeliveriesAsync(
        string? status, string? kind, string? eventType, int? page, int? pageSize, ITenantContext tenant, NotificationAdministrationService service, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var p = page ?? 1;
            var size = pageSize ?? DefaultPageSize;
            if (p is < 1 or > 1_000_000 || size is < 1 or > MaximumPageSize) throw new InvalidNotificationException("page", "invalid_value");
            var result = await service.ListDeliveriesAsync(tenant.TenantId!, status, kind, eventType, p, size, cancellationToken);
            return Results.Ok(new
            {
                items = result.Items.Select(d => new
                {
                    id = d.Id, eventId = d.EventId, eventType = d.EventType, phase = d.Phase, severity = d.Severity, subjectKind = d.SubjectKind, subjectName = d.SubjectName,
                    channelKind = d.ChannelKind, channelName = d.ChannelName, targetDisplay = d.TargetDisplay, status = d.Status, attempts = d.Attempts, errorCode = d.ErrorCode,
                    nextAttemptAt = d.NextAttemptAt, sentAt = d.SentAt, createdAt = d.CreatedAt
                }).ToList(),
                totalCount = result.TotalCount, page = result.Page, pageSize = result.PageSize
            });
        });

    // ---- platform transports ------------------------------------------------------------------------------------------------------------

    private static async Task<IResult> ListTransportsAsync(NotificationTransportService service, CancellationToken cancellationToken)
    {
        var transports = await service.ListAsync(cancellationToken);
        return Results.Ok(new { email = ToEmailResponse(transports[0]), sms = ToSmsResponse(transports[1]) });
    }

    private static Task<IResult> PutEmailTransportAsync(EmailTransportRequest request, ICurrentUser user, NotificationTransportService service, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var values = new Dictionary<string, string?>
            {
                ["host"] = request.Host, ["port"] = request.Port?.ToString(), ["security"] = request.Security, ["username"] = request.Username,
                ["fromAddress"] = request.FromAddress, ["fromName"] = request.FromName
            };
            var result = await service.UpdateAsync(NotificationTransports.Email, new TransportCommand(request.Enabled ?? throw new InvalidNotificationException("enabled", "required"), values), PlatformActor(user), cancellationToken);
            return result.Succeeded ? Results.Ok(ToEmailResponse(result.Value!)) : Failure(result.Outcome);
        });

    private static Task<IResult> PutSmsTransportAsync(SmsTransportRequest request, ICurrentUser user, NotificationTransportService service, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var values = new Dictionary<string, string?>
            {
                ["provider"] = request.Provider, ["endpoint"] = request.Endpoint, ["authentication"] = request.Authentication, ["username"] = request.Username,
                ["headerName"] = request.HeaderName, ["sender"] = request.Sender
            };
            var result = await service.UpdateAsync(NotificationTransports.Sms, new TransportCommand(request.Enabled ?? throw new InvalidNotificationException("enabled", "required"), values), PlatformActor(user), cancellationToken);
            return result.Succeeded ? Results.Ok(ToSmsResponse(result.Value!)) : Failure(result.Outcome);
        });

    private static Task<IResult> SetTransportSecretAsync(string key, NotificationSecretRequest request, ICurrentUser user, NotificationTransportService service, CancellationToken cancellationToken) =>
        GuardAsync(async () => RespondTransport(await service.SetSecretAsync(key, request.Value, PlatformActor(user), cancellationToken)));

    private static Task<IResult> ClearTransportSecretAsync(string key, ICurrentUser user, NotificationTransportService service, CancellationToken cancellationToken) =>
        GuardAsync(async () => RespondTransport(await service.ClearSecretAsync(key, PlatformActor(user), cancellationToken)));

    private static IResult RespondTransport(NotificationResult<NotificationTransportView> result) =>
        !result.Succeeded ? Failure(result.Outcome) : Results.Ok(result.Value!.Key == NotificationTransports.Email ? ToEmailResponse(result.Value) : ToSmsResponse(result.Value));

    private static object ToEmailResponse(NotificationTransportView view)
    {
        string? Value(string key) => view.Configuration.TryGetValue(key, out var value) ? value : null;
        return new
        {
            enabled = view.Enabled, configured = view.Configured, host = Value("host"), port = int.TryParse(Value("port"), out var port) ? port : (int?)null,
            security = Value("security") ?? "starttls", username = Value("username"), fromAddress = Value("fromAddress"), fromName = Value("fromName"),
            hasSecret = view.HasSecret, secretUpdatedAt = view.SecretUpdatedAt, updatedAt = view.UpdatedAt
        };
    }

    private static object ToSmsResponse(NotificationTransportView view)
    {
        string? Value(string key) => view.Configuration.TryGetValue(key, out var value) ? value : null;
        return new
        {
            enabled = view.Enabled, configured = view.Configured, provider = Value("provider") ?? NotificationTransportRules.GenericHttpProvider, endpoint = Value("endpoint"),
            authentication = Value("authentication") ?? "none", username = Value("username"), headerName = Value("headerName"), sender = Value("sender"),
            hasSecret = view.HasSecret, secretUpdatedAt = view.SecretUpdatedAt, updatedAt = view.UpdatedAt
        };
    }
}
