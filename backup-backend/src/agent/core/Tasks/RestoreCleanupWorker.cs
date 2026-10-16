using System.Threading.Channels;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Restores;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>
/// Removes a platform-created database (a verification's disposable <c>bp_verify_*</c> database, or an operator-approved <c>bp_old_*</c> rollback copy) that the backend owes the target's
/// cleanup for. The backend knows the name before anything exists, so this also reclaims what a crashed agent, a lost lease or a cancelled run left behind. One removal at a time,
/// leased and fenced by the lease token the claim handed out. The provider drops only a name that matches the kind's reserved pattern, and this worker refuses a task whose name or kind
/// does not before it even resolves a credential.
/// </summary>
public sealed class RestoreCleanupWorker : IAgentTaskSink
{
    private static readonly TimeSpan CompletionRetry = TimeSpan.FromSeconds(2);

    private readonly Channel<RestoreCleanupSnapshot> _queue = Channel.CreateBounded<RestoreCleanupSnapshot>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        AllowSynchronousContinuations = false
    });
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _seen = [];
    private readonly IRestoreTaskClient _client;
    private readonly IAgentCredentialStore _identities;
    private readonly IReadOnlyDictionary<DatabaseEngineId, IRestoreVerificationProvider> _providers;
    private readonly RestoreTaskRequestFactory _requests;
    private readonly TimeProvider _time;
    private readonly ILogger<RestoreCleanupWorker> _logger;
    private Guid? _active;
    private int _started;

    public RestoreCleanupWorker(IRestoreTaskClient client, IAgentCredentialStore identities, IEnumerable<IDatabaseRestoreProvider> providers, RestoreTaskRequestFactory requests,
        TimeProvider timeProvider, ILogger<RestoreCleanupWorker> logger)
    {
        _client = client;
        _identities = identities;
        _providers = providers.OfType<IRestoreVerificationProvider>().Cast<IDatabaseRestoreProvider>().ToDictionary(provider => provider.Engine, provider => (IRestoreVerificationProvider)provider);
        _requests = requests;
        _time = timeProvider;
        _logger = logger;
    }

    public bool TryPublish(AgentTaskEnvelope envelope)
    {
        var now = _time.GetUtcNow();
        if (envelope is not { Type: RestoreTaskTypes.DatabaseRestoreCleanup, RestoreCleanup: { Version: 1 } task }
            || !Guid.TryParse(envelope.Id, out var id) || id != task.LeaseToken
            || task.LeaseToken == Guid.Empty || task.OperationId == Guid.Empty || task.TenantId == Guid.Empty || task.Target is null || task.LeaseExpiresAt <= now)
        {
            return false;
        }
        lock (_gate)
        {
            foreach (var pair in _seen)
            {
                if (pair.Value <= now && pair.Key != _active)
                {
                    _seen.Remove(pair.Key);
                }
            }
            if (_seen.ContainsKey(id) || !_queue.Writer.TryWrite(task))
            {
                return false;
            }
            _seen.Add(id, task.LeaseExpiresAt);
            return true;
        }
    }

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The cleanup worker can only run once.");
        }
        _time.StartOperation("restore cleanup");
        _time.EnterOperation("restore cleanup");
        try
        {
            while (await _time.ObserveAsync("restore cleanup", _queue.Reader.WaitToReadAsync(stoppingToken).AsTask()).ConfigureAwait(false))
            {
                if (!_queue.Reader.TryRead(out var task))
                {
                    continue;
                }
                lock (_gate)
                {
                    _active = task.LeaseToken;
                }
                try
                {
                    await ExecuteAsync(task, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    // Type only: provider and credential diagnostics must not leak into task logs. The backend keeps the removal owed and offers it again.
                    _logger.LogWarning("Cleanup of restore {OperationId} stopped ({ExceptionType}).", task.OperationId, exception.GetType().Name);
                }
                finally
                {
                    lock (_gate)
                    {
                        _active = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _time.EndOperation("restore cleanup");
        }
    }

    private async Task ExecuteAsync(RestoreCleanupSnapshot task, CancellationToken stoppingToken)
    {
        using var lease = new BackupTaskLease(task.LeaseExpiresAt, _time);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.Token);
        var completion = await DropAsync(task, lifetime.Token).ConfigureAwait(false);
        await CompleteAsync(task.OperationId, completion, lifetime.Token).ConfigureAwait(false);
    }

    /// <summary>Never throws except for cancellation: every other outcome, including a refused task, becomes a report with a stable code.</summary>
    private async Task<RestoreCleanupCompletionRequest> DropAsync(RestoreCleanupSnapshot task, CancellationToken cancellationToken)
    {
        DatabaseDropRequest request;
        try
        {
            var kind = task.Kind switch
            {
                "VerificationDatabase" => DatabaseDropKind.VerificationDatabase,
                "RollbackCopy" => DatabaseDropKind.RollbackCopy,
                _ => throw new ArgumentException("Unknown cleanup kind.")
            };
            request = new DatabaseDropRequest(task.DatabaseName, kind);
        }
        catch (ArgumentException)
        {
            return Report(task, "Failed", "invalid-request");
        }

        if (!_providers.TryGetValue(new DatabaseEngineId(task.Engine), out var provider))
        {
            return Report(task, "Failed", "unsupported-engine");
        }

        try
        {
            var (connection, credential) = await _requests.CreateCleanupTargetAsync(task, cancellationToken).ConfigureAwait(false);
            var result = await provider.DropDatabaseAsync(connection, credential, request, cancellationToken).ConfigureAwait(false);
            return result.Outcome switch
            {
                DatabaseDropOutcome.Dropped => Report(task, "Dropped", null),
                DatabaseDropOutcome.NotFound => Report(task, "NotFound", null),
                _ => Report(task, "Failed", Safe(result.ReasonCode))
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RestoreTaskRequestException)
        {
            return Report(task, "Failed", "invalid-configuration");
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Cleanup of restore {OperationId} failed ({ExceptionType}).", task.OperationId, exception.GetType().Name);
            return Report(task, "Failed", "cleanup-error");
        }
    }

    private static RestoreCleanupCompletionRequest Report(RestoreCleanupSnapshot task, string outcome, string? failureCode) =>
        new() { LeaseToken = task.LeaseToken, Outcome = outcome, FailureCode = failureCode };

    private static string Safe(string? code) => code is { Length: > 0 and <= 64 } && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') ? code : "cleanup-failed";

    /// <summary>Retries a transient failure until the backend answers or the lease is gone; the backend ignores a repeat that agrees with what it recorded.</summary>
    private async Task CompleteAsync(Guid operationId, RestoreCleanupCompletionRequest completion, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AgentBackendResult<bool> response;
            try
            {
                var identity = await _identities.LoadAsync(cancellationToken).ConfigureAwait(false);
                if (identity is null)
                {
                    return;
                }
                response = await _client.CompleteCleanupAsync(identity, operationId, completion, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                response = AgentBackendResult<bool>.Fail(new AgentBackendFailure(AgentBackendFailureKind.Transient, "The task backend could not be reached."));
            }
            if (response.IsSuccess || response.Failure.Kind is not (AgentBackendFailureKind.Transient or AgentBackendFailureKind.RateLimited))
            {
                return;
            }
            var delay = response.Failure.RetryAfter is { } requested && requested > CompletionRetry ? requested : CompletionRetry;
            await _time.ObserveAsync("restore cleanup", Task.Delay(delay, _time, cancellationToken)).ConfigureAwait(false);
        }
    }
}
