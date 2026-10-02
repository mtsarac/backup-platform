using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace BackupPlatform.AgentTests.Support;

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body);

/// <summary>Records each request (headers copied, body read) and answers with whatever the test configures.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        _requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri!, headers, body));
        return await Respond(request, cancellationToken);
    }
}
