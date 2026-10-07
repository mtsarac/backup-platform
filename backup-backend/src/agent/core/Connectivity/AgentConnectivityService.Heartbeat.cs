using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Core.Connectivity;

public sealed partial class AgentConnectivityService
{
    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (true)
        {
            var attempt = await WaitForRestrictionAsync("heartbeat", cancellationToken).ConfigureAwait(false);
            var identity = Identity;
            var request = _software.ToHeartbeat(_capabilities.GetRevision());
            var result = await GuardAsync("heartbeat", () => _client.SendHeartbeatAsync(identity, request, cancellationToken)).ConfigureAwait(false);

            // Failures never shorten the period; they can only add to it (Retry-After) or leave it unchanged.
            TimeSpan delay;
            if (result.IsSuccess)
            {
                failures = 0;
                ClearRestriction(attempt);
                ApplyServerHeartbeatInterval(identity.AgentId, result.Value);
                var acknowledgedAt = _time.GetUtcNow();
                Update(s => s with { ConsecutiveHeartbeatFailures = 0, LastSuccessfulHeartbeatUtc = acknowledgedAt });
                LogHeartbeatSucceeded(identity.AgentId, result.Value.ServerTime);
                if (result.Value.CapabilitySyncRequested)
                {
                    StartCapabilitySync(identity, cancellationToken);
                }
                delay = _backoff.AddJitter(_effectiveHeartbeatInterval);
            }
            else
            {
                failures++;
                var failure = result.Failure;
                Update(s => s with { ConsecutiveHeartbeatFailures = failures });
                delay = _backoff.AddJitter(_effectiveHeartbeatInterval);
                switch (failure.Kind)
                {
                    case AgentBackendFailureKind.Unauthorized:
                        SetRestriction(AgentChannelState.Unauthorized, null, "heartbeat rejected the credential");
                        break;
                    case AgentBackendFailureKind.ProtocolIncompatible:
                        SetRestriction(AgentChannelState.ProtocolIncompatible, failure.Error?.Action, "heartbeat reported a protocol mismatch");
                        break;
                    case AgentBackendFailureKind.RateLimited when ClampRetryAfter(failure.RetryAfter) is { } retryAfter && retryAfter > delay:
                        delay = retryAfter;
                        break;
                }
                LogHeartbeatFailed(identity.AgentId, failure.Kind, failure.Description, failures, delay, _backendHost);
            }
            await _time.ObserveAsync("heartbeat", Task.Delay(delay, _time, cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts a capability sync in the background unless one is already running, so a slow or failing sync can never delay
    /// the next heartbeat. A failure is only logged: the backend keeps asking on the following heartbeats until the
    /// snapshot is stored.
    /// </summary>
    private void StartCapabilitySync(AgentLocalIdentity identity, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _capabilitySyncRunning, 1, 0) != 0)
        {
            return;
        }
        _time.StartOperation("capability sync");
        _capabilitySync = Task.Run(() => RunCapabilitySyncAsync(identity, cancellationToken), CancellationToken.None);
    }

    private async Task RunCapabilitySyncAsync(AgentLocalIdentity identity, CancellationToken cancellationToken)
    {
        _time.EnterOperation("capability sync");
        try
        {
            if (await _time.ObserveAsync("capability sync", _capabilities.SyncAsync(identity, cancellationToken)).ConfigureAwait(false))
            {
                LogCapabilitySyncSucceeded(identity.AgentId);
            }
            else
            {
                LogCapabilitySyncFailed(identity.AgentId, _backendHost);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down; the next run asks again.
        }
        catch (Exception exception)
        {
            LogUnexpected("capability sync", exception.GetType().Name);
        }
        finally
        {
            Volatile.Write(ref _capabilitySyncRunning, 0);
            _time.EndOperation("capability sync");
        }
    }

    /// <summary>
    /// The server's advertised interval is its recommended cadence (its offline timeout is a multiple of it), so the
    /// effective period is the smaller of it and the configured interval, never below the minimum.
    /// </summary>
    private void ApplyServerHeartbeatInterval(Guid agentId, AgentHeartbeatResponse response)
    {
        var effective = _options.HeartbeatInterval;
        if (response.HeartbeatIntervalSeconds > 0)
        {
            var advertised = TimeSpan.FromSeconds(response.HeartbeatIntervalSeconds);
            effective = TimeSpan.FromTicks(Math.Min(effective.Ticks, advertised.Ticks));
        }
        effective = TimeSpan.FromTicks(Math.Max(effective.Ticks, AgentConnectivityOptions.MinimumHeartbeatInterval.Ticks));
        TimeSpan previous;
        lock (_gate)
        {
            previous = _effectiveHeartbeatInterval;
            _effectiveHeartbeatInterval = effective;
        }
        if (effective < previous)
        {
            LogHeartbeatIntervalLowered(agentId, _options.HeartbeatInterval, response.HeartbeatIntervalSeconds, effective);
        }
    }
}
