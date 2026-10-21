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
public sealed record BandwidthProfileAssignmentRequest(Guid? ProfileId);
public sealed record SaveBandwidthProfileRequest(string Name, string TimeZoneId, List<TransferScheduleWindowDto> Windows);

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

        // Profile assignments
        limits.MapPut("/tenant/profile", (BandwidthProfileAssignmentRequest request, ITenantContext tenant, ICurrentUser user, UploadBandwidthService service, CancellationToken cancellationToken) =>
            AssignProfileAsync(UploadBandwidthScope.Tenant, tenant.TenantId!.Value, request, tenant, user, service, cancellationToken)).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        limits.MapPut("/agents/{agentId:guid}/profile", (Guid agentId, BandwidthProfileAssignmentRequest request, ITenantContext tenant, ICurrentUser user, UploadBandwidthService service, CancellationToken cancellationToken) =>
            AssignProfileAsync(UploadBandwidthScope.Agent, agentId, request, tenant, user, service, cancellationToken)).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        limits.MapPut("/jobs/{jobId:guid}/profile", (Guid jobId, BandwidthProfileAssignmentRequest request, ITenantContext tenant, ICurrentUser user, UploadBandwidthService service, CancellationToken cancellationToken) =>
            AssignProfileAsync(UploadBandwidthScope.Job, jobId, request, tenant, user, service, cancellationToken)).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);

        // Bandwidth profiles CRUD (both tenant-scoped and /api/bandwidth-profiles)
        var profiles = endpoints.MapGroup("/api/tenants/{tenantId:guid}/bandwidth-profiles").WithMetadata(new TenantScopedEndpoint());
        profiles.MapGet("", ListProfilesAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        profiles.MapGet("/{profileId:guid}", GetProfileAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        profiles.MapPost("", (SaveBandwidthProfileRequest request, ITenantContext tenant, ICurrentUser user, UploadBandwidthService service, CancellationToken cancellationToken) =>
            SaveProfileAsync(null, request, tenant, user, service, cancellationToken)).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        profiles.MapPut("/{profileId:guid}", (Guid profileId, SaveBandwidthProfileRequest request, ITenantContext tenant, ICurrentUser user, UploadBandwidthService service, CancellationToken cancellationToken) =>
            SaveProfileAsync(profileId, request, tenant, user, service, cancellationToken)).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        profiles.MapDelete("/{profileId:guid}", DeleteProfileAsync).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);

        var directProfiles = endpoints.MapGroup("/api/bandwidth-profiles").WithMetadata(new TenantScopedEndpoint());
        directProfiles.MapGet("", ListProfilesAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        directProfiles.MapGet("/{profileId:guid}", GetProfileAsync).RequireAuthorization(AuthorizationPolicies.TenantViewer);
        directProfiles.MapPost("", (SaveBandwidthProfileRequest request, ITenantContext tenant, ICurrentUser user, UploadBandwidthService service, CancellationToken cancellationToken) =>
            SaveProfileAsync(null, request, tenant, user, service, cancellationToken)).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        directProfiles.MapPut("/{profileId:guid}", (Guid profileId, SaveBandwidthProfileRequest request, ITenantContext tenant, ICurrentUser user, UploadBandwidthService service, CancellationToken cancellationToken) =>
            SaveProfileAsync(profileId, request, tenant, user, service, cancellationToken)).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
        directProfiles.MapDelete("/{profileId:guid}", DeleteProfileAsync).RequireAuthorization(AuthorizationPolicies.TenantAdministrator);
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
            tenant = new { bytesPerSecond = settings.Tenant, profileId = settings.TenantProfileId },
            agents = settings.Agents.Select(pair => new { agentId = pair.Key, bytesPerSecond = pair.Value, profileId = settings.AgentProfiles?.GetValueOrDefault(pair.Key) }),
            jobs = settings.Jobs.Select(pair => new { jobId = pair.Key, bytesPerSecond = pair.Value, profileId = settings.JobProfiles?.GetValueOrDefault(pair.Key) })
        });
    }

    private static async Task<IResult> ListProfilesAsync(ITenantContext tenant, UploadBandwidthService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.ListProfilesAsync(tenant.TenantId!, cancellationToken));

    private static async Task<IResult> GetProfileAsync(Guid profileId, ITenantContext tenant, UploadBandwidthService service, CancellationToken cancellationToken)
    {
        var profile = await service.GetProfileAsync(tenant.TenantId!, profileId, cancellationToken);
        return profile is null ? Results.NotFound() : Results.Ok(profile);
    }

    private static async Task<IResult> SaveProfileAsync(Guid? profileId, SaveBandwidthProfileRequest request, ITenantContext tenant,
        ICurrentUser user, UploadBandwidthService service, CancellationToken cancellationToken)
    {
        try
        {
            var saved = await service.SaveProfileAsync(tenant.TenantId!, profileId, request.Name, request.TimeZoneId, request.Windows, user.UserId!, cancellationToken);
            return Results.Ok(saved);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = "invalid_request", message = exception.Message });
        }
    }

    private static async Task<IResult> DeleteProfileAsync(Guid profileId, ITenantContext tenant, ICurrentUser user, UploadBandwidthService service, CancellationToken cancellationToken) =>
        await service.DeleteProfileAsync(tenant.TenantId!, profileId, user.UserId!, cancellationToken) ? Results.NoContent() : Results.NotFound();

    private static async Task<IResult> AssignProfileAsync(UploadBandwidthScope scope, Guid scopeId, BandwidthProfileAssignmentRequest request,
        ITenantContext tenant, ICurrentUser user, UploadBandwidthService service, CancellationToken cancellationToken)
    {
        return await service.AssignProfileAsync(tenant.TenantId!, scope, scopeId, request.ProfileId, user.UserId!, cancellationToken)
            ? Results.Ok(new { scope = scope.ToString().ToLowerInvariant(), scopeId, profileId = request.ProfileId })
            : Results.NotFound();
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
        var policy = await service.PolicyAsync(orchestrator.Command(agent.Identity!, attemptId, request.LeaseToken) with { UploadId = request.UploadId == Guid.Empty ? null : request.UploadId }, action, cancellationToken);
        return policy is null ? BackupTaskEndpoints.Mutation(BackupMutationResult.Conflict) : Results.Ok(policy);
    }
}
