using BackupPlatform.Domain.Audit;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Agents.Enrollment;
using BackupPlatform.Agents.Management;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Agents;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Api.Endpoints;

public sealed class CreateEnrollmentTokenRequest
{
}

public sealed class UpdatePlatformAgentSettingsRequest
{
    public string? ServerUrl { get; init; }
    public int EnrollmentTokenLifetimeMinutes { get; init; }
}

public sealed class SetAgentEnrollmentRequest
{
    public bool Enabled { get; init; }
}

/// <summary>
/// Browser-authenticated control-plane routes. Platform enrollment operations require PlatformAdmin independently
/// of membership. Tenant routes resolve membership first: safe agent reads require Viewer, while enrollment policy,
/// tokens and agent revocation require the administrator role for that same tenant.
/// </summary>
public static class AgentAdministrationEndpoints
{
    public static IEndpointRouteBuilder MapAgentAdministrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var platform = endpoints.MapGroup("/api/platform/tenants/{tenantId:guid}/agent-enrollment")
            .RequireAuthorization(AuthorizationPolicies.PlatformAdmin);
        platform.MapGet("", GetPolicyAsync);
        platform.MapPut("", SetPlatformPolicyAsync);

        var settings = endpoints.MapGroup("/api/platform/agent-settings")
            .RequireAuthorization(AuthorizationPolicies.PlatformAdmin);
        settings.MapGet("", GetAgentSettingsAsync);
        settings.MapPut("", UpdateAgentSettingsAsync);

        var tenant = endpoints.MapGroup("/api/tenants/{tenantId:guid}")
            .WithMetadata(new TenantScopedEndpoint());
        tenant.MapGet("/agent-enrollment", GetPolicyAsync).RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapPut("/agent-enrollment", SetTenantPolicyAsync).RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapPost("/agent-enrollment-tokens", CreateTokenAsync).RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapGet("/agent-enrollment-tokens", ListTokensAsync).RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapGet("/agent-enrollment-tokens/{tokenId:guid}", GetTokenStatusAsync).RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapDelete("/agent-enrollment-tokens/{tokenId:guid}", RevokeTokenAsync).RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapGet("/agents", ListAgentsAsync).RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapGet("/agents/connectivity-summary", GetConnectivitySummaryAsync).RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapGet("/agents/{agentId:guid}", GetAgentAsync).RequireAuthorization(AuthorizationPolicies.TenantAccess);
        tenant.MapPost("/agents/{agentId:guid}/revoke", RevokeAgentAsync).RequireAuthorization(AuthorizationPolicies.TenantAccess);
        return endpoints;
    }

    private static async Task<IResult> GetPolicyAsync(Guid tenantId, EnrollmentAdministrationService service, CancellationToken cancellationToken)
    {
        var policy = await service.GetPolicyAsync(new TenantId(tenantId), cancellationToken);
        return policy is null
            ? Results.NotFound()
            : Results.Ok(new { tenantId = policy.TenantId.Value, enabled = policy.IsEnabled, changedAt = policy.ChangedAt });
    }

    private static Task<IResult> SetPlatformPolicyAsync(
        Guid tenantId, SetAgentEnrollmentRequest request, ICurrentUser currentUser, EnrollmentAdministrationService service, CancellationToken cancellationToken) =>
        SetPolicyAsync(tenantId, request, currentUser, service, AuditActorKind.PlatformAdmin, cancellationToken);

    private static Task<IResult> SetTenantPolicyAsync(
        Guid tenantId, SetAgentEnrollmentRequest request, ICurrentUser currentUser, EnrollmentAdministrationService service, CancellationToken cancellationToken) =>
        SetPolicyAsync(tenantId, request, currentUser, service, AuditActorKind.UserAdmin, cancellationToken);

    private static async Task<IResult> SetPolicyAsync(
        Guid tenantId, SetAgentEnrollmentRequest request, ICurrentUser currentUser, EnrollmentAdministrationService service,
        AuditActorKind actorKind, CancellationToken cancellationToken)
    {
        var change = await service.SetEnrollmentEnabledAsync(new TenantId(tenantId), request.Enabled, currentUser.UserId!, actorKind, cancellationToken);
        return change is null
            ? Results.NotFound()
            : Results.Ok(new { tenantId = change.TenantId.Value, enabled = change.IsEnabled, changed = change.Changed });
    }

    private static async Task<IResult> CreateTokenAsync(
        ITenantContext tenantContext, ICurrentUser currentUser,
        EnrollmentAdministrationService service, CancellationToken cancellationToken)
    {
        var result = await service.CreateTokenAsync(tenantContext.TenantId!, currentUser.UserId!, cancellationToken);
        return result.Status switch
        {
            CreateEnrollmentTokenStatus.Created => Results.Json(new
            {
                tokenId = result.Issued!.Token.Id,
                token = result.Issued.Plaintext,
                expiresAt = result.Issued.Token.ExpiresAt
            }, statusCode: StatusCodes.Status201Created),
            CreateEnrollmentTokenStatus.EnrollmentDisabled => Results.Json(
                new { error = "enrollment_disabled", message = "Agent enrollment is not enabled for this tenant." }, statusCode: StatusCodes.Status403Forbidden),
            _ => Results.BadRequest()
        };
    }

    private static async Task<IResult> GetAgentSettingsAsync(PlatformAgentSettingsService service, CancellationToken cancellationToken)
    {
        var view = await service.GetAsync(cancellationToken);
        return Results.Ok(ToAgentSettingsResponse(view));
    }

    private static async Task<IResult> UpdateAgentSettingsAsync(
        UpdatePlatformAgentSettingsRequest? request, ICurrentUser currentUser, PlatformAgentSettingsService service, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "invalid_request" });
        }
        var result = await service.SaveAsync(request.ServerUrl, request.EnrollmentTokenLifetimeMinutes, currentUser.UserId!, cancellationToken);
        return result.Status switch
        {
            SavePlatformAgentSettingsStatus.Saved => Results.Ok(ToAgentSettingsResponse(result.Settings!)),
            SavePlatformAgentSettingsStatus.InvalidServerUrl => Results.BadRequest(new { error = "invalid_server_url" }),
            SavePlatformAgentSettingsStatus.InvalidLifetime => Results.BadRequest(new { error = "invalid_lifetime" }),
            _ => Results.BadRequest(new { error = "invalid_request" })
        };
    }

    private static object ToAgentSettingsResponse(PlatformAgentSettingsView view) => new
    {
        serverUrl = view.ServerUrl,
        enrollmentTokenLifetimeMinutes = view.EnrollmentTokenLifetimeMinutes,
        limits = new
        {
            minMinutes = view.MinimumMinutes,
            maxMinutes = view.MaximumMinutes
        },
        updatedAt = view.UpdatedAt
    };

    private static async Task<IResult> ListTokensAsync(ITenantContext tenantContext, EnrollmentAdministrationService service, CancellationToken cancellationToken)
    {
        var tokens = await service.ListActiveTokensAsync(tenantContext.TenantId!, cancellationToken);
        return Results.Ok(tokens.Select(token => new
        {
            tokenId = token.Id,
            createdAt = token.CreatedAt,
            expiresAt = token.ExpiresAt,
            createdBy = token.CreatedBy.Value
        }));
    }

    private static async Task<IResult> GetTokenStatusAsync(
        Guid tokenId, ITenantContext tenantContext, EnrollmentAdministrationService service, CancellationToken cancellationToken)
    {
        var status = await service.GetTokenStatusAsync(tenantContext.TenantId!, tokenId, cancellationToken);
        return status is null ? Results.NotFound() : Results.Ok(new
        {
            tokenId = status.TokenId,
            expiresAt = status.ExpiresAt,
            state = status.State.ToString(),
            agentId = status.AgentId?.Value
        });
    }

    private static async Task<IResult> RevokeTokenAsync(
        Guid tokenId, ITenantContext tenantContext, ICurrentUser currentUser, EnrollmentAdministrationService service, CancellationToken cancellationToken)
    {
        var revoked = await service.RevokeTokenAsync(tenantContext.TenantId!, tokenId, currentUser.UserId!, cancellationToken);
        return revoked is null ? Results.NotFound() : Results.NoContent();
    }

    /// <summary>Revoked agents are history, not choices: they are listed only when <paramref name="includeRevoked"/> is set. Every other reader keeps resolving them by id.</summary>
    private static async Task<IResult> ListAgentsAsync(
        string? connectivity, bool? includeRevoked, ITenantContext tenantContext, AgentConnectivityQueryService service, CancellationToken cancellationToken)
    {
        if (!TryParseFilter(connectivity, out var filter))
        {
            return Results.Json(new { error = "connectivity must be online, offline or unknown." }, statusCode: StatusCodes.Status400BadRequest);
        }
        var agents = await service.ListAsync(tenantContext.TenantId!, filter, cancellationToken);
        return Results.Ok(agents.Where(agent => includeRevoked == true || agent.Agent.Status != AgentStatus.Revoked).Select(ToAgentResponse));
    }

    private static async Task<IResult> GetAgentAsync(
        Guid agentId, ITenantContext tenantContext, AgentConnectivityQueryService service, CancellationToken cancellationToken)
    {
        var agent = await service.GetAsync(tenantContext.TenantId!, new AgentId(agentId), cancellationToken);
        return agent is null ? Results.NotFound() : Results.Ok(ToAgentResponse(agent));
    }

    private static object ToAgentResponse(AgentStatusView view) => new
    {
        agentId = view.Agent.Id.Value,
        hostName = view.Agent.HostName,
        status = view.Agent.Status.ToString(),
        connectivity = view.Connectivity.ToString().ToLowerInvariant(),
        operationalStatus = view.Status.ToString(),
        lastSeenAt = view.LastSeenAt,
        lastHeartbeatAt = view.LastHeartbeatAt,
        lastConnectedAt = view.LastConnectedAt,
        platform = view.Agent.Software.Platform.ToString(),
        agentVersion = view.ReportedAgentVersion,
        protocolVersion = view.ReportedProtocolVersion,
        capabilityRevision = view.CapabilityRevision,
        osDescription = view.OsDescription,
        runtimeVersion = view.RuntimeVersion,
        enrolledAt = view.EnrolledAt,
        capabilitiesRevision = view.Capabilities?.Revision,
        capabilitiesReportedAt = view.Capabilities?.ReportedAt,
        capabilities = (view.Capabilities?.Capabilities ?? []).Select(capability => new
        {
            identifier = capability.Identifier,
            support = capability.Support.ToString(),
            version = (string?)null,
            metadata = capability.Metadata
        })
    };

    private static async Task<IResult> GetConnectivitySummaryAsync(
        ITenantContext tenantContext, AgentConnectivityQueryService service, CancellationToken cancellationToken)
    {
        var summary = await service.SummarizeAsync(tenantContext.TenantId!, cancellationToken);
        return Results.Ok(new
        {
            online = summary.Online,
            offline = summary.Offline,
            unknown = summary.Unknown,
            disabled = summary.Disabled,
            revoked = summary.Revoked,
            total = summary.Total
        });
    }

    private static bool TryParseFilter(string? value, out AgentConnectivityFilter filter)
    {
        filter = AgentConnectivityFilter.All;
        return string.IsNullOrEmpty(value)
            || (Enum.TryParse(value, ignoreCase: true, out filter) && filter != AgentConnectivityFilter.All && Enum.IsDefined(filter));
    }

    private static async Task<IResult> RevokeAgentAsync(
        Guid agentId, ITenantContext tenantContext, ICurrentUser currentUser, AgentManagementService service, CancellationToken cancellationToken)
    {
        var revoked = await service.RevokeAsync(tenantContext.TenantId!, new AgentId(agentId), currentUser.UserId!, cancellationToken);
        return revoked is null ? Results.NotFound() : Results.NoContent();
    }
}
