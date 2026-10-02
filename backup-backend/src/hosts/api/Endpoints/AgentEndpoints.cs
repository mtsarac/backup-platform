using BackupPlatform.Agents;
using BackupPlatform.Agents.Credentials;
using BackupPlatform.Agents.Enrollment;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Agents;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Protocol;

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
        agent.MapPost("/enroll", EnrollAsync).AllowAnonymous();
        agent.MapGet("/me", GetIdentity).RequireAuthorization(AuthorizationPolicies.Agent);
        agent.MapPost("/credential/rotate", BeginRotationAsync).RequireAuthorization(AuthorizationPolicies.Agent);
        agent.MapPost("/credential/confirm", ConfirmRotationAsync)
            .WithMetadata(new AllowPendingAgentCredential())
            .RequireAuthorization(AuthorizationPolicies.Agent);
        return endpoints;
    }

    private static async Task<IResult> EnrollAsync(AgentEnrollmentRequest request, AgentEnrollmentService service, AgentSecurityOptions options, CancellationToken cancellationToken)
    {
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
