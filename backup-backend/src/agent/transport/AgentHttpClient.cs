using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Transport;

/// <summary>One authentication, protocol and failure policy for connectivity and task requests.</summary>
internal sealed class AgentHttpClient(HttpClient http, AgentBackendEndpoint endpoint, TimeProvider timeProvider)
{
    public Task<AgentBackendResult<T>> SendAsync<T>(AgentLocalIdentity identity, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
        where T : class => SendCoreAsync(identity, method, path, content, ReadSuccessAsync<T>, cancellationToken);

    /// <summary>Like <see cref="SendAsync{T}"/>, but a 204 answer is a success without a value (the claim route had nothing to hand out).</summary>
    public Task<AgentBackendResult<T?>> SendOptionalAsync<T>(AgentLocalIdentity identity, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
        where T : class => SendCoreAsync<T?>(identity, method, path, content, ReadOptionalSuccessAsync<T>, cancellationToken);

    public Task<AgentBackendResult<bool>> SendEmptyAsync(AgentLocalIdentity identity, string path, HttpContent content, CancellationToken cancellationToken) =>
        SendEmptyAsync(identity, HttpMethod.Post, path, content, cancellationToken);

    public Task<AgentBackendResult<bool>> SendEmptyAsync(AgentLocalIdentity identity, HttpMethod method, string path, HttpContent content, CancellationToken cancellationToken) =>
        SendCoreAsync(identity, method, path, content, static (_, _) => Task.FromResult(AgentBackendResult<bool>.Success(true)), cancellationToken);

    internal Task<AgentBackendResult<T>> ReadAsync<T>(AgentLocalIdentity identity, string path,
        Func<HttpResponseMessage, CancellationToken, Task<AgentBackendResult<T>>> readSuccess, CancellationToken cancellationToken) =>
        SendCoreAsync(identity, HttpMethod.Get, path, null, readSuccess, cancellationToken);

    private async Task<AgentBackendResult<T>> SendCoreAsync<T>(AgentLocalIdentity identity, HttpMethod method, string path, HttpContent? content,
        Func<HttpResponseMessage, CancellationToken, Task<AgentBackendResult<T>>> readSuccess, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        using var request = new HttpRequestMessage(method, new Uri(endpoint.BaseUri, path)) { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", identity.Current.ToAuthorizationHeaderValue());
        request.Headers.TryAddWithoutValidation(AgentProtocolHeader.Name, AgentProtocol.Current.Version.ToString());
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? await readSuccess(response, cancellationToken).ConfigureAwait(false)
                : AgentBackendResult<T>.Fail(await ReadFailureAsync(response, cancellationToken).ConfigureAwait(false));
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

    private static async Task<AgentBackendResult<T>> ReadSuccessAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) where T : class
    {
        var value = await ReadBodyAsync<T>(response, cancellationToken).ConfigureAwait(false);
        return value is null
            ? AgentBackendResult<T>.Fail(new AgentBackendFailure(AgentBackendFailureKind.Transient, "the backend answer could not be read") { StatusCode = (int)response.StatusCode })
            : AgentBackendResult<T>.Success(value);
    }

    private static async Task<AgentBackendResult<T?>> ReadOptionalSuccessAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) where T : class
    {
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return AgentBackendResult<T?>.NoContent();
        }
        var value = await ReadBodyAsync<T>(response, cancellationToken).ConfigureAwait(false);
        return value is null
            ? AgentBackendResult<T?>.Fail(new AgentBackendFailure(AgentBackendFailureKind.Transient, "the backend answer could not be read") { StatusCode = (int)response.StatusCode })
            : AgentBackendResult<T?>.Success(value);
    }

    private async Task<AgentBackendFailure> ReadFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var failure = AgentTransportFailures.FromStatus(response.StatusCode);
        if (failure.Kind == AgentBackendFailureKind.ProtocolIncompatible)
        {
            var error = await ReadBodyAsync<AgentApiError>(response, cancellationToken).ConfigureAwait(false);
            return error is { Error: AgentErrorCodes.ProtocolIncompatible or AgentErrorCodes.ProtocolMissing }
                ? failure with { Error = error, Description = $"protocol {error.Error} (action {error.Action ?? "none"})" }
                : failure with { Kind = AgentBackendFailureKind.Rejected, Description = "the backend rejected the request (HTTP 400)", Error = error };
        }
        if (failure.Kind != AgentBackendFailureKind.RateLimited)
        {
            return failure;
        }
        var limited = await ReadBodyAsync<AgentApiError>(response, cancellationToken).ConfigureAwait(false);
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date is { } date ? date - timeProvider.GetUtcNow() :
            limited?.RetryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null);
        return failure with { RetryAfter = delay, Error = limited };
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) where T : class
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
