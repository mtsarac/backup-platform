using BackupPlatform.Application.Restores;

namespace BackupPlatform.Api.Restores;

/// <summary>What one sweep ended: restores whose agent stopped renewing the lease (AgentLost), and restores no agent claimed in time (Cancelled, NotClaimed).</summary>
public sealed record RestoreSweepResult(int LeasesExpired, int UnclaimedWithdrawn);

/// <summary>
/// Ends restores nobody is working on, so their replica pins cannot outlive them and a lost agent never leaves a restore "running" forever. Both rules are
/// decided by the database against the backend clock; this only chooses when to ask. A restore is never retried: a lost lease ends it AgentLost with the
/// target state the stage it reached allows, and the user decides what to do next.
/// </summary>
public sealed class StaleRestoreDetector(IServiceScopeFactory scopes, RestoreExecutionOptions options, TimeProvider time, ILogger<StaleRestoreDetector> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.SweepInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try { await SweepAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogError(exception, "Unable to expire stale restores."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>One pass at the backend clock's current time. Safe to run from any number of nodes at once: each expiry is decided under the agent's row lock.</summary>
    public async Task<RestoreSweepResult> SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IRestoreStore>();
        var now = time.GetUtcNow();
        var expired = await store.ExpireStaleAsync(now, cancellationToken);
        var withdrawn = await store.ExpireUnclaimedAsync(now, options.UnclaimedExpiry, cancellationToken);
        return new RestoreSweepResult(expired, withdrawn);
    }
}
