using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Downloads;

namespace BackupPlatform.Agent.Transport;

public sealed class HttpDownloadTaskClient(HttpClient http, AgentBackendEndpoint endpoint, TimeProvider time) : IDownloadTaskClient
{
    private readonly AgentHttpClient _client = new(http, endpoint, time);

    public Task<AgentBackendResult<BackupTaskLeaseResponse>> StartAsync(AgentLocalIdentity identity, Guid sessionId, BackupTaskStartRequest request, CancellationToken cancellationToken) =>
        _client.SendAsync<BackupTaskLeaseResponse>(identity, HttpMethod.Post, Path(sessionId, "start"), Content(request), cancellationToken);

    public Task<AgentBackendResult<BackupTaskLeaseResponse>> RenewAsync(AgentLocalIdentity identity, Guid sessionId, BackupTaskLeaseRequest request, CancellationToken cancellationToken) =>
        _client.SendAsync<BackupTaskLeaseResponse>(identity, HttpMethod.Post, Path(sessionId, "lease"), Content(request), cancellationToken);

    public Task<AgentBackendResult<bool>> UploadAsync(AgentLocalIdentity identity, DownloadTaskSnapshot task, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(content);
        if (task.LeaseToken == Guid.Empty || task.SizeBytes <= 0) throw new ArgumentException("A fenced download source is required.", nameof(task));
        var body = new StreamContent(content, 64 * 1024);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        body.Headers.ContentLength = task.SizeBytes;
        body.Headers.Add(DownloadTaskTypes.LeaseHeader, task.LeaseToken.ToString("D"));
        return _client.SendEmptyAsync(identity, Path(task.SessionId, "content"), body, cancellationToken);
    }

    public Task<AgentBackendResult<bool>> CompleteAsync(AgentLocalIdentity identity, Guid sessionId, DownloadTaskCompletionRequest request, CancellationToken cancellationToken) =>
        _client.SendEmptyAsync(identity, Path(sessionId, "complete"), Content(request), cancellationToken);

    private static JsonContent Content<T>(T request) where T : class => JsonContent.Create(request, options: JsonSerializerOptions.Web);

    private static string Path(Guid sessionId, string operation)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A session identifier is required.", nameof(sessionId));
        return $"api/agent/downloads/{sessionId:D}/{operation}";
    }
}
