using BackupPlatform.Application.Backups;

namespace BackupPlatform.Api.Databases;

/// <summary>
/// Removes credentials of job creations that crashed after storing the login and before saving the job, once no retry came within <see cref="Grace"/>.
/// A credential some job references is never touched. Housekeeping: a failed pass is logged by exception type only and tried again next minute.
/// </summary>
public sealed class JobCredentialSweepWorker(IJobCredentialClaims claims, DatabaseCredentialVault vault, TimeProvider time, ILogger<JobCredentialSweepWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Grace = TimeSpan.FromHours(1);
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private const int Batch = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("Job credential sweep failed with {ExceptionType}", exception.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var removed = await claims.SweepStaleAsync(time.GetUtcNow() - Grace, Batch, vault.DeleteStoredAsync, cancellationToken);
        if (removed > 0) logger.LogInformation("Removed unreferenced job credentials: Count={Count}", removed);
        return removed;
    }
}
