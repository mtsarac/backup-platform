using BackupPlatform.Api.Security;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Storage;
using Microsoft.AspNetCore.Authorization;

namespace BackupPlatform.Api.Endpoints;

public sealed class CreateStorageConnectionRequest
{
    public string? ProviderType { get; init; }
    public string? Name { get; init; }
    public string? Ownership { get; init; }
    public string? QuotaScope { get; init; }
    public Dictionary<string, string>? Configuration { get; init; }
    public Dictionary<string, string>? Secret { get; init; }
}

public sealed class UpdateStorageConnectionRequest
{
    public string? Name { get; init; }
    public string? QuotaScope { get; init; }
    public Dictionary<string, string>? Configuration { get; init; }
}

public sealed class StorageSecretRequest
{
    public Dictionary<string, string>? Values { get; init; }
}

/// <summary>
/// Storage connections for tenants and for the platform. Both route groups call the same service with a <see cref="StorageScope"/>; the scope, not the
/// route, decides what is allowed, and the tenant routes bind every lookup to the member's own tenant so another tenant's id is simply not found.
/// A response never contains a secret value: only whether credentials are stored, which fields, and when they were last changed.
/// </summary>
public static class StorageConnectionEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaximumPageSize = 100;

    public static IEndpointRouteBuilder MapStorageConnectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tenant = endpoints.MapGroup("/api/tenants/{tenantId:guid}").WithMetadata(new TenantScopedEndpoint());
        tenant.MapGet("/storage-providers", ListProviders).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapGet("/storage-connections", ListForTenantAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapGet("/storage-connections/{id:guid}", GetForTenantAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapPost("/storage-connections", CreateForTenantAsync).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        tenant.MapPut("/storage-connections/{id:guid}", UpdateForTenantAsync).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        tenant.MapPut("/storage-connections/{id:guid}/secret", SetSecretForTenantAsync).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        tenant.MapDelete("/storage-connections/{id:guid}/secret", ClearSecretForTenantAsync).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        tenant.MapPost("/storage-connections/{id:guid}/test", TestForTenantAsync).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        tenant.MapPost("/storage-connections/{id:guid}/enable", (Guid id, ITenantContext t, ICurrentUser u, StorageConnectionService s, CancellationToken c) =>
            ToggleAsync(Scope(t), id, true, u, s, c)).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        tenant.MapPost("/storage-connections/{id:guid}/disable", (Guid id, ITenantContext t, ICurrentUser u, StorageConnectionService s, CancellationToken c) =>
            ToggleAsync(Scope(t), id, false, u, s, c)).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        tenant.MapDelete("/storage-connections/{id:guid}", DeleteForTenantAsync).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);

        var platform = endpoints.MapGroup("/api/platform").RequireAuthorization(AuthorizationPolicies.PlatformAdmin);
        platform.MapGet("/storage-providers", ListProviders);
        platform.MapGet("/storage-connections", SearchAsync);
        platform.MapGet("/tenants/{tenantId:guid}/storage-connections", ListForPlatformAsync);
        platform.MapPost("/tenants/{tenantId:guid}/storage-connections", CreateForPlatformAsync);
        platform.MapGet("/storage-connections/{id:guid}", GetForPlatformAsync);
        platform.MapPut("/storage-connections/{id:guid}", UpdateForPlatformAsync);
        platform.MapPut("/storage-connections/{id:guid}/secret", SetSecretForPlatformAsync);
        platform.MapDelete("/storage-connections/{id:guid}/secret", ClearSecretForPlatformAsync);
        platform.MapPost("/storage-connections/{id:guid}/test", TestForPlatformAsync);
        platform.MapPost("/storage-connections/{id:guid}/enable", (Guid id, ICurrentUser u, StorageConnectionService s, CancellationToken c) =>
            ToggleAsync(StorageScope.Platform, id, true, u, s, c));
        platform.MapPost("/storage-connections/{id:guid}/disable", (Guid id, ICurrentUser u, StorageConnectionService s, CancellationToken c) =>
            ToggleAsync(StorageScope.Platform, id, false, u, s, c));
        platform.MapDelete("/storage-connections/{id:guid}", DeleteForPlatformAsync);
        return endpoints;
    }

    // ---- shared -------------------------------------------------------------------------------------------------------------------------------

    private static IResult ListProviders(StorageConnectionService service) => Results.Ok(new { items = service.Providers.Select(ToResponse).ToList() });

    private static StorageScope Scope(ITenantContext tenant) => StorageScope.Tenant(tenant.TenantId!, canManage: true);

    private static async Task<StorageScope> ReadScopeAsync(HttpContext context, ITenantContext tenant, IAuthorizationService authorization) =>
        StorageScope.Tenant(tenant.TenantId!, (await authorization.AuthorizeAsync(context.User, context, AuthorizationPolicies.TenantAdministrator)).Succeeded);

    private static async Task<IResult> ToggleAsync(StorageScope scope, Guid id, bool enabled, ICurrentUser user, StorageConnectionService service, CancellationToken cancellationToken) =>
        await GuardAsync(async () => Respond(await service.SetEnabledAsync(scope, id, enabled, user.UserId!, cancellationToken)));

    private static async Task<IResult> GuardAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (InvalidStorageConnectionException exception)
        {
            return Results.BadRequest(new { error = "invalid_request", field = exception.Field, code = exception.Code });
        }
    }

    private static IResult Respond(StorageResult<StorageConnectionView> result, bool created = false)
    {
        if (result.Succeeded)
        {
            return created ? Results.Json(ToResponse(result.Value!), statusCode: StatusCodes.Status201Created) : Results.Ok(ToResponse(result.Value!));
        }
        return Failure(result.Outcome, result.Code);
    }

    private static IResult Failure(StorageConnectionOutcome outcome, string? code) => outcome switch
    {
        StorageConnectionOutcome.NotFound => Results.NotFound(new { error = "storage_connection_not_found" }),
        StorageConnectionOutcome.TenantNotFound => Results.NotFound(new { error = "tenant_not_found" }),
        StorageConnectionOutcome.NameTaken => Results.Conflict(new { error = "storage_connection_name_taken" }),
        StorageConnectionOutcome.LimitReached => Results.Conflict(new { error = "connection_limit_reached" }),
        StorageConnectionOutcome.InUse => Results.Conflict(new { error = "connection_in_use" }),
        StorageConnectionOutcome.ConcurrentUpdate => Results.Conflict(new { error = "concurrent_update" }),
        StorageConnectionOutcome.Rejected when code == StorageRejections.PlatformManagedProvider => Results.Json(new { error = "forbidden", code }, statusCode: StatusCodes.Status403Forbidden),
        StorageConnectionOutcome.Rejected => Results.Conflict(new { error = code ?? "rejected" }),
        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError)
    };

    // ---- tenant -------------------------------------------------------------------------------------------------------------------------------

    private static async Task<IResult> ListForTenantAsync(HttpContext context, ITenantContext tenant, IAuthorizationService authorization, StorageConnectionService service, CancellationToken cancellationToken)
    {
        var scope = await ReadScopeAsync(context, tenant, authorization);
        var items = await service.ListAsync(scope, tenant.TenantId!, cancellationToken);
        return items is null ? Failure(StorageConnectionOutcome.TenantNotFound, null) : Results.Ok(new { items = items.Select(ToResponse).ToList() });
    }

    private static async Task<IResult> GetForTenantAsync(Guid id, HttpContext context, ITenantContext tenant, IAuthorizationService authorization, StorageConnectionService service, CancellationToken cancellationToken)
    {
        var view = await service.GetAsync(await ReadScopeAsync(context, tenant, authorization), id, cancellationToken);
        return view is null ? Failure(StorageConnectionOutcome.NotFound, null) : Results.Ok(ToResponse(view));
    }

    private static Task<IResult> CreateForTenantAsync(CreateStorageConnectionRequest? request, ITenantContext tenant, ICurrentUser user, StorageConnectionService service, CancellationToken cancellationToken) =>
        GuardAsync(async () => Respond(await service.CreateAsync(Scope(tenant), tenant.TenantId!, ToCommand(request), user.UserId!, cancellationToken), created: true));

    private static Task<IResult> UpdateForTenantAsync(Guid id, UpdateStorageConnectionRequest? request, ITenantContext tenant, ICurrentUser user, StorageConnectionService service, CancellationToken cancellationToken) =>
        GuardAsync(async () => Respond(await service.UpdateAsync(Scope(tenant), id, ToCommand(request), user.UserId!, cancellationToken)));

    private static Task<IResult> SetSecretForTenantAsync(Guid id, StorageSecretRequest? request, ITenantContext tenant, ICurrentUser user, StorageConnectionService service, CancellationToken cancellationToken) =>
        GuardAsync(async () => Respond(await service.SetSecretAsync(Scope(tenant), id, request?.Values, user.UserId!, cancellationToken)));

    private static Task<IResult> ClearSecretForTenantAsync(Guid id, ITenantContext tenant, ICurrentUser user, StorageConnectionService service, CancellationToken cancellationToken) =>
        GuardAsync(async () => Respond(await service.ClearSecretAsync(Scope(tenant), id, user.UserId!, cancellationToken)));

    private static Task<IResult> TestForTenantAsync(Guid id, ITenantContext tenant, ICurrentUser user, StorageConnectionService service, CancellationToken cancellationToken) =>
        GuardAsync(async () => RespondTest(await service.TestAsync(Scope(tenant), id, user.UserId!, cancellationToken)));

    private static async Task<IResult> DeleteForTenantAsync(Guid id, ITenantContext tenant, ICurrentUser user, StorageConnectionService service, CancellationToken cancellationToken) =>
        RespondDelete(await service.DeleteAsync(Scope(tenant), id, user.UserId!, cancellationToken));

    // ---- platform -----------------------------------------------------------------------------------------------------------------------------

    private static async Task<IResult> SearchAsync(
        string? ownership, string? providerType, Guid? tenantId, bool? enabled, string? search, int? page, int? pageSize, StorageConnectionService service, CancellationToken cancellationToken)
    {
        var p = page ?? 1;
        var size = pageSize ?? DefaultPageSize;
        if (p is < 1 or > 1_000_000 || size is < 1 or > MaximumPageSize)
        {
            return Results.BadRequest(new { error = "invalid_request", field = "page", code = "invalid_value" });
        }
        StorageOwnership? parsedOwnership = null;
        if (ownership is not null)
        {
            if (!Enum.TryParse<StorageOwnership>(ownership, ignoreCase: false, out var value) || !Enum.IsDefined(value))
            {
                return Results.BadRequest(new { error = "invalid_request", field = "ownership", code = "invalid_value" });
            }
            parsedOwnership = value;
        }
        var filter = new StorageConnectionFilter(parsedOwnership, providerType, tenantId is { } id ? new TenantId(id) : null, enabled, search);
        var result = await service.SearchAsync(filter, p, size, cancellationToken);
        return Results.Ok(new { items = result.Items.Select(ToResponse).ToList(), totalCount = result.TotalCount, page = result.Page, pageSize = result.PageSize });
    }

    private static async Task<IResult> ListForPlatformAsync(Guid tenantId, StorageConnectionService service, CancellationToken cancellationToken)
    {
        var items = await service.ListAsync(StorageScope.Platform, new TenantId(tenantId), cancellationToken);
        return items is null ? Failure(StorageConnectionOutcome.TenantNotFound, null) : Results.Ok(new { items = items.Select(ToResponse).ToList() });
    }

    private static Task<IResult> CreateForPlatformAsync(Guid tenantId, CreateStorageConnectionRequest? request, ICurrentUser user, StorageConnectionService service, CancellationToken cancellationToken) =>
        GuardAsync(async () => Respond(await service.CreateAsync(StorageScope.Platform, new TenantId(tenantId), ToCommand(request), user.UserId!, cancellationToken), created: true));

    private static async Task<IResult> GetForPlatformAsync(Guid id, StorageConnectionService service, CancellationToken cancellationToken)
    {
        var view = await service.GetAsync(StorageScope.Platform, id, cancellationToken);
        return view is null ? Failure(StorageConnectionOutcome.NotFound, null) : Results.Ok(ToResponse(view));
    }

    private static Task<IResult> UpdateForPlatformAsync(Guid id, UpdateStorageConnectionRequest? request, ICurrentUser user, StorageConnectionService service, CancellationToken cancellationToken) =>
        GuardAsync(async () => Respond(await service.UpdateAsync(StorageScope.Platform, id, ToCommand(request), user.UserId!, cancellationToken)));

    private static Task<IResult> SetSecretForPlatformAsync(Guid id, StorageSecretRequest? request, ICurrentUser user, StorageConnectionService service, CancellationToken cancellationToken) =>
        GuardAsync(async () => Respond(await service.SetSecretAsync(StorageScope.Platform, id, request?.Values, user.UserId!, cancellationToken)));

    private static Task<IResult> ClearSecretForPlatformAsync(Guid id, ICurrentUser user, StorageConnectionService service, CancellationToken cancellationToken) =>
        GuardAsync(async () => Respond(await service.ClearSecretAsync(StorageScope.Platform, id, user.UserId!, cancellationToken)));

    private static Task<IResult> TestForPlatformAsync(Guid id, ICurrentUser user, StorageConnectionService service, CancellationToken cancellationToken) =>
        GuardAsync(async () => RespondTest(await service.TestAsync(StorageScope.Platform, id, user.UserId!, cancellationToken)));

    private static async Task<IResult> DeleteForPlatformAsync(Guid id, ICurrentUser user, StorageConnectionService service, CancellationToken cancellationToken) =>
        RespondDelete(await service.DeleteAsync(StorageScope.Platform, id, user.UserId!, cancellationToken));

    // ---- mapping ------------------------------------------------------------------------------------------------------------------------------

    private static IResult RespondTest(StorageResult<StorageTestOutcome> result) => result.Value is { } outcome
        ? Results.Ok(new
        {
            connection = ToResponse(outcome.Connection),
            test = new { status = outcome.Test.Status.ToString(), code = outcome.Test.Code, message = outcome.Test.Message, testedAt = outcome.Test.TestedAt, durationMs = Math.Round(outcome.DurationMilliseconds, 1) }
        })
        : Failure(result.Outcome, result.Code);

    private static IResult RespondDelete(StorageResult<StorageConnectionView> result) =>
        result.Succeeded ? Results.NoContent() : Failure(result.Outcome, result.Code);

    private static CreateStorageConnectionCommand ToCommand(CreateStorageConnectionRequest? request) =>
        new(request?.ProviderType, request?.Name, request?.Ownership, request?.QuotaScope, request?.Configuration, request?.Secret);

    private static UpdateStorageConnectionCommand ToCommand(UpdateStorageConnectionRequest? request) =>
        new(request?.Name, request?.QuotaScope, request?.Configuration);

    private static object ToResponse(StorageProviderDescriptor descriptor) => new
    {
        providerType = descriptor.ProviderType,
        displayName = descriptor.DisplayName,
        availability = descriptor.Availability.ToString(),
        topology = descriptor.Topology?.ToString(),
        executionLocation = descriptor.ExecutionLocation?.ToString(),
        capabilities = descriptor.Capabilities,
        supportedOwnerships = descriptor.SupportedOwnerships.Select(ownership => ownership.ToString()).ToList(),
        tenantCreatable = descriptor.TenantCreatable,
        platformCreatable = descriptor.PlatformCreatable,
        maxConnectionsPerTenant = descriptor.MaxConnectionsPerTenant,
        configFields = descriptor.ConfigFields.Select(field => new { name = field.Name, label = field.Label, kind = field.Kind.ToString(), required = field.Required, maxLength = field.MaxLength }).ToList(),
        secretFields = descriptor.SecretFields.Select(field => new { name = field.Name, label = field.Label, required = field.Required }).ToList(),
        testMode = descriptor.TestMode.ToString()
    };

    private static object ToResponse(StorageConnectionView view) => new
    {
        id = view.Id,
        tenantId = view.TenantId,
        tenantName = view.TenantName,
        name = view.Name,
        providerType = view.ProviderType,
        providerDisplayName = view.ProviderDisplayName,
        availability = view.Availability.ToString(),
        ownership = view.Ownership.ToString(),
        quotaScope = view.QuotaScope,
        enabled = view.Enabled,
        isDefault = view.IsDefault,
        administrativelyDisabled = view.AdministrativelyDisabled,
        effectiveEnabled = view.EffectiveEnabled,
        capabilities = view.Capabilities,
        configuration = view.Configuration,
        hasSecret = view.HasSecret,
        secretFieldNames = view.SecretFieldNames,
        secretUpdatedAt = view.SecretUpdatedAt,
        lastTest = view.LastTest is null ? null : new { status = view.LastTest.Status.ToString(), code = view.LastTest.Code, message = view.LastTest.Message, testedAt = view.LastTest.TestedAt },
        replicaCount = view.ReplicaCount,
        storedBytes = view.StoredBytes,
        createdAt = view.CreatedAt,
        updatedAt = view.UpdatedAt,
        stateChangedAt = view.StateChangedAt,
        permissions = new
        {
            canEdit = view.Permissions.CanEdit,
            canSetQuotaScope = view.Permissions.CanSetQuotaScope,
            canToggle = view.Permissions.CanToggle,
            canDelete = view.Permissions.CanDelete,
            canManageSecret = view.Permissions.CanManageSecret,
            canTest = view.Permissions.CanTest
        }
    };
}
