using BackupPlatform.Api.Databases;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Discovery;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Discovery;
using Microsoft.AspNetCore.Mvc;

namespace BackupPlatform.Api.Endpoints;

/// <summary>
/// Database discovery inventory: the claim/report calls of the agent and the tenant-facing views and requests. Discovery is read-only with
/// respect to jobs: nothing here creates, changes or runs a backup job.
/// </summary>
public static class DiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapDiscoveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var agent = endpoints.MapGroup("/api/agent/discovery/scans")
            .WithMetadata(new AgentApiEndpoint(), new RequiresAgentProtocol())
            .RequireAuthorization(AuthorizationPolicies.Agent)
            .RequireRateLimiting(AgentRateLimitOptions.Policies.Discovery);
        agent.MapPost("", ClaimAsync).WithMetadata(new RequestSizeLimitAttribute(1024));
        agent.MapPut("{scanId:guid}", ReportAsync).WithMetadata(new RequestSizeLimitAttribute(DiscoveryProtocol.MaximumReportBytes));

        var tenant = endpoints.MapGroup("/api/tenants/{tenantId:guid}/discovery").WithMetadata(new TenantScopedEndpoint());
        tenant.MapGet("/agents", ListAgentsAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapGet("/instances", ListInstancesAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapGet("/instances/{instanceId:guid}", GetInstanceAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        tenant.MapPost("/agents/{agentId:guid}/rescan", RescanAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        tenant.MapPost("/instances/{instanceId:guid}/verify", VerifyAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        tenant.MapPut("/instances/{instanceId:guid}/credential", BindCredentialAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator);
        tenant.MapPut("/agents/{agentId:guid}/settings", ChangeSettingsAsync).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        return endpoints;
    }

    private static async Task<IResult> ClaimAsync(ICurrentAgent currentAgent, DiscoveryService service, CancellationToken cancellationToken)
    {
        var plan = await service.ClaimAsync(currentAgent.Identity!, cancellationToken);
        return plan is null ? Results.NoContent() : Results.Ok(plan);
    }

    private static async Task<IResult> ReportAsync(
        Guid scanId, AgentDiscoveryReport? report, ICurrentAgent currentAgent, DiscoveryService service, CancellationToken cancellationToken)
    {
        var result = await service.ReportAsync(currentAgent.Identity!, scanId, report, cancellationToken);
        return result.Status switch
        {
            ReportStatus.Applied => Results.NoContent(),
            ReportStatus.Invalid => Results.Json(new AgentApiError { Error = AgentErrorCodes.InvalidRequest, Message = $"Invalid field: {result.InvalidField}." }, statusCode: StatusCodes.Status400BadRequest),
            ReportStatus.Superseded => Results.Json(new AgentApiError { Error = "scan_superseded" }, statusCode: StatusCodes.Status409Conflict),
            _ => Results.Json(new AgentApiError { Error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized)
        };
    }

    private static async Task<IResult> ListAgentsAsync(ITenantContext tenant, DiscoveryService service, CancellationToken cancellationToken) =>
        Results.Ok(new { items = await service.ListAgentsAsync(tenant.TenantId!, cancellationToken) });

    private static async Task<IResult> ListInstancesAsync(
        Guid? agentId, string? engine, bool? includeMissing, ITenantContext tenant, DiscoveryService service, CancellationToken cancellationToken)
    {
        if (engine is not null && engine is not (DiscoveryEngines.MsSql or DiscoveryEngines.PostgreSql))
        {
            return Invalid("engine", "invalid_value", "Unknown database engine.");
        }
        return Results.Ok(new { items = await service.ListInstancesAsync(tenant.TenantId!, agentId, engine, includeMissing ?? false, cancellationToken) });
    }

    private static async Task<IResult> GetInstanceAsync(Guid instanceId, ITenantContext tenant, DiscoveryService service, CancellationToken cancellationToken)
    {
        var detail = await service.GetInstanceAsync(tenant.TenantId!, instanceId, cancellationToken);
        return detail is null ? Results.NotFound() : Results.Ok(ToWire(detail));
    }

    private static async Task<IResult> RescanAsync(
        Guid agentId, HttpContext context, ITenantContext tenant, ICurrentUser user, DiscoveryService service, CancellationToken cancellationToken) =>
        ToResult(context, await service.RescanAsync(tenant.TenantId!, agentId, user.UserId!, cancellationToken));

    private static async Task<IResult> VerifyAsync(
        Guid instanceId, HttpContext context, ITenantContext tenant, ICurrentUser user, DiscoveryService service, CancellationToken cancellationToken) =>
        ToResult(context, await service.VerifyAsync(tenant.TenantId!, instanceId, user.UserId!, cancellationToken));

    private static async Task<IResult> BindCredentialAsync(
        Guid instanceId, DiscoveryCredentialRequest request, ITenantContext tenant, ICurrentUser user, DiscoveryService service, DatabaseCredentialVault vault,
        CancellationToken cancellationToken)
    {
        if (await service.GetInstanceAsync(tenant.TenantId!, instanceId, cancellationToken) is not { } detail) return Results.NotFound();
        try
        {
            // The typed login (or a reference of this instance's agent) is the only accepted source; free text is never a reference.
            request = request with
            {
                CredentialReference = await vault.PrepareAsync(detail.Instance.AgentId, request.IntegratedSecurity, request.DatabaseLogin, request.CredentialReference, cancellationToken),
                DatabaseLogin = null
            };
        }
        catch (DatabaseLoginRejectedException)
        {
            return Invalid("databaseLogin", "invalid_value", "A user name and password, or a login already stored for this agent, is required.");
        }
        var outcome = await service.BindCredentialAsync(tenant.TenantId!, instanceId, request, user.UserId!, cancellationToken);
        return outcome.Status switch
        {
            BindStatus.Bound => Results.Ok(outcome.Instance),
            BindStatus.NotFound => Results.NotFound(),
            _ => Invalid("credentialReference", "invalid_value", "The credential reference or authentication mode is invalid for this instance.")
        };
    }

    private static async Task<IResult> ChangeSettingsAsync(
        Guid agentId, DiscoverySettingsRequest request, ITenantContext tenant, ICurrentUser user, DiscoveryService service, CancellationToken cancellationToken)
    {
        try
        {
            var status = await service.ChangeSettingsAsync(tenant.TenantId!, agentId, request, user.UserId!, cancellationToken);
            return status is null ? Results.NotFound() : Results.Ok(status);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Invalid("scanIntervalMinutes", "out_of_range", "The scan interval is outside the supported range.");
        }
    }

    private static IResult ToResult(HttpContext context, RescanOutcome outcome)
    {
        switch (outcome.Status)
        {
            case RescanStatus.Requested:
                return Results.Json(outcome.Agent, statusCode: StatusCodes.Status202Accepted);
            case RescanStatus.NotFound:
                return Results.NotFound();
            case RescanStatus.TooSoon:
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling(outcome.RetryAfter!.Value.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.Json(new { error = "rescan_too_soon" }, statusCode: StatusCodes.Status429TooManyRequests);
            default:
                var code = outcome.Status switch
                {
                    RescanStatus.Unsupported => "discovery_unsupported",
                    RescanStatus.Disabled => "discovery_disabled",
                    _ => "credentials_required"
                };
                return Results.Json(new { error = code }, statusCode: StatusCodes.Status409Conflict);
        }
    }

    // The detail document is the instance summary with its databases beside it, flat, as the UI consumes it.
    private static object ToWire(DiscoveryInstanceDetail detail)
    {
        var summary = detail.Instance;
        return new
        {
            summary.Id, summary.AgentId, summary.InstanceKey, summary.Engine, summary.Source, summary.DisplayName, summary.InstanceName, summary.Host, summary.Port,
            summary.Version, summary.Edition, summary.HostPlatform, summary.ContainerName, summary.ContainerImage, summary.Presence, summary.Reachability,
            summary.Access, summary.AccessReasonCode, summary.FullBackup, summary.FullBackupReasonCode, summary.Credential, summary.FirstSeenAt, summary.LastSeenAt,
            summary.LastChange, summary.DatabasesCheckedAt, summary.DatabaseCounts, databases = detail.Databases
        };
    }

    private static IResult Invalid(string field, string code, string message) =>
        Results.BadRequest(new { error = "invalid_request", field, code, message });
}
