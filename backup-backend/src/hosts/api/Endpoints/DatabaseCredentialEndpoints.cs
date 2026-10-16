using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Api.Databases;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Contracts.Agents;
using Microsoft.AspNetCore.Mvc;

namespace BackupPlatform.Api.Endpoints;

/// <summary>
/// The only way a database login leaves the backend: an authenticated agent asks for a reference minted for itself. A reference of another agent or tenant
/// is indistinguishable from a missing one (404), so the answer reveals nothing about other agents' logins.
/// </summary>
public static class DatabaseCredentialEndpoints
{
    public static IEndpointRouteBuilder MapDatabaseCredentialEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup("/api/agent/database-credentials")
            .WithMetadata(new AgentApiEndpoint(), new RequiresAgentProtocol())
            .RequireAuthorization(AuthorizationPolicies.Agent)
            .MapPost("", ResolveAsync).WithMetadata(new RequestSizeLimitAttribute(1024));
        endpoints.MapGroup("/api/tenants/{tenantId:guid}/agents/{agentId:guid}/database-logins").WithMetadata(new TenantScopedEndpoint())
            .MapPost("", StoreAsync).RequireAuthorization(AuthorizationPolicies.TenantOperator).WithMetadata(new RequestSizeLimitAttribute(4096));
        return endpoints;
    }

    /// <summary>For forms that submit later (restore targets): the login is encrypted now and the form keeps only the returned reference, so a retried submit stays idempotent.</summary>
    private static async Task<IResult> StoreAsync(Guid agentId, DatabaseLoginInput? login, ITenantContext tenant, AgentConnectivityQueryService agents, DatabaseCredentialVault vault, CancellationToken cancellationToken)
    {
        if (await agents.GetAsync(tenant.TenantId!, new AgentId(agentId), cancellationToken) is null) return Results.NotFound();
        if (login is null) return Results.BadRequest(new { error = "invalid_request", field = "databaseLogin", code = "invalid_value" });
        try { return Results.Ok(new { reference = await vault.StoreAsync(agentId, login, cancellationToken) }); }
        catch (DatabaseLoginRejectedException) { return Results.BadRequest(new { error = "invalid_request", field = "databaseLogin", code = "invalid_value" }); }
    }

    private static async Task<IResult> ResolveAsync(DatabaseCredentialRequest? request, ICurrentAgent agent, DatabaseCredentialVault vault, CancellationToken cancellationToken)
    {
        var login = await vault.ResolveAsync(agent.Identity!.AgentId.Value, request?.Reference, cancellationToken);
        return login is null ? Results.NotFound(new AgentApiError { Error = "credential_unavailable" }) : Results.Ok(login);
    }
}
