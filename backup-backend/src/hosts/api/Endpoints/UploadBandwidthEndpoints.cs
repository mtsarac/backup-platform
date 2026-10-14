using BackupPlatform.Api.Security;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Identity;
using BackupPlatform.Application.Tenancy;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using Microsoft.AspNetCore.Mvc;

namespace BackupPlatform.Api.Endpoints;

/// <summary>The body of a limit change: null removes the limit (unlimited).</summary>
public sealed record UploadBandwidthLimitRequest(long? BytesPerSecond);

/// <summary>Tenant-facing management of upload bandwidth limits, and the lease-fenced call an agent makes to learn the limits it must obey.</summary>
public static class UploadBandwidthEndpoints
{
    public static IEndpointRouteBuilder MapUploadBandwidthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var limits = endpoints.MapGroup("/api/tenants/{tenantId:guid}/upload-bandwidth").WithMetadata(new TenantScopedEndpoint());
        limits.MapGet("", GetAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        limits.MapPut("/tenant", (UploadBandwidthLimitRequest request, ITenantContext tenant, ICurrentUser user, UploadBandwidthService service, CancellationToken cancellationToken) =>
            SetAsync(UploadBandwidthScope.Tenant, tenant.TenantId!.Value, request, tenant, user, service, cancellationToken)).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        limits.MapPut("/agents/{agentId:guid}", (Guid agentId, UploadBandwidthLimitRequest request, ITenantContext tenant, ICurrentUser user, UploadBandwidthService service, CancellationToken cancellationToken) =>
            SetAsync(UploadBandwidthScope.Agent, agentId, request, tenant, user, service, cancellationToken)).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        limits.MapPut("/jobs/{jobId:guid}", (Guid jobId, UploadBandwidthLimitRequest request, ITenantContext tenant, ICurrentUser user, UploadBandwidthService service, CancellationToken cancellationToken) =>
            SetAsync(UploadBandwidthScope.Job, jobId, request, tenant, user, service, cancellationToken)).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);

        endpoints.MapGroup("/api/agent/tasks/{attemptId:guid}")
            .WithMetadata(new AgentApiEndpoint(), new RequiresAgentProtocol(), new RequestSizeLimitAttribute(4 * 1024))
            .RequireAuthorization(AuthorizationPolicies.Agent)
            .MapPost("/upload-policy", PolicyAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(ITenantContext tenant, UploadBandwidthService service, CancellationToken cancellationToken)
    {
        var settings = await service.GetAsync(tenant.TenantId!, cancellationToken);
        return Results.Ok(new
        {
            minimumBytesPerSecond = UploadBandwidth.MinimumBytesPerSecond,
            maximumBytesPerSecond = UploadBandwidth.MaximumBytesPerSecond,
            tenant = new { bytesPerSecond = settings.Tenant },
            agents = settings.Agents.Select(pair => new { agentId = pair.Key, bytesPerSecond = pair.Value }),
            jobs = settings.Jobs.Select(pair => new { jobId = pair.Key, bytesPerSecond = pair.Value })
        });
    }

    private static async Task<IResult> SetAsync(UploadBandwidthScope scope, Guid scopeId, UploadBandwidthLimitRequest request, ITenantContext tenant,
        ICurrentUser user, UploadBandwidthService service, CancellationToken cancellationToken)
    {
        try
        {
            return await service.SetAsync(tenant.TenantId!, scope, scopeId, request.BytesPerSecond, user.UserId!, cancellationToken)
                ? Results.Ok(new { scope = scope.ToString().ToLowerInvariant(), scopeId, bytesPerSecond = request.BytesPerSecond })
                : Results.NotFound();
        }
        catch (ArgumentOutOfRangeException)
        {
            return Results.BadRequest(new { error = "invalid_request", field = "bytesPerSecond", code = "out_of_range", message = "The upload limit is outside the supported range." });
        }
    }

    private static async Task<IResult> PolicyAsync(Guid attemptId, UploadPolicyRequest request, ICurrentAgent agent, BackupOrchestrator orchestrator,
        UploadBandwidthService service, CancellationToken cancellationToken)
    {
        if (request.LeaseToken == Guid.Empty || request is { Release: true, Observe: true }) return BackupTaskEndpoints.Invalid();
        var action = request.Release ? UploadSliceAction.Release : request.Observe ? UploadSliceAction.Observe : UploadSliceAction.Renew;
        var policy = await service.PolicyAsync(orchestrator.Command(agent.Identity!, attemptId, request.LeaseToken), action, cancellationToken);
        return policy is null ? BackupTaskEndpoints.Mutation(BackupMutationResult.Conflict) : Results.Ok(policy);
    }
}
