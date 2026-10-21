using System.Threading.Channels;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Storage;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Storage;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Core.Tasks;

public sealed class CapacityProbeTaskWorker(ICapacityProbeTaskClient client, IAgentCredentialStore identities,
    CapacityProbeTaskHandler handler, TimeProvider time, ILogger<CapacityProbeTaskWorker> logger) : IAgentTaskSink
{
    private readonly Channel<CapacityProbeTaskRequest> _queue = Channel.CreateBounded<CapacityProbeTaskRequest>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.Wait, SingleReader = true, AllowSynchronousContinuations = false
    });
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _seen = [];
    private int _started;

    public bool TryPublish(AgentTaskEnvelope envelope)
    {
        if (envelope is not { Type: CapacityProbeProtocol.TaskType, CapacityProbe: { } task }
            || !Guid.TryParse(envelope.Id, out var id) || id != task.TaskId || id == Guid.Empty
            || task.ConnectionId == Guid.Empty || task.AgentId == Guid.Empty || task.LeaseToken == Guid.Empty
            || task.LeaseExpiresAt <= time.GetUtcNow()) return false;
        lock (_gate)
        {
            var now = time.GetUtcNow();
            foreach (var pair in _seen)
                if (pair.Value <= now) _seen.Remove(pair.Key);
            if (_seen.ContainsKey(task.LeaseToken) || !_queue.Writer.TryWrite(task)) return false;
            _seen.Add(task.LeaseToken, task.LeaseExpiresAt);
            return true;
        }
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("The capacity worker can only run once.");
        try
        {
            await foreach (var task in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await ExecuteAsync(task, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception exception)
                {
                    logger.LogWarning("Capacity task {TaskId} stopped ({ExceptionType}).", task.TaskId, exception.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task ExecuteAsync(CapacityProbeTaskRequest task, CancellationToken stoppingToken)
    {
        using var lease = new BackupTaskLease(task.LeaseExpiresAt, time);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.Token);
        var cancellationToken = lifetime.Token;
        var claim = new CapacityProbeCompletionRequest { LeaseToken = task.LeaseToken };
        if (!await SendAsync(task, claim, authorize: true, cancellationToken).ConfigureAwait(false)) return;
        CapacityProbeTaskResult result;
        try { result = await handler.ExecuteAsync(task, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { result = CapacityProbeTaskResult.Failure(task.TaskId, task.ConnectionId, CapacityProbeFailureCodes.CapacityUnavailable); }
        await SendAsync(task, claim with { Result = result with { ErrorMessage = null } }, authorize: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SendAsync(CapacityProbeTaskRequest task, CapacityProbeCompletionRequest request, bool authorize, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = await identities.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (identity is null || identity.AgentId != task.AgentId) return false;
            AgentBackendResult<bool> response;
            try
            {
                response = authorize
                    ? await client.AuthorizeAsync(identity, task.TaskId, request, cancellationToken).ConfigureAwait(false)
                    : await client.CompleteAsync(identity, task.TaskId, request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                response = AgentBackendResult<bool>.Fail(new AgentBackendFailure(AgentBackendFailureKind.Transient, "The capacity backend could not be reached."));
            }
            if (response.IsSuccess) return true;
            if (response.Failure.Kind is not (AgentBackendFailureKind.Transient or AgentBackendFailureKind.RateLimited)) return false;
            var delay = response.Failure.RetryAfter is { } retry && retry > TimeSpan.FromSeconds(2) ? retry : TimeSpan.FromSeconds(2);
            await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);
        }
    }
}
