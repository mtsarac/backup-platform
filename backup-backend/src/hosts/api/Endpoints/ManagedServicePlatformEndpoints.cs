using BackupPlatform.Api.Security;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Identity;
using BackupPlatform.ManagedService;
using BackupPlatform.ManagedService.Administration;
using BackupPlatform.ManagedService.Plans;
using BackupPlatform.ManagedService.Subscriptions;

namespace BackupPlatform.Api.Endpoints;

public sealed class PlanRequest
{
    public string? Name { get; init; }
    public long? StorageLimitBytes { get; init; }
    public bool? Unlimited { get; init; }
    public string? QuotaPolicyReference { get; init; }
    public bool? Enabled { get; init; }
}

public sealed class PlanEnabledRequest
{
    public bool? Enabled { get; init; }
}

public sealed class StorageLimitOverrideRequest
{
    public string? Kind { get; init; }
    public long? Bytes { get; init; }
}

public sealed class AssignPlanRequest
{
    public Guid? PlanId { get; init; }
    public StorageLimitOverrideRequest? StorageLimitOverride { get; init; }
    public DateTimeOffset? EndsAt { get; init; }
}

/// <summary>
/// Plan and tenant-subscription administration. Every route requires the PlatformAdmin policy and none is tenant-scoped: a
/// tenant Owner/Admin membership grants nothing here. They manage the commercial records and audit every change; the quota policies a plan references are administered in QuotaEndpoints.
/// </summary>
public static class ManagedServicePlatformEndpoints
{
    private const int DefaultPlanPageSize = 50;
    private const int DefaultHistoryPageSize = 25;
    private const int MaximumPageSize = 100;

    public static IEndpointRouteBuilder MapManagedServicePlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var platform = endpoints.MapGroup("/api/platform").RequireAuthorization(AuthorizationPolicies.PlatformAdmin);
        platform.MapGet("/plans", ListPlansAsync);
        platform.MapPost("/plans", CreatePlanAsync);
        platform.MapPut("/plans/{planId:guid}", UpdatePlanAsync);
        platform.MapPut("/plans/{planId:guid}/enabled", SetPlanEnabledAsync);
        platform.MapGet("/tenants/{tenantId:guid}/subscription", GetSubscriptionAsync);
        platform.MapGet("/tenants/{tenantId:guid}/subscriptions", ListSubscriptionsAsync);
        platform.MapPut("/tenants/{tenantId:guid}/subscription", AssignPlanAsync);
        platform.MapPost("/tenants/{tenantId:guid}/subscription/suspend", SuspendAsync);
        platform.MapPost("/tenants/{tenantId:guid}/subscription/resume", ResumeAsync);
        platform.MapPut("/tenants/{tenantId:guid}/subscription/storage-limit-override", SetOverrideAsync);
        return endpoints;
    }

    private static async Task<IResult> ListPlansAsync(
        bool? enabled, string? search, int? page, int? pageSize, ManagedServiceAdministrationService service, CancellationToken cancellationToken)
    {
        if (!TryPaging(page, pageSize, DefaultPlanPageSize, out var p, out var size))
        {
            return InvalidPaging();
        }
        var result = await service.ListPlansAsync(enabled, search, p, size, cancellationToken);
        return Results.Ok(Page(result, ToResponse));
    }

    private static Task<IResult> CreatePlanAsync(
        PlanRequest? request, ICurrentUser currentUser, ManagedServiceAdministrationService service, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var change = await service.CreatePlanAsync(
                request?.Name, request?.StorageLimitBytes, request?.Unlimited, request?.QuotaPolicyReference, request?.Enabled, currentUser.UserId!, cancellationToken);
            return change.Outcome == ManagedServiceOutcome.Changed
                ? Results.Json(ToResponse(change.Value!), statusCode: StatusCodes.Status201Created)
                : Failure(change.Outcome, change.Code);
        });

    private static Task<IResult> UpdatePlanAsync(
        Guid planId, PlanRequest? request, ICurrentUser currentUser, ManagedServiceAdministrationService service, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var change = await service.UpdatePlanAsync(
                new PlanId(planId), request?.Name, request?.StorageLimitBytes, request?.Unlimited, request?.QuotaPolicyReference, currentUser.UserId!, cancellationToken);
            return IsSuccess(change.Outcome) ? Results.Ok(ToResponse(change.Value!)) : Failure(change.Outcome, change.Code);
        });

    private static Task<IResult> SetPlanEnabledAsync(
        Guid planId, PlanEnabledRequest? request, ICurrentUser currentUser, ManagedServiceAdministrationService service, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var change = await service.SetPlanEnabledAsync(new PlanId(planId), request?.Enabled, currentUser.UserId!, cancellationToken);
            return IsSuccess(change.Outcome) ? Results.Ok(ToResponse(change.Value!)) : Failure(change.Outcome, change.Code);
        });

    private static async Task<IResult> GetSubscriptionAsync(Guid tenantId, ManagedServiceAdministrationService service, CancellationToken cancellationToken)
    {
        var state = await service.GetSubscriptionAsync(new TenantId(tenantId), cancellationToken);
        return state is null
            ? Failure(ManagedServiceOutcome.TenantNotFound, null)
            : Results.Ok(new { tenantId = state.TenantId.Value, subscription = state.Subscription is null ? null : ToResponse(state.Subscription) });
    }

    private static async Task<IResult> ListSubscriptionsAsync(
        Guid tenantId, int? page, int? pageSize, ManagedServiceAdministrationService service, CancellationToken cancellationToken)
    {
        if (!TryPaging(page, pageSize, DefaultHistoryPageSize, out var p, out var size))
        {
            return InvalidPaging();
        }
        var result = await service.ListSubscriptionsAsync(new TenantId(tenantId), p, size, cancellationToken);
        return result is null ? Failure(ManagedServiceOutcome.TenantNotFound, null) : Results.Ok(Page(result, ToResponse));
    }

    private static Task<IResult> AssignPlanAsync(
        Guid tenantId, AssignPlanRequest? request, ICurrentUser currentUser, ManagedServiceAdministrationService service, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var change = await service.AssignPlanAsync(
                new TenantId(tenantId), request?.PlanId, request?.StorageLimitOverride?.Kind, request?.StorageLimitOverride?.Bytes, request?.EndsAt,
                currentUser.UserId!, cancellationToken);
            if (change.Outcome != ManagedServiceOutcome.Changed)
            {
                return Failure(change.Outcome, change.Code);
            }
            var assignment = change.Value!;
            return Results.Ok(new
            {
                outcome = assignment.SupersededSubscriptionId is null ? "assigned" : "changed",
                subscription = ToResponse(assignment.Subscription),
                previousSubscriptionId = assignment.SupersededSubscriptionId?.Value
            });
        });

    private static Task<IResult> SuspendAsync(
        Guid tenantId, ICurrentUser currentUser, ManagedServiceAdministrationService service, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var change = await service.SuspendAsync(new TenantId(tenantId), currentUser.UserId!, cancellationToken);
            return IsSuccess(change.Outcome) ? Results.Ok(ToResponse(change.Value!)) : Failure(change.Outcome, change.Code);
        });

    private static Task<IResult> ResumeAsync(
        Guid tenantId, ICurrentUser currentUser, ManagedServiceAdministrationService service, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var change = await service.ResumeAsync(new TenantId(tenantId), currentUser.UserId!, cancellationToken);
            return IsSuccess(change.Outcome) ? Results.Ok(ToResponse(change.Value!)) : Failure(change.Outcome, change.Code);
        });

    private static Task<IResult> SetOverrideAsync(
        Guid tenantId, StorageLimitOverrideRequest? request, ICurrentUser currentUser, ManagedServiceAdministrationService service, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var change = await service.SetStorageLimitOverrideAsync(new TenantId(tenantId), request?.Kind, request?.Bytes, currentUser.UserId!, cancellationToken);
            return IsSuccess(change.Outcome) ? Results.Ok(ToResponse(change.Value!)) : Failure(change.Outcome, change.Code);
        });

    private static async Task<IResult> GuardAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (InvalidManagedServiceRequestException exception)
        {
            return Results.BadRequest(new { error = "invalid_request", field = exception.Field, code = exception.Code });
        }
    }

    private static bool IsSuccess(ManagedServiceOutcome outcome) => outcome is ManagedServiceOutcome.Changed or ManagedServiceOutcome.Unchanged;

    private static IResult Failure(ManagedServiceOutcome outcome, string? code) => outcome switch
    {
        ManagedServiceOutcome.TenantNotFound => Results.NotFound(new { error = "tenant_not_found" }),
        ManagedServiceOutcome.PlanNotFound => Results.NotFound(new { error = "plan_not_found" }),
        ManagedServiceOutcome.SubscriptionNotFound => Results.NotFound(new { error = "subscription_not_found" }),
        ManagedServiceOutcome.PlanNameTaken => Results.Conflict(new { error = "plan_name_taken" }),
        ManagedServiceOutcome.PlanDisabled => Results.Conflict(new { error = "plan_disabled" }),
        ManagedServiceOutcome.SystemPlanImmutable => Results.Conflict(new { error = "system_plan_immutable" }),
        ManagedServiceOutcome.PlanAlreadyAssigned => Results.Conflict(new { error = "plan_already_assigned" }),
        ManagedServiceOutcome.InvalidTransition => Results.Conflict(new { error = code ?? "invalid_transition" }),
        ManagedServiceOutcome.ConcurrentUpdate => Results.Conflict(new { error = "concurrent_update" }),
        ManagedServiceOutcome.QuotaPolicyNotFound => Results.NotFound(new { error = "quota_policy_not_found" }),
        ManagedServiceOutcome.QuotaPolicyDisabled => Results.Conflict(new { error = "quota_policy_disabled" }),
        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
    };

    private static bool TryPaging(int? page, int? pageSize, int defaultSize, out int p, out int size)
    {
        p = page ?? 1;
        size = pageSize ?? defaultSize;
        return p is >= 1 and <= 1_000_000 && size is >= 1 and <= MaximumPageSize;
    }

    private static IResult InvalidPaging() => Results.BadRequest(new { error = "invalid_request", field = "page", code = "invalid_value" });

    private static object Page<TSource, TResult>(PagedResult<TSource> result, Func<TSource, TResult> map) => new
    {
        items = result.Items.Select(map).ToList(),
        totalCount = result.TotalCount,
        page = result.Page,
        pageSize = result.PageSize
    };

    private static object ToResponse(PlanView plan) => new
    {
        planId = plan.PlanId.Value,
        name = plan.Name,
        enabled = plan.Enabled,
        storageLimitBytes = plan.StorageLimitBytes,
        quotaPolicyReference = plan.QuotaPolicyReference,
        policyConfigurationVersion = plan.PolicyConfigurationVersion,
        isSystem = plan.IsSystem,
        systemKey = plan.SystemKey,
        tenantCount = plan.TenantCount,
        createdAt = plan.CreatedAt,
        updatedAt = plan.UpdatedAt
    };

    private static object ToResponse(SubscriptionView subscription) => new
    {
        subscriptionId = subscription.SubscriptionId.Value,
        tenantId = subscription.TenantId.Value,
        planId = subscription.PlanId.Value,
        planName = subscription.PlanName,
        planStorageLimitBytes = subscription.PlanStorageLimitBytes,
        status = subscription.Status.ToString(),
        startsAt = subscription.StartsAt,
        endsAt = subscription.EndsAt,
        storageLimitOverride = new { kind = OverrideKind(subscription.StorageLimitOverride.Kind), bytes = subscription.StorageLimitOverride.Bytes },
        effectiveStorageLimit = new
        {
            unlimited = subscription.EffectiveStorageLimit.IsUnlimited,
            bytes = subscription.EffectiveStorageLimit.Bytes,
            source = subscription.EffectiveStorageLimit.Source == EffectiveStorageLimitSource.Override ? "override" : "plan"
        },
        assignedByUserId = subscription.AssignedByUserId?.Value,
        assignedByDisplayName = subscription.AssignedByDisplayName,
        createdAt = subscription.CreatedAt,
        updatedAt = subscription.UpdatedAt
    };

    private static string OverrideKind(StorageLimitOverrideKind kind) => kind switch
    {
        StorageLimitOverrideKind.Limited => "limited",
        StorageLimitOverrideKind.Unlimited => "unlimited",
        _ => "none"
    };
}
