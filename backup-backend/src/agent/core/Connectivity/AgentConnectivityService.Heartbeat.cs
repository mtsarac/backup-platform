using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Core.Connectivity;

public sealed partial class AgentConnectivityService
{
    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (true)
        {
            await WaitForRestrictionAsync(cancellationToken).ConfigureAwait(false);
            var identity = Identity;
            var attempt = BeginAttempt();
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
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
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
