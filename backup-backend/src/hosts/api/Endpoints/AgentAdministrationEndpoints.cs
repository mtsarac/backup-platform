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
    /// <summary>Optional token lifetime in minutes; the service default applies when omitted.</summary>
    public int? LifetimeMinutes { get; init; }
}

public sealed class SetAgentEnrollmentRequest
{
    public bool Enabled { get; init; }
}

/// <summary>
/// Browser-authenticated control-plane routes. Enrollment permission is an explicit platform operation under
/// <c>/api/platform</c>: it names the target tenant itself and relies on the PlatformAdmin policy, never on tenant
/// membership. Everything under <c>/api/tenants/{tenantId}</c> resolves and verifies membership first, then requires
/// the tenant administrator role for that same tenant.
/// </summary>
public static class AgentAdministrationEndpoints
{
    public static IEndpointRouteBuilder MapAgentAdministrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var platform = endpoints.MapGroup("/api/platform/tenants/{tenantId:guid}/agent-enrollment")
            .RequireAuthorization(AuthorizationPolicies.PlatformAdmin);
        platform.MapGet("", GetPolicyAsync);
        platform.MapPut("", SetPolicyAsync);

        var tenant = endpoints.MapGroup("/api/tenants/{tenantId:guid}")
            .WithMetadata(new TenantScopedEndpoint())
            .RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        tenant.MapPost("/agent-enrollment-tokens", CreateTokenAsync);
        tenant.MapGet("/agent-enrollment-tokens", ListTokensAsync);
        tenant.MapDelete("/agent-enrollment-tokens/{tokenId:guid}", RevokeTokenAsync);
        tenant.MapGet("/agents", ListAgentsAsync);
        tenant.MapPost("/agents/{agentId:guid}/revoke", RevokeAgentAsync);
        return endpoints;
    }

    private static async Task<IResult> GetPolicyAsync(Guid tenantId, EnrollmentAdministrationService service, CancellationToken cancellationToken)
    {
        var policy = await service.GetPolicyAsync(new TenantId(tenantId), cancellationToken);
        return policy is null
            ? Results.NotFound()
            : Results.Ok(new { tenantId = policy.TenantId.Value, enabled = policy.IsEnabled, changedAt = policy.ChangedAt });
    }

    private static async Task<IResult> SetPolicyAsync(
        Guid tenantId, SetAgentEnrollmentRequest request, ICurrentUser currentUser, EnrollmentAdministrationService service, CancellationToken cancellationToken)
    {
        var change = await service.SetEnrollmentEnabledAsync(new TenantId(tenantId), request.Enabled, currentUser.UserId!, cancellationToken);
        return change is null
            ? Results.NotFound()
            : Results.Ok(new { tenantId = change.TenantId.Value, enabled = change.IsEnabled, changed = change.Changed });
    }

    private static async Task<IResult> CreateTokenAsync(
        CreateEnrollmentTokenRequest? request, ITenantContext tenantContext, ICurrentUser currentUser,
        EnrollmentAdministrationService service, CancellationToken cancellationToken)
    {
        var lifetime = request?.LifetimeMinutes is { } minutes
            ? TimeSpan.FromMinutes(Math.Clamp(minutes, 0, (int)TimeSpan.FromDays(3650).TotalMinutes))
            : (TimeSpan?)null;
        var result = await service.CreateTokenAsync(tenantContext.TenantId!, currentUser.UserId!, lifetime, cancellationToken);
        return result.Status switch
        {
            CreateEnrollmentTokenStatus.Created => Results.Json(new
            {
                tokenId = result.Issued!.Token.Id,
                token = result.Issued.Plaintext,
                expiresAt = result.Issued.Token.ExpiresAt
            }, statusCode: StatusCodes.Status201Created),
            CreateEnrollmentTokenStatus.EnrollmentDisabled => Results.Json(
                new { error = "Agent enrollment is not enabled for this tenant." }, statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Json(new { error = "The requested token lifetime is outside the allowed range." }, statusCode: StatusCodes.Status400BadRequest)
        };
    }

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

    private static async Task<IResult> RevokeTokenAsync(
        Guid tokenId, ITenantContext tenantContext, ICurrentUser currentUser, EnrollmentAdministrationService service, CancellationToken cancellationToken)
    {
        var revoked = await service.RevokeTokenAsync(tenantContext.TenantId!, tokenId, currentUser.UserId!, cancellationToken);
        return revoked is null ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> ListAgentsAsync(ITenantContext tenantContext, AgentManagementService service, CancellationToken cancellationToken)
    {
        var agents = await service.ListAsync(tenantContext.TenantId!, cancellationToken);
        return Results.Ok(agents.Select(agent => new
        {
            agentId = agent.Id.Value,
            hostName = agent.HostName,
            status = agent.Status.ToString(),
            platform = agent.Software.Platform.ToString(),
            agentVersion = agent.Software.Version.ToString(),
            protocolVersion = $"{agent.Software.ProtocolVersion.Major}.{agent.Software.ProtocolVersion.Minor}",
            credentialGeneration = agent.CredentialGeneration
        }));
    }

    private static async Task<IResult> RevokeAgentAsync(
        Guid agentId, ITenantContext tenantContext, ICurrentUser currentUser, AgentManagementService service, CancellationToken cancellationToken)
    {
        var revoked = await service.RevokeAsync(tenantContext.TenantId!, new AgentId(agentId), currentUser.UserId!, cancellationToken);
        return revoked is null ? Results.NotFound() : Results.NoContent();
    }
}
