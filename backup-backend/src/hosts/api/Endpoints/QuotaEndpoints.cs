using BackupPlatform.Api.Security;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using BackupPlatform.ManagedService;
using BackupPlatform.ManagedService.Administration;
using BackupPlatform.ManagedService.Entitlement;
using BackupPlatform.ManagedService.Quota;

namespace BackupPlatform.Api.Endpoints;

public sealed class QuotaScopeSelectorBody
{
    public string? Kind { get; init; }
    public List<string>? Values { get; init; }
}

public sealed class QuotaPolicyRequest
{
    public string? Key { get; init; }
    public string? Name { get; init; }
    public string? AccountingMode { get; init; }
    public List<QuotaScopeSelectorBody>? IncludedStorageScopes { get; init; }
    public string? EnforcementMode { get; init; }
    public int? WarningThresholdPercent { get; init; }
    public int? CriticalThresholdPercent { get; init; }
    public bool? Enabled { get; init; }
}

/// <summary>
/// Quota: PlatformAdmin-only policy and usage administration (never tenant-scoped, so a tenant Owner/Admin membership grants nothing there) and
/// one read-only tenant route that gives the tenant's own storage standing to any member of that tenant.
/// </summary>
public static class QuotaEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaximumPageSize = 100;

    public static IEndpointRouteBuilder MapQuotaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var platform = endpoints.MapGroup("/api/platform").RequireAuthorization(AuthorizationPolicies.PlatformAdmin);
        platform.MapGet("/quota-policies", ListPoliciesAsync);
        platform.MapPost("/quota-policies", CreatePolicyAsync);
        platform.MapPut("/quota-policies/{policyId:guid}", UpdatePolicyAsync);
        platform.MapGet("/tenants/{tenantId:guid}/storage-usage", GetUsageAsync);
        platform.MapGet("/tenants/{tenantId:guid}/quota", GetPlatformQuotaAsync);
        platform.MapPost("/tenants/{tenantId:guid}/storage-usage/reconcile", ReconcileAsync);
        platform.MapPost("/tenants/{tenantId:guid}/storage-usage/rebuild", RebuildAsync);
        platform.MapGet("/storage-usage", ListStorageUsageAsync);

        var tenant = endpoints.MapGroup("/api/tenants/{tenantId:guid}").WithMetadata(new TenantScopedEndpoint());
        tenant.MapGet("/storage-quota", GetTenantQuotaAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        return endpoints;
    }

    private static async Task<IResult> ListPoliciesAsync(QuotaPolicyAdministrationService service, CancellationToken cancellationToken) =>
        Results.Ok(new { items = (await service.ListAsync(cancellationToken)).Select(ToResponse).ToList() });

    private static Task<IResult> CreatePolicyAsync(
        QuotaPolicyRequest? request, ICurrentUser currentUser, QuotaPolicyAdministrationService service, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var change = await service.CreateAsync(
                request?.Key, request?.Name, request?.AccountingMode, Scopes(request), request?.EnforcementMode, request?.WarningThresholdPercent,
                request?.CriticalThresholdPercent, request?.Enabled, currentUser.UserId!, cancellationToken);
            return change.Outcome == ManagedServiceOutcome.Changed
                ? Results.Json(ToResponse(change.Value!.Policy), statusCode: StatusCodes.Status201Created)
                : Failure(change.Outcome, change.Code);
        });

    private static Task<IResult> UpdatePolicyAsync(
        Guid policyId, QuotaPolicyRequest? request, ICurrentUser currentUser, QuotaPolicyAdministrationService service, CancellationToken cancellationToken) =>
        GuardAsync(async () =>
        {
            var change = await service.UpdateAsync(
                new QuotaPolicyId(policyId), request?.Name, request?.AccountingMode, Scopes(request), request?.EnforcementMode, request?.WarningThresholdPercent,
                request?.CriticalThresholdPercent, request?.Enabled, currentUser.UserId!, cancellationToken);
            return change.Outcome is ManagedServiceOutcome.Changed or ManagedServiceOutcome.Unchanged
                ? Results.Ok(new { policy = ToResponse(change.Value!.Policy), tenantsRecalculated = change.Value.TenantsRecalculated })
                : Failure(change.Outcome, change.Code);
        });

    private static async Task<IResult> GetUsageAsync(Guid tenantId, IStorageUsageService usage, CancellationToken cancellationToken)
    {
        var view = await usage.GetUsageAsync(new TenantId(tenantId), cancellationToken);
        return view is null ? TenantNotFound() : Results.Ok(ToResponse(view));
    }

    private static async Task<IResult> ListStorageUsageAsync(
        string? search, string? quotaState, string? subscriptionStatus, int? page, int? pageSize, IStorageUsageService usage, TimeProvider time, CancellationToken cancellationToken)
    {
        var p = page ?? 1;
        var size = pageSize ?? DefaultPageSize;
        if (p is < 1 or > 1_000_000 || size is < 1 or > MaximumPageSize)
        {
            return Results.BadRequest(new { error = "invalid_request", field = "page", code = "invalid_value" });
        }
        if (!TryEnum<QuotaState>(quotaState, out var state))
        {
            return Results.BadRequest(new { error = "invalid_request", field = "quotaState", code = "invalid_value" });
        }
        if (!TryEnum<EntitlementStatus>(subscriptionStatus, out var status))
        {
            return Results.BadRequest(new { error = "invalid_request", field = "subscriptionStatus", code = "invalid_value" });
        }
        var result = await usage.ListStorageUsageAsync(new StorageUsageFilter(search, state, status), p, size, time.GetUtcNow(), cancellationToken);
        return Results.Ok(new { items = result.Items.Select(ToListItemResponse).ToList(), totalCount = result.TotalCount, page = result.Page, pageSize = result.PageSize });
    }

    private static async Task<IResult> GetPlatformQuotaAsync(Guid tenantId, IStorageUsageService usage, TimeProvider time, CancellationToken cancellationToken)
    {
        var status = await usage.GetQuotaStatusAsync(new TenantId(tenantId), time.GetUtcNow(), cancellationToken);
        return status is null ? TenantNotFound() : Results.Ok(ToPlatformResponse(status));
    }

    private static async Task<IResult> ReconcileAsync(Guid tenantId, ICurrentUser currentUser, IStorageUsageService usage, TimeProvider time, CancellationToken cancellationToken)
    {
        var result = await usage.ReconcileAsync(new TenantId(tenantId), currentUser.UserId!, time.GetUtcNow(), cancellationToken);
        return result is null ? TenantNotFound() : Results.Ok(ToResponse(result));
    }

    private static async Task<IResult> RebuildAsync(Guid tenantId, ICurrentUser currentUser, IStorageUsageService usage, TimeProvider time, CancellationToken cancellationToken)
    {
        var result = await usage.RebuildAsync(new TenantId(tenantId), currentUser.UserId!, time.GetUtcNow(), cancellationToken);
        return result is null ? TenantNotFound() : Results.Ok(ToResponse(result));
    }

    private static async Task<IResult> GetTenantQuotaAsync(ITenantContext tenant, IStorageUsageService usage, TimeProvider time, CancellationToken cancellationToken)
    {
        var status = await usage.GetQuotaStatusAsync(tenant.TenantId!, time.GetUtcNow(), cancellationToken);
        return status is null ? Results.NotFound() : Results.Ok(ToTenantResponse(status));
    }

    private static IReadOnlyList<QuotaScopeSelectorRequest>? Scopes(QuotaPolicyRequest? request) =>
        request?.IncludedStorageScopes?.Select(scope => new QuotaScopeSelectorRequest(scope?.Kind, scope?.Values)).ToList();

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

    private static IResult TenantNotFound() => Results.NotFound(new { error = "tenant_not_found" });

    private static IResult Failure(ManagedServiceOutcome outcome, string? code) => outcome switch
    {
        ManagedServiceOutcome.QuotaPolicyNotFound => Results.NotFound(new { error = "quota_policy_not_found" }),
        ManagedServiceOutcome.QuotaPolicyKeyTaken => Results.Conflict(new { error = "quota_policy_key_taken" }),
        ManagedServiceOutcome.InvalidTransition => Results.Conflict(new { error = code ?? "invalid_transition" }),
        ManagedServiceOutcome.ConcurrentUpdate => Results.Conflict(new { error = "concurrent_update" }),
        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
    };

    private static object ToResponse(QuotaPolicyView policy) => new
    {
        policyId = policy.Id.Value,
        key = policy.Key,
        name = policy.Name,
        version = policy.Version,
        accountingMode = policy.AccountingMode.ToString(),
        includedStorageScopes = ScopesResponse(policy.IncludedStorageScopes),
        enforcementMode = policy.EnforcementMode.ToString(),
        warningThresholdPercent = policy.WarningThresholdPercent,
        criticalThresholdPercent = policy.CriticalThresholdPercent,
        enabled = policy.Enabled,
        isDefault = policy.IsDefault,
        createdAt = policy.CreatedAt,
        updatedAt = policy.UpdatedAt
    };

    private static object ToResponse(TenantStorageUsageView usage) => new
    {
        tenantId = usage.TenantId.Value,
        usedBytes = usage.UsedBytes,
        reservedBytes = usage.ReservedBytes,
        calculatedAt = usage.CalculatedAt,
        accountingMode = usage.AccountingMode.ToString(),
        policyId = usage.PolicyId.Value,
        policyKey = usage.PolicyKey,
        policyVersion = usage.PolicyVersion,
        revision = usage.Revision
    };

    private static object ToResponse(UsageReconciliation result) => new
    {
        tenantId = result.TenantId.Value,
        drifted = result.Drifted,
        previousUsedBytes = result.PreviousUsedBytes,
        previousReservedBytes = result.PreviousReservedBytes,
        previousPolicyVersion = result.PreviousPolicyVersion,
        usedBytes = result.UsedBytes,
        reservedBytes = result.ReservedBytes,
        policyId = result.PolicyId.Value,
        policyVersion = result.PolicyVersion,
        reservationsReleased = result.ReservationsReleased
    };

    private static object ToTenantResponse(TenantQuotaStatus status) => new
    {
        limitBytes = status.LimitBytes,
        unlimited = status.IsUnlimited,
        usedBytes = status.UsedBytes,
        reservedBytes = status.ReservedBytes,
        remainingBytes = status.RemainingBytes,
        state = status.State.ToString(),
        warningThreshold = status.WarningThresholdPercent,
        criticalThreshold = status.CriticalThresholdPercent,
        accountingMode = status.AccountingMode.ToString(),
        subscriptionStatus = status.EntitlementStatus.ToString(),
        newBackupsAllowed = status.NewBackupsAllowed,
        denialCode = status.DenialCode,
        enforcementMode = status.EnforcementMode.ToString(),
        plan = PlanResponse(status.Plan),
        includedStorageScopes = ScopesResponse(status.IncludedStorageScopes),
        calculatedAt = status.UsageCalculatedAt
    };

    private static object ToPlatformResponse(TenantQuotaStatus status) => new
    {
        tenantId = status.TenantId.Value,
        limitBytes = status.LimitBytes,
        unlimited = status.IsUnlimited,
        usedBytes = status.UsedBytes,
        reservedBytes = status.ReservedBytes,
        remainingBytes = status.RemainingBytes,
        state = status.State.ToString(),
        warningThreshold = status.WarningThresholdPercent,
        criticalThreshold = status.CriticalThresholdPercent,
        accountingMode = status.AccountingMode.ToString(),
        enforcementMode = status.EnforcementMode.ToString(),
        subscriptionStatus = status.EntitlementStatus.ToString(),
        newBackupsAllowed = status.NewBackupsAllowed,
        denialCode = status.DenialCode,
        policy = new { policyId = status.PolicyId.Value, key = status.PolicyKey, version = status.PolicyVersion },
        plan = PlanResponse(status.Plan),
        includedStorageScopes = ScopesResponse(status.IncludedStorageScopes),
        calculatedAt = status.UsageCalculatedAt
    };

    private static object ToListItemResponse(TenantStorageUsageListItem item) => new
    {
        tenantId = item.TenantId.Value,
        tenantName = item.TenantName,
        plan = PlanResponse(item.Quota.Plan),
        subscriptionStatus = item.Quota.EntitlementStatus.ToString(),
        limitBytes = item.Quota.LimitBytes,
        unlimited = item.Quota.IsUnlimited,
        // No stored projection: the figures and the state do not exist yet, and are reported as absent rather than as a computed zero.
        usedBytes = item.HasUsage ? item.Quota.UsedBytes : (long?)null,
        reservedBytes = item.HasUsage ? item.Quota.ReservedBytes : (long?)null,
        remainingBytes = item.HasUsage ? item.Quota.RemainingBytes : null,
        state = item.HasUsage ? item.Quota.State.ToString() : null,
        warningThreshold = item.Quota.WarningThresholdPercent,
        criticalThreshold = item.Quota.CriticalThresholdPercent,
        accountingMode = item.Quota.AccountingMode.ToString(),
        enforcementMode = item.Quota.EnforcementMode.ToString(),
        newBackupsAllowed = item.Quota.NewBackupsAllowed,
        denialCode = item.Quota.DenialCode,
        policy = new { policyId = item.Quota.PolicyId.Value, key = item.Quota.PolicyKey, version = item.Quota.PolicyVersion },
        hasUsage = item.HasUsage,
        usageCurrent = item.UsageCurrent,
        calculatedAt = item.CalculatedAt
    };

    private static object? PlanResponse(TenantPlanSummary? plan) => plan is null
        ? null
        : new { name = plan.Name, isSystem = plan.IsSystem, systemKey = plan.SystemKey, subscriptionEndsAt = plan.SubscriptionEndsAt };

    private static object ScopesResponse(QuotaScopeRule scopes) => scopes.Selectors.Select(selector => selector.Kind == QuotaScopeKind.All
        ? (object)new { kind = QuotaScopeRule.KindName(selector.Kind) }
        : new { kind = QuotaScopeRule.KindName(selector.Kind), values = selector.Values }).ToList();

    private static bool TryEnum<T>(string? text, out T? value) where T : struct, Enum
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }
        // By name only: numeric text such as "1" is not a name.
        foreach (var name in Enum.GetNames<T>())
        {
            if (string.Equals(name, text.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                value = Enum.Parse<T>(name);
                return true;
            }
        }
        return false;
    }
}
