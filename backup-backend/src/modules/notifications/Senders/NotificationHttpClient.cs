using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using BackupPlatform.Domain.Notifications;

namespace BackupPlatform.Notifications.Senders;

/// <summary>
/// The one HTTP client outbound notifications use (tenant webhooks and the SMS gateway). It is built so that it cannot be talked into reaching an
/// internal service: every connection goes through <see cref="NotificationNetworkGuard"/> (public addresses only, validated address = connected
/// address), redirects are never followed (a 3xx would otherwise hop to an unchecked host), no proxy from the environment is used (it would bypass the
/// guard), cookies and decompression are off and responses are never read beyond their status and headers.
/// </summary>
public sealed class NotificationHttpClient : IDisposable
{
    private readonly HttpClient _client;

    /// <param name="configure">A seam for tests (for example to trust a test certificate); production composes none.</param>
    public NotificationHttpClient(NotificationNetworkGuard guard, Action<SocketsHttpHandler>? configure = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (!guard.IsHttpsPortAllowed(context.DnsEndPoint.Port)) throw new NotificationNetworkException(NotificationNetworkException.Denied);
                var socket = await guard.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
        };
        configure?.Invoke(handler);
        // The per-send deadline comes from the caller's token; the client itself never times out on its own.
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 64 * 1024 };
    }

    /// <summary>Sends <paramref name="request"/> and maps the answer (or the transport failure) to an outcome. The response body is never read.</summary>
    public async Task<SendOutcome> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { Scheme: "https" }) return SendOutcome.Permanent("insecure_endpoint");
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return Map(response);
        }
        catch (HttpRequestException exception)
        {
            return exception.InnerException switch
            {
                NotificationNetworkException { Code: NotificationNetworkException.Denied } => SendOutcome.Permanent(NotificationNetworkException.Denied),
                NotificationNetworkException network => SendOutcome.Transient(network.Code),
                _ => SendOutcome.Transient("unreachable")
            };
        }
        catch (NotificationNetworkException exception)
        {
            return exception.Code == NotificationNetworkException.Denied ? SendOutcome.Permanent(exception.Code) : SendOutcome.Transient(exception.Code);
        }
    }

    internal static SendOutcome Map(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var code = "http_" + status;
        return status switch
        {
            >= 200 and < 300 => SendOutcome.Sent,
            >= 300 and < 400 => SendOutcome.Permanent("redirect_not_followed"),
            408 or 425 or 429 => SendOutcome.Transient(code, RetryAfter(response.Headers.RetryAfter)),
            >= 500 => SendOutcome.Transient(code, RetryAfter(response.Headers.RetryAfter)),
            _ => SendOutcome.Permanent(code)
        };
    }

    private static TimeSpan? RetryAfter(RetryConditionHeaderValue? header)
    {
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date && date - DateTimeOffset.UtcNow is { } wait && wait > TimeSpan.Zero) return wait;
        return null;
    }

    public void Dispose() => _client.Dispose();
}
