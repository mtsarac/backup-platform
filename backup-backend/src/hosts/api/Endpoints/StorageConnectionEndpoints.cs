using BackupPlatform.Api.Security;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Storage;
using BackupPlatform.Tenancy;
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
        tenant.MapGet("/storage-connections/{id:guid}/capacity", GetCapacityForTenantAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapPost("/storage-connections/{id:guid}/probe", ProbeForTenantAsync).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        tenant.MapPost("/storage-connections/{id:guid}/enable", (Guid id, ITenantContext t, ICurrentUser u, StorageConnectionService s, CancellationToken c) =>
            ToggleAsync(Scope(t), id, true, u, s, c)).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        tenant.MapPost("/storage-connections/{id:guid}/disable", (Guid id, ITenantContext t, ICurrentUser u, StorageConnectionService s, CancellationToken c) =>
            ToggleAsync(Scope(t), id, false, u, s, c)).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        tenant.MapDelete("/storage-connections/{id:guid}", DeleteForTenantAsync).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        tenant.MapPost("/storage-connections/{id:guid}/google-drive/authorize-url", GoogleDriveAuthorizeAsync).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        tenant.MapPost("/storage-connections/{id:guid}/google-drive/disconnect", GoogleDriveDisconnectAsync).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        tenant.MapPost("/storage-connections/{id:guid}/google-drive/reconcile", GoogleDriveReconcileAsync).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        tenant.MapGet("/storage-connections/google-drive/callback", GoogleDriveOAuthCallbackAsync).AllowAnonymous();

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
        platform.MapGet("/storage-connections/{id:guid}/capacity", GetCapacityForPlatformAsync);
        platform.MapPost("/storage-connections/{id:guid}/probe", ProbeForPlatformAsync);
        platform.MapPost("/storage-connections/{id:guid}/enable", (Guid id, ICurrentUser u, StorageConnectionService s, CancellationToken c) =>
            ToggleAsync(StorageScope.Platform, id, true, u, s, c));
        platform.MapPost("/storage-connections/{id:guid}/disable", (Guid id, ICurrentUser u, StorageConnectionService s, CancellationToken c) =>
            ToggleAsync(StorageScope.Platform, id, false, u, s, c));
        platform.MapDelete("/storage-connections/{id:guid}", DeleteForPlatformAsync);
        endpoints.MapGet("/api/platform/google-drive/oauth/callback", GoogleDriveOAuthCallbackAsync).AllowAnonymous();

        var v1 = endpoints.MapGroup("/api/v1/storage/connections/{id:guid}");
        v1.MapGet("/capacity", GetCapacityV1Async).RequireAuthorization();
        v1.MapPost("/probe", ProbeV1Async).RequireAuthorization();
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

    private static async Task<IResult> GoogleDriveAuthorizeAsync(
        Guid id, ITenantContext tenant, ICurrentUser user,
        BackupPlatform.Api.Backups.GoogleDriveOAuthService oauth, CancellationToken cancellationToken)
    {
        try
        {
            var url = await oauth.CreateAuthorizationUrlAsync(tenant.TenantId!, id, user.UserId!.Value, cancellationToken);
            return Results.Ok(new { authorizationUrl = url });
        }
        catch (InvalidOperationException)
        {
            // Unknown / disabled connection, or the platform OAuth client is not configured: nothing about which is revealed.
            return Results.Conflict(new { error = "google_drive_authorization_unavailable" });
        }
    }

    private static async Task<IResult> GoogleDriveDisconnectAsync(
        Guid id, ITenantContext tenant, ICurrentUser user,
        BackupPlatform.Api.Backups.GoogleDriveOAuthService oauth, CancellationToken cancellationToken)
    {
        try
        {
            await oauth.DisconnectAsync(tenant.TenantId!, id, user.UserId!.Value, cancellationToken);
            return Results.Ok(new { disconnected = true });
        }
        catch (InvalidOperationException)
        {
            return Failure(StorageConnectionOutcome.NotFound, null);
        }
    }

    private static async Task<IResult> GoogleDriveReconcileAsync(
        Guid id, ITenantContext tenant, IStorageConnectionStore store,
        BackupPlatform.SecretProviders.Abstractions.ISecretProvider secrets,
        BackupPlatform.Api.Backups.GoogleDriveConnectionProviderFactory factory, CancellationToken cancellationToken)
    {
        var record = await store.FindAsync(id, tenant.TenantId, cancellationToken);
        if (record is null || record.Connection.TenantId != tenant.TenantId
            || record.Connection.ProviderType != StorageProviderTypes.GoogleDrive)
            return Results.NotFound();
        try
        {
            var provider = (BackupPlatform.StorageProviders.GoogleDrive.GoogleDriveStorageProvider)factory.Create(record.Connection, secrets);
            return Results.Ok(await provider.ReconcileAsync(cancellationToken));
        }
        catch (BackupPlatform.StorageProviders.Abstractions.StorageException exception)
        {
            return Results.UnprocessableEntity(new { error = exception.Kind.ToString() });
        }
    }

    private static async Task<IResult> GoogleDriveOAuthCallbackAsync(
        HttpRequest request,
        BackupPlatform.Api.Backups.GoogleDriveOAuthService oauth,
        Microsoft.Extensions.Options.IOptions<BackupPlatform.Api.Backups.GoogleDrivePlatformOptions> options,
        CancellationToken cancellationToken)
    {
        var code = request.Query["code"].ToString();
        var state = request.Query["state"].ToString();
        var error = request.Query["error"].ToString();
        var result = await oauth.ProcessCallbackAsync(code, state, string.IsNullOrEmpty(error) ? null : error, cancellationToken);

        // Only server-fixed text and a server-generated GUID reach the page: nothing from the query string is echoed.
        var html = $$"""
            <!DOCTYPE html>
            <html>
            <head><title>Google Drive Authorization</title></head>
            <body style="font-family:sans-serif;text-align:center;padding:50px;">
            <script>
                if (window.opener) {
                    window.opener.postMessage({
                        type: 'GOOGLE_DRIVE_OAUTH_COMPLETED',
                        success: {{(result.Success ? "true" : "false")}},
                        connectionId: '{{(result.ConnectionId?.ToString("D") ?? "")}}'
                    }, {{MessageTargetOriginScript(options.Value.FrontendOrigin)}});
                    window.close();
                } else {
                    window.location.href = '/';
                }
            </script>
            <h2>{{(result.Success ? "Google Drive bağlantısı başarılı!" : "Bağlantı başarısız")}}</h2>
            <p>{{(result.Success ? "Pencereyi kapatabilirsiniz." : "Google Drive yetkilendirmesi tamamlanamadı. Lütfen tekrar deneyin.")}}</p>
            </body>
            </html>
            """;

        return Results.Content(html, "text/html", System.Text.Encoding.UTF8);
    }

    /// <summary>
    /// The popup is opened by the frontend, which is not the API origin when the two run on different ports. Only the explicitly configured
    /// <c>GoogleDrive:FrontendOrigin</c> (a bare http/https origin) is ever a target; unset or malformed means same-origin delivery. Never a wildcard.
    /// </summary>
    private static string MessageTargetOriginScript(string? configured) =>
        Uri.TryCreate(configured, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? System.Text.Json.JsonSerializer.Serialize(uri.GetLeftPart(UriPartial.Authority))
            : "window.location.origin";

    private static async Task<IResult> GetCapacityForTenantAsync(
        Guid id, ITenantContext tenant, StorageCapacityProbeService service, CancellationToken cancellationToken)
    {
        var capacity = await service.GetCapacityAsync(tenant.TenantId!, id, cancellationToken);
        return capacity is null ? Results.NotFound(new { error = "capacity_not_found" }) : Results.Ok(capacity);
    }

    private static async Task<IResult> ProbeForTenantAsync(
        Guid id, ITenantContext tenant, StorageCapacityProbeService service, CancellationToken cancellationToken)
    {
        var capacity = await service.ProbeAsync(tenant.TenantId!, id, cancellationToken);
        return capacity is null ? Results.NotFound(new { error = "storage_connection_not_found" }) : Results.Ok(capacity);
    }

    private static async Task<IResult> GetCapacityForPlatformAsync(
        Guid id, IStorageConnectionStore store, StorageCapacityProbeService service, CancellationToken cancellationToken)
    {
        var record = await store.FindAsync(id, null, cancellationToken);
        if (record is null) return Results.NotFound(new { error = "storage_connection_not_found" });
        var capacity = await service.GetCapacityAsync(record.Connection.TenantId, id, cancellationToken);
        return capacity is null ? Results.NotFound(new { error = "capacity_not_found" }) : Results.Ok(capacity);
    }

    private static async Task<IResult> ProbeForPlatformAsync(
        Guid id, IStorageConnectionStore store, StorageCapacityProbeService service, CancellationToken cancellationToken)
    {
        var record = await store.FindAsync(id, null, cancellationToken);
        if (record is null) return Results.NotFound(new { error = "storage_connection_not_found" });
        var capacity = await service.ProbeAsync(record.Connection.TenantId, id, cancellationToken);
        return capacity is null ? Results.NotFound(new { error = "storage_connection_not_found" }) : Results.Ok(capacity);
    }

    private static async Task<IResult> GetCapacityV1Async(
        Guid id, ICurrentUser user, TenantResolutionService resolution, IUserAccessStore userAccess,
        IStorageConnectionStore store, StorageCapacityProbeService service, CancellationToken cancellationToken)
    {
        if (!user.IsAuthenticated || user.UserId is not { } userId) return Results.Unauthorized();
        var record = await store.FindAsync(id, null, cancellationToken);
        if (record is null) return Results.NotFound(new { error = "storage_connection_not_found" });

        var isPlatformAdmin = await userAccess.IsPlatformAdminAsync(userId, cancellationToken);
        var membership = isPlatformAdmin ? null : await resolution.ResolveAsync(record.Connection.TenantId, cancellationToken);
        if (!isPlatformAdmin && membership is null)
        {
            return Results.NotFound(new { error = "storage_connection_not_found" });
        }

        var capacity = await service.GetCapacityAsync(record.Connection.TenantId, id, cancellationToken);
        return capacity is null ? Results.NotFound(new { error = "capacity_not_found" }) : Results.Ok(capacity);
    }

    private static async Task<IResult> ProbeV1Async(
        Guid id, ICurrentUser user, TenantResolutionService resolution, IUserAccessStore userAccess,
        IStorageConnectionStore store, StorageCapacityProbeService service, CancellationToken cancellationToken)
    {
        if (!user.IsAuthenticated || user.UserId is not { } userId) return Results.Unauthorized();
        var record = await store.FindAsync(id, null, cancellationToken);
        if (record is null) return Results.NotFound(new { error = "storage_connection_not_found" });

        var isPlatformAdmin = await userAccess.IsPlatformAdminAsync(userId, cancellationToken);
        var membership = isPlatformAdmin ? null : await resolution.ResolveAsync(record.Connection.TenantId, cancellationToken);
        if (!isPlatformAdmin && (membership is null || (membership.Role != BackupPlatform.Domain.Tenancy.TenantRole.Owner && membership.Role != BackupPlatform.Domain.Tenancy.TenantRole.Admin)))
        {
            return Results.NotFound(new { error = "storage_connection_not_found" });
        }

        var capacity = await service.ProbeAsync(record.Connection.TenantId, id, cancellationToken);
        return capacity is null ? Results.NotFound(new { error = "storage_connection_not_found" }) : Results.Ok(capacity);
    }
}
