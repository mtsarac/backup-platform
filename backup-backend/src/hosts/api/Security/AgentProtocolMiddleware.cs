using BackupPlatform.Agents;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Protocol;

namespace BackupPlatform.Api.Security;

/// <summary>Endpoint metadata: the caller must declare a compatible protocol in <see cref="AgentProtocolHeader"/>.</summary>
public sealed class RequiresAgentProtocol { }

/// <summary>The protocol version the current request declared; set only after the gate accepted it.</summary>
public sealed record AgentProtocolFeature(ProtocolVersion Version);

/// <summary>
/// Enforces the protocol window on heartbeat, poll and push-channel requests, after authentication and rate limiting
/// (so an unauthenticated caller learns nothing about the window and a mismatched agent cannot bypass throttling).
/// Rejection is a 400 with a machine-readable code and action so an agent can tell "upgrade me" from "retry later".
/// </summary>
public sealed class AgentProtocolMiddleware(RequestDelegate next, AgentProtocolGate gate, AgentSecurityOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<RequiresAgentProtocol>() is null)
        {
            await next(context);
            return;
        }
        var values = context.Request.Headers[AgentProtocolHeader.Name];
        var decision = values.Count > 1 ? gate.Evaluate("multiple") : gate.Evaluate(values.Count == 1 ? values[0] : null);
        if (decision.ToError() is not { } error)
        {
            context.Features.Set(new AgentProtocolFeature(decision.Version!));
            await next(context);
            return;
        }
        var logger = context.RequestServices.GetRequiredService<ILogger<AgentProtocolMiddleware>>();
        logger.LogInformation("Agent request rejected by the protocol gate: {Outcome}, declared {DeclaredProtocol}.",
            decision.Outcome, decision.Version?.ToString() ?? "(none)");
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new AgentApiError
        {
            Error = error.Error,
            Action = error.Action,
            SupportedProtocol = ToWire(options.Protocol),
            Message = decision.Outcome == AgentProtocolOutcome.Malformed
                ? $"{AgentProtocolHeader.Name} must be major.minor."
                : null
        }, context.RequestAborted);
    }

    public static AgentServerProtocol ToWire(ProtocolNegotiationContract protocol) => new()
    {
        Current = protocol.Version.ToString(),
        MinimumSupported = protocol.SupportedVersions.Minimum.ToString(),
        MaximumSupported = protocol.SupportedVersions.Maximum.ToString()
    };
}
