using System.Net;
using BackupPlatform.Agent.Core.Connectivity;

namespace BackupPlatform.Agent.Transport;

/// <summary>Maps transport errors to core failures. Descriptions contain status codes and exception type/message only, never request headers.</summary>
internal static class AgentTransportFailures
{
    public static AgentBackendFailure FromStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => new(AgentBackendFailureKind.Unauthorized, "the backend did not accept the credential (HTTP 401)") { StatusCode = 401 },
        HttpStatusCode.BadRequest => new(AgentBackendFailureKind.ProtocolIncompatible, "the backend rejected the request as incompatible (HTTP 400)") { StatusCode = 400 },
        HttpStatusCode.TooManyRequests => new(AgentBackendFailureKind.RateLimited, "the backend is rate limiting this agent (HTTP 429)") { StatusCode = 429 },
        HttpStatusCode.RequestTimeout => new(AgentBackendFailureKind.Transient, "the backend timed out (HTTP 408)") { StatusCode = 408 },
        >= HttpStatusCode.InternalServerError => new(AgentBackendFailureKind.Transient, $"the backend failed (HTTP {(int)status})") { StatusCode = (int)status },
        _ => new(AgentBackendFailureKind.Rejected, $"the backend refused the request (HTTP {(int)status})") { StatusCode = (int)status },
    };

    public static AgentBackendFailure FromException(Exception? exception)
    {
        if (exception is null)
        {
            return new(AgentBackendFailureKind.Transient, "the connection was lost");
        }
        var current = exception;
        while (current is not null)
        {
            if (current is HttpRequestException { StatusCode: { } status })
            {
                return FromStatus(status);
            }
            current = current.InnerException;
        }
        return new(AgentBackendFailureKind.Transient, $"{exception.GetType().Name}: {exception.Message}");
    }
}
