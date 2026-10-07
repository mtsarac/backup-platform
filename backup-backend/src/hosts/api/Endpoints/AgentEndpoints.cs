using BackupPlatform.Agents;
using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Agents.Credentials;
using BackupPlatform.Agents.Enrollment;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Restores;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.Contracts.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BackupPlatform.Api.Endpoints;

/// <summary>
/// Agent-facing API. Enrollment is anonymous at the HTTP layer and authorized solely by the enrollment token;
/// every other route requires the agent authentication scheme and never accepts a browser session.
/// </summary>
public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var agent = endpoints.MapGroup("/api/agent").WithMetadata(new AgentApiEndpoint());
        agent.MapPost("/enroll", EnrollAsync).AllowAnonymous().RequireRateLimiting(AgentRateLimitOptions.Policies.Enrollment);
        agent.MapPost("/heartbeat", HeartbeatAsync)
            .RequireAuthorization(AuthorizationPolicies.Agent)
            .RequireRateLimiting(AgentRateLimitOptions.Policies.Heartbeat)
            .WithMetadata(new RequiresAgentProtocol(), new RequestSizeLimitAttribute(MaximumHeartbeatBytes));
        agent.MapPut("/capabilities", SyncCapabilitiesAsync)
            .RequireAuthorization(AuthorizationPolicies.Agent)
            .RequireRateLimiting(AgentRateLimitOptions.Policies.Heartbeat)
            .WithMetadata(new RequiresAgentProtocol(), new RequestSizeLimitAttribute(MaximumCapabilitySyncBytes));
        agent.MapGet("/tasks", PollAsync)
            .RequireAuthorization(AuthorizationPolicies.Agent)
            .RequireRateLimiting(AgentRateLimitOptions.Policies.Poll)
            .WithMetadata(new RequiresAgentProtocol());
        agent.MapGet("/me", GetIdentity).RequireAuthorization(AuthorizationPolicies.Agent);
        agent.MapPost("/credential/rotate", BeginRotationAsync).RequireAuthorization(AuthorizationPolicies.Agent);
        agent.MapPost("/credential/confirm", ConfirmRotationAsync)
            .WithMetadata(new AllowPendingAgentCredential())
            .RequireAuthorization(AuthorizationPolicies.Agent);
        return endpoints;
    }

    private const long MaximumHeartbeatBytes = 8 * 1024;
    private const long MaximumCapabilitySyncBytes = 32 * 1024;

    private static async Task<IResult> EnrollAsync(
        AgentEnrollmentRequest request, HttpContext context, AgentEnrollmentService service, AgentSecurityOptions options,
        EnrollmentTokenThrottle tokenThrottle, CancellationToken cancellationToken)
    {
        // The token identifier is the public half of the token: limiting by it never keys on a secret.
        if (AgentSecretReference.TryParse(request.EnrollmentToken, out var tokenId, out _) && !tokenThrottle.TryAcquire(tokenId))
        {
            await AgentRateLimiting.WriteRateLimitedAsync(context, tokenThrottle.RetryAfter, cancellationToken);
            return Results.Empty;
        }
        var result = await service.EnrollAsync(request, cancellationToken);
        switch (result.Status)
        {
            case AgentEnrollmentStatus.Enrolled:
                var enrolled = result.Enrolled!;
                return Results.Json(new AgentEnrollmentResponse
                {
                    AgentId = enrolled.AgentId.Value,
                    Credential = ToWire(enrolled.Credential, null),
                    Protocol = ToWire(options.Protocol)
                }, statusCode: StatusCodes.Status201Created);
            case AgentEnrollmentStatus.InvalidRequest:
                return Error(StatusCodes.Status400BadRequest, "invalid_request", $"Invalid field: {result.InvalidField}.");
            case AgentEnrollmentStatus.ProtocolIncompatible:
                return Results.Json(new AgentApiError
                {
                    Error = "protocol_incompatible",
                    Message = "The agent protocol version is not supported by this backend.",
                    SupportedProtocol = ToWire(result.ServerProtocol!)
                }, statusCode: StatusCodes.Status400BadRequest);
            case AgentEnrollmentStatus.EnrollmentDisabled:
                return Error(StatusCodes.Status403Forbidden, "enrollment_disabled", null);
            case AgentEnrollmentStatus.InstallationConflict:
                return Error(StatusCodes.Status409Conflict, "installation_already_enrolled", null);
            default:
                return Error(StatusCodes.Status401Unauthorized, "invalid_enrollment_token", null);
        }
    }

    private static async Task<IResult> HeartbeatAsync(
        AgentHeartbeatRequest request, HttpContext context, ICurrentAgent currentAgent, AgentConnectivityService service,
        AgentCapabilityService capabilities, AgentConnectivityOptions options, AgentSecurityOptions security, CancellationToken cancellationToken)
    {
        var protocol = context.Features.Get<AgentProtocolFeature>()!.Version;
        var result = await service.RecordHeartbeatAsync(currentAgent.Identity!, protocol, request, cancellationToken);
        if (result.Status != HeartbeatStatus.Recorded)
        {
            return Error(StatusCodes.Status400BadRequest, AgentErrorCodes.InvalidRequest, $"Invalid field: {result.InvalidField}.");
        }
        var syncRequested = await capabilities.IsSyncRequestedAsync(currentAgent.Identity!, request.CapabilityRevision, cancellationToken);
        return Results.Ok(new AgentHeartbeatResponse
        {
            ServerTime = result.ServerTime,
            HeartbeatIntervalSeconds = (int)options.HeartbeatInterval.TotalSeconds,
            OfflineTimeoutSeconds = (int)options.OfflineTimeout.TotalSeconds,
            Protocol = AgentProtocolMiddleware.ToWire(security.Protocol),
            CapabilitySyncRequested = syncRequested
        });
    }

    private static async Task<IResult> SyncCapabilitiesAsync(
        AgentCapabilitySyncRequest request, ICurrentAgent currentAgent, AgentCapabilityService service, CancellationToken cancellationToken)
    {
        var result = await service.SyncAsync(currentAgent.Identity!, request, cancellationToken);
        return result.Status switch
        {
            CapabilitySyncStatus.Synced => Results.Ok(new AgentCapabilitySyncResponse { Revision = result.Revision!, Changed = result.Changed }),
            CapabilitySyncStatus.InvalidRequest => Error(StatusCodes.Status400BadRequest, AgentErrorCodes.InvalidRequest, $"Invalid field: {result.InvalidField}."),
            _ => Error(StatusCodes.Status401Unauthorized, "unauthorized", null)
        };
    }

    private static async Task<IResult> PollAsync(
        string? cursor, ICurrentAgent currentAgent, AgentConnectivityService service, BackupOrchestrator backups,
        AgentReplicaDeletionService deletions, RestoreService restores, CancellationToken cancellationToken)
    {
        var poll = await service.PollAsync(currentAgent.Identity!, cursor, cancellationToken);
        if (poll is null)
        {
            return Error(StatusCodes.Status400BadRequest, AgentErrorCodes.InvalidRequest, "Invalid field: cursor.");
        }
        var deletion = await deletions.ClaimAsync(currentAgent.Identity!, cancellationToken);
        // A restore is explicit user work on a target and never runs beside a backup of the same agent: it is claimed first, and when one was claimed the backup claim is
        // skipped (the store would refuse it anyway while the restore holds the agent).
        var restore = await restores.ClaimAsync(currentAgent.Identity!, cancellationToken);
        var backup = restore is null ? await backups.ClaimAsync(currentAgent.Identity!, cancellationToken) : null;
        var tasks = new List<AgentTaskEnvelope>(2);
        if (deletion is not null) tasks.Add(new AgentTaskEnvelope { Id = deletion.ReplicaId.ToString(), Type = "delete-replica", DeleteReplica = deletion });
        if (restore is not null) tasks.Add(new AgentTaskEnvelope { Id = restore.AttemptId.ToString(), Type = RestoreTaskTypes.DatabaseRestore, Restore = restore });
        if (backup is not null) tasks.Add(new AgentTaskEnvelope { Id = backup.AttemptId.ToString(), Type = "database-backup", Backup = backup });
        return Results.Ok(new AgentPollResponse
        {
            Cursor = poll.Cursor,
            Tasks = tasks,
            NextPollAfterSeconds = (int)poll.NextPollAfter.TotalSeconds
        });
    }

    private static IResult GetIdentity(ICurrentAgent currentAgent)
    {
        var identity = currentAgent.Identity!;
        return Results.Ok(new AgentIdentityResponse
        {
            AgentId = identity.AgentId.Value,
            TenantId = identity.TenantId.Value,
            CredentialId = identity.CredentialId,
            CredentialGeneration = identity.CredentialGeneration
        });
    }

    private static async Task<IResult> BeginRotationAsync(ICurrentAgent currentAgent, AgentCredentialService service, CancellationToken cancellationToken)
    {
        var result = await service.BeginRotationAsync(currentAgent.Identity!, cancellationToken);
        return result.Status switch
        {
            BeginRotationStatus.Started => Results.Json(
                ToWire(result.Started!.Pending, result.Started.Pending.Credential.ExpiresAt), statusCode: StatusCodes.Status201Created),
            BeginRotationStatus.AgentNotActive => Error(StatusCodes.Status403Forbidden, "agent_not_active", null),
            _ => Error(StatusCodes.Status401Unauthorized, "unauthorized", null)
        };
    }

    private static async Task<IResult> ConfirmRotationAsync(ICurrentAgent currentAgent, AgentCredentialService service, CancellationToken cancellationToken)
    {
        var result = await service.ConfirmRotationAsync(currentAgent.Identity!, cancellationToken);
        return result.Status switch
        {
            ConfirmRotationStatus.Confirmed => Results.Ok(new AgentRotationConfirmationResponse { CredentialGeneration = result.CredentialGeneration }),
            ConfirmRotationStatus.AlreadyConfirmed => Results.Ok(new AgentRotationConfirmationResponse
            {
                CredentialGeneration = result.CredentialGeneration,
                AlreadyConfirmed = true
            }),
            ConfirmRotationStatus.PendingRotationExists => Error(StatusCodes.Status409Conflict, "pending_rotation_exists",
                "A pending credential exists; confirm with the pending credential, or begin a new rotation."),
            ConfirmRotationStatus.AgentNotActive => Error(StatusCodes.Status403Forbidden, "agent_not_active", null),
            _ => Error(StatusCodes.Status401Unauthorized, "unauthorized", null)
        };
    }

    private static IssuedAgentCredential ToWire(NewAgentCredential issued, DateTimeOffset? expiresAt) => new()
    {
        CredentialId = issued.Credential.Id,
        Secret = issued.Secret,
        Generation = issued.Credential.Generation,
        IssuedAt = issued.Credential.IssuedAt,
        ExpiresAt = expiresAt
    };

    private static AgentServerProtocol ToWire(ProtocolNegotiationContract protocol) => new()
    {
        Current = protocol.Version.ToString(),
        MinimumSupported = protocol.SupportedVersions.Minimum.ToString(),
        MaximumSupported = protocol.SupportedVersions.Maximum.ToString()
    };

    private static IResult Error(int status, string error, string? message) =>
        Results.Json(new AgentApiError { Error = error, Message = message }, statusCode: status);
}
