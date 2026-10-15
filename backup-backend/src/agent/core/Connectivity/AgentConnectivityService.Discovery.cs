using BackupPlatform.Agent.Core.Discovery;
using BackupPlatform.Agent.Core.Identity;

namespace BackupPlatform.Agent.Core.Connectivity;

public sealed partial class AgentConnectivityService
{
    /// <summary>
    /// Starts a database discovery scan in the background unless one is already running, exactly like <see cref="StartCapabilitySync"/>:
    /// a scan can take minutes and must never delay a heartbeat. A failure is only logged; the backend asks again when a scan is due.
    /// </summary>
    private void StartDiscoveryScan(AgentLocalIdentity identity, CancellationToken cancellationToken)
    {
        if (_discovery is null || Interlocked.CompareExchange(ref _discoveryScanRunning, 1, 0) != 0)
        {
            return;
        }
        _time.StartOperation("discovery scan");
        _discoveryScan = Task.Run(() => RunDiscoveryScanAsync(_discovery, identity, cancellationToken), CancellationToken.None);
    }

    private async Task RunDiscoveryScanAsync(IDiscoveryScanRunner discovery, AgentLocalIdentity identity, CancellationToken cancellationToken)
    {
        _time.EnterOperation("discovery scan");
        try
        {
            await _time.ObserveAsync("discovery scan", discovery.RunAsync(identity, cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down; the next heartbeat asks again.
        }
        catch (Exception exception)
        {
            LogUnexpected("discovery scan", exception.GetType().Name);
        }
        finally
        {
            Volatile.Write(ref _discoveryScanRunning, 0);
            _time.EndOperation("discovery scan");
        }
    }
}
