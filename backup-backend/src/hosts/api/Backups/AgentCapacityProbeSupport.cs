using System.Text.Json;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Storage;
using BackupPlatform.Contracts.Storage;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.Storage;

namespace BackupPlatform.Api.Backups;

public sealed class AgentCapacityProbeService(IStorageCapacityTaskQueue queue, IStorageConnectionStore connections,
    ISecretProvider secrets, TimeProvider time)
{
    public async Task<CapacityProbeTaskRequest?> ClaimAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        var task = await queue.ClaimAsync(identity, time.GetUtcNow(), cancellationToken);
        if (task is null) return null;
        var record = await connections.FindAsync(task.ConnectionId, identity.TenantId, cancellationToken);
        if (record is null) return null;
        try
        {
            IReadOnlyDictionary<string, string>? values = null;
            if (record.Connection.SecretReference is { } reference)
            {
                var secret = await secrets.ResolveAsync(new SecretReference(reference), cancellationToken);
                values = JsonSerializer.Deserialize<Dictionary<string, string>>(secret.Reveal());
            }
            if (!await queue.IsLiveAsync(identity, task.TaskId, task.LeaseToken, time.GetUtcNow(), cancellationToken)) return null;
            return task with { Secret = values };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            await queue.CompleteAsync(identity, task.TaskId, new CapacityProbeCompletionRequest
            {
                LeaseToken = task.LeaseToken,
                Result = CapacityProbeTaskResult.Failure(task.TaskId, task.ConnectionId, CapacityProbeFailureCodes.AuthenticationFailed)
            }, time.GetUtcNow(), cancellationToken);
            return null;
        }
    }
}
