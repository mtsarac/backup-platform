using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Transport;

/// <summary>
/// <see cref="IAgentBackendClient"/> over HTTPS. Every request carries the credential of the identity passed in and the
/// protocol header; secrets never reach exception messages, failure descriptions or logs. Network errors, timeouts and
/// unreadable answers become failures; only cancellation throws.
/// </summary>
public sealed class HttpAgentBackendClient : IAgentBackendClient
{
    private const string HeartbeatPath = "api/agent/heartbeat";
    private const string PollPath = "api/agent/tasks";

    private static readonly string ProtocolHeaderValue = AgentProtocol.Current.Version.ToString();

    private readonly HttpClient _http;
    private readonly AgentBackendEndpoint _endpoint;
    private readonly TimeProvider _time;

    public HttpAgentBackendClient(HttpClient http, AgentBackendEndpoint endpoint, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _http = http;
        _endpoint = endpoint;
        _time = timeProvider;
    }

    public Uri BackendBaseUri => _endpoint.BaseUri;

    public Task<AgentBackendResult<AgentHeartbeatResponse>> SendHeartbeatAsync(AgentLocalIdentity identity, AgentHeartbeatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<AgentHeartbeatResponse>(identity, HttpMethod.Post, HeartbeatPath, JsonContent.Create(request, options: JsonSerializerOptions.Web), cancellationToken);
    }

    public Task<AgentBackendResult<AgentPollResponse>> PollAsync(AgentLocalIdentity identity, string? cursor, CancellationToken cancellationToken)
    {
        var path = string.IsNullOrEmpty(cursor) ? PollPath : $"{PollPath}?cursor={Uri.EscapeDataString(cursor)}";
        return SendAsync<AgentPollResponse>(identity, HttpMethod.Get, path, null, cancellationToken);
    }

    private async Task<AgentBackendResult<T>> SendAsync<T>(AgentLocalIdentity identity, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(identity);
        using var request = new HttpRequestMessage(method, new Uri(_endpoint.BaseUri, path)) { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", identity.Current.ToAuthorizationHeaderValue());
        request.Headers.TryAddWithoutValidation(AgentProtocolHeader.Name, ProtocolHeaderValue);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return await MapAsync<T>(response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AgentBackendResult<T>.Fail(new AgentBackendFailure(AgentBackendFailureKind.Transient, "the request timed out"));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            return AgentBackendResult<T>.Fail(AgentTransportFailures.FromException(exception));
        }
    }

    private async Task<AgentBackendResult<T>> MapAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        where T : class
    {
        var status = response.StatusCode;
        if (status == HttpStatusCode.OK)
        {
            var value = await ReadBodyAsync<T>(response, cancellationToken).ConfigureAwait(false);
            return value is null
                ? AgentBackendResult<T>.Fail(new AgentBackendFailure(AgentBackendFailureKind.Transient, "the backend answer could not be read") { StatusCode = 200 })
                : AgentBackendResult<T>.Success(value);
        }

        var failure = AgentTransportFailures.FromStatus(status);
        switch (failure.Kind)
        {
            case AgentBackendFailureKind.ProtocolIncompatible:
                var error = await ReadBodyAsync<AgentApiError>(response, cancellationToken).ConfigureAwait(false);
                return error is { Error: AgentErrorCodes.ProtocolIncompatible or AgentErrorCodes.ProtocolMissing }
                    ? AgentBackendResult<T>.Fail(failure with
                    {
                        Error = error,
                        Description = $"protocol {error.Error} (action {error.Action ?? "none"})",
                    })
                    : AgentBackendResult<T>.Fail(failure with
                    {
                        Kind = AgentBackendFailureKind.Rejected,
                        Description = "the backend rejected the request (HTTP 400)",
                        Error = error,
                    });
            case AgentBackendFailureKind.RateLimited:
                var limited = await ReadBodyAsync<AgentApiError>(response, cancellationToken).ConfigureAwait(false);
                return AgentBackendResult<T>.Fail(failure with { RetryAfter = ReadRetryAfter(response, limited), Error = limited });
            default:
                return AgentBackendResult<T>.Fail(failure);
        }
    }

    private TimeSpan? ReadRetryAfter(HttpResponseMessage response, AgentApiError? body)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }
        if (header?.Date is { } date)
        {
            return date - _time.GetUtcNow();
        }
        return body?.RetryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <summary>Reads a JSON body, returning null when it is missing or malformed so an odd error body never hides the status.</summary>
    private static async Task<T?> ReadBodyAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonSerializerOptions.Web, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}
