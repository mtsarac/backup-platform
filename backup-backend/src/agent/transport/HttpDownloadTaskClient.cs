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

    public Task<AgentBackendResult<DownloadSignal>> SignalAsync(AgentLocalIdentity identity, Guid sessionId, DownloadSignalRequest request, CancellationToken cancellationToken) =>
        _client.SendAsync<DownloadSignal>(identity, HttpMethod.Post, Path(sessionId, "signal"), Content(request), cancellationToken);

    public Task<AgentBackendResult<BackupPlatform.Contracts.Restores.ArtifactKeyUnwrapResponse>> KeyAsync(AgentLocalIdentity identity, Guid sessionId, DownloadSignalRequest request, CancellationToken cancellationToken) =>
        _client.SendAsync<BackupPlatform.Contracts.Restores.ArtifactKeyUnwrapResponse>(identity, HttpMethod.Post, Path(sessionId, "artifact-key"), Content(request), cancellationToken);

    public Task<AgentBackendResult<bool>> CompleteAsync(AgentLocalIdentity identity, Guid sessionId, DownloadTaskCompletionRequest request, CancellationToken cancellationToken) =>
        _client.SendEmptyAsync(identity, Path(sessionId, "complete"), Content(request), cancellationToken);

    private static JsonContent Content<T>(T request) where T : class => JsonContent.Create(request, options: JsonSerializerOptions.Web);

    private static string Path(Guid sessionId, string operation)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A session identifier is required.", nameof(sessionId));
        return $"api/agent/downloads/{sessionId:D}/{operation}";
    }
}
