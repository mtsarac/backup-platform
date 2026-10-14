using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Contracts.Backups;

namespace BackupPlatform.Agent.Transport;

public sealed class HttpBackupTaskClient : IBackupTaskClient
{
    private readonly AgentHttpClient _client;

    public HttpBackupTaskClient(HttpClient http, AgentBackendEndpoint endpoint, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _client = new AgentHttpClient(http, endpoint, timeProvider);
    }

    public Task<AgentBackendResult<BackupTaskLeaseResponse>> StartAsync(AgentLocalIdentity identity, Guid attemptId, BackupTaskStartRequest request, CancellationToken cancellationToken) =>
        _client.SendAsync<BackupTaskLeaseResponse>(identity, HttpMethod.Post, Path(attemptId, "start"), Content(request), cancellationToken);

    public Task<AgentBackendResult<BackupTaskLeaseResponse>> RenewAsync(AgentLocalIdentity identity, Guid attemptId, BackupTaskLeaseRequest request, CancellationToken cancellationToken) =>
        _client.SendAsync<BackupTaskLeaseResponse>(identity, HttpMethod.Post, Path(attemptId, "lease"), Content(request), cancellationToken);

    public Task<AgentBackendResult<UploadPolicyResponse>> GetUploadPolicyAsync(AgentLocalIdentity identity, Guid attemptId, UploadPolicyRequest request, CancellationToken cancellationToken) =>
        _client.SendAsync<UploadPolicyResponse>(identity, HttpMethod.Post, Path(attemptId, "upload-policy"), Content(request), cancellationToken);

    public Task<AgentBackendResult<bool>> ReportProgressAsync(AgentLocalIdentity identity, Guid attemptId, BackupTaskProgressRequest request, CancellationToken cancellationToken) =>
        _client.SendEmptyAsync(identity, Path(attemptId, "progress"), Content(request), cancellationToken);

    public Task<AgentBackendResult<bool>> CompleteAsync(AgentLocalIdentity identity, Guid attemptId, BackupTaskCompletionRequest request, CancellationToken cancellationToken) =>
        _client.SendEmptyAsync(identity, Path(attemptId, "complete"), Content(request), cancellationToken);

    private static JsonContent Content<T>(T request) where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        return JsonContent.Create(request, options: JsonSerializerOptions.Web);
    }

    private static string Path(Guid attemptId, string operation)
    {
        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException("An attempt identifier is required.", nameof(attemptId));
        }
        return $"api/agent/tasks/{attemptId:D}/{operation}";
    }
}
