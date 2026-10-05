namespace BackupPlatform.Agent.Core.Connectivity;

public sealed partial class AgentConnectivityService
{
    /// <summary>
    /// Decides when to poll. Polls happen (1) on a schedule while the fallback is active, (2) once after every
    /// (re)connect of the push channel and (3) after a poll hint. Every poll respects the minimum spacing, any
    /// Retry-After / failure backoff, and the shared restriction.
    /// </summary>
    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        string? cursor = null;
        long? lastPollAt = null;
        long? requestedAt = null;   // a catch-up or hint poll is wanted
        long? scheduledAt = null;   // next poll of the polling fallback
        long? notBefore = null;     // Retry-After / failure backoff for every kind of poll
        var pollingActive = false;
        var pollFailures = 0;
        var serverPollAfter = TimeSpan.Zero;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool pushUp, catchUp, hint;
            long downSince;
            Restriction? restriction;
            lock (_gate)
            {
                pushUp = _pushUp;
                downSince = _pushDownSince;
                catchUp = _catchUpRequested;
                hint = _hintRequested;
                _catchUpRequested = false;
                _hintRequested = false;
                restriction = _restriction;
            }
            var now = Now;
            if (catchUp || hint)
            {
                requestedAt ??= now;
            }

            long? activateAt = null;
            if (pushUp && !_options.PollWhilePushConnected)
            {
                if (pollingActive)
                {
                    pollingActive = false;
                    scheduledAt = null;
                    Update(static s => s with { PollingActive = false });
                    LogPollingDeactivated(Identity.AgentId);
                }
            }
            else if (!pollingActive)
            {
                activateAt = _options.PollWhilePushConnected ? now : After(downSince, _options.PushFallbackDelay);
                if (now >= activateAt)
                {
                    pollingActive = true;
                    activateAt = null;
                    scheduledAt = lastPollAt is { } last ? Math.Max(now, After(last, PollPeriod(serverPollAfter))) : now;
                    Update(static s => s with { PollingActive = true });
                    LogPollingActivated(Identity.AgentId, _time.GetElapsedTime(downSince), PollPeriod(serverPollAfter));
                }
            }

            var due = (requestedAt, scheduledAt) switch
            {
                (null, null) => (long?)null,
                (long r, null) => r,
                (null, long s) => s,
                (long r, long s) => Math.Min(r, s),
            };
            long? wakeAt = activateAt;
            if (due is { } dueAt)
            {
                if (lastPollAt is { } previous)
                {
                    dueAt = Math.Max(dueAt, After(previous, _options.MinimumPollSpacing));
                }
                if (notBefore is { } floor)
                {
                    dueAt = Math.Max(dueAt, floor);
                }
                if (restriction is not null)
                {
                    dueAt = Math.Max(dueAt, restriction.RetryAt);
                }
                if (dueAt <= now)
                {
                    if (restriction is not null)
                    {
                        await RefreshIdentityIfRejectedAsync(restriction, cancellationToken).ConfigureAwait(false);
                    }
                    var outcome = await PollOnceAsync(cursor, pollingActive, pollFailures, serverPollAfter, cancellationToken).ConfigureAwait(false);
                    lastPollAt = Now;
                    requestedAt = null;
                    cursor = outcome.Cursor ?? cursor;
                    pollFailures = outcome.Failures;
                    serverPollAfter = outcome.ServerPollAfter;
                    notBefore = outcome.NotBefore is { } wait ? After(lastPollAt.Value, wait) : null;
                    scheduledAt = pollingActive ? After(lastPollAt.Value, outcome.NextDelay) : null;
                    continue;
                }
                wakeAt = wakeAt is { } other ? Math.Min(other, dueAt) : dueAt;
            }
            await WaitForWakeAsync(wakeAt is { } target ? Until(target) : null, cancellationToken).ConfigureAwait(false);
        }
    }

    private readonly record struct PollOutcome(string? Cursor, int Failures, TimeSpan ServerPollAfter, TimeSpan NextDelay, TimeSpan? NotBefore);

    /// <summary>The polling period: the configured interval, lengthened (never shortened) by the server's hint.</summary>
    private TimeSpan PollPeriod(TimeSpan serverPollAfter) =>
        TimeSpan.FromTicks(Math.Max(_options.PollInterval.Ticks, serverPollAfter.Ticks));

    private async Task<PollOutcome> PollOnceAsync(string? cursor, bool pollingActive, int failures, TimeSpan serverPollAfter, CancellationToken cancellationToken)
    {
        var identity = Identity;
        var attempt = BeginAttempt();
        var result = await GuardAsync("poll", () => _client.PollAsync(identity, cursor, cancellationToken)).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            var response = result.Value;
            var next = response.NextPollAfterSeconds > 0 ? TimeSpan.FromSeconds(response.NextPollAfterSeconds) : TimeSpan.Zero;
            ClearRestriction(attempt);
            var polledAt = _time.GetUtcNow();
            Update(s => s with { ConsecutivePollFailures = 0, LastSuccessfulPollUtc = polledAt });
            LogPollSucceeded(identity.AgentId, response.Tasks.Count, response.NextPollAfterSeconds, pollingActive);
            if (_taskSink is not null)
            {
                foreach (var task in response.Tasks)
                {
                    _taskSink.TryPublish(task);
                }
            }
            return new PollOutcome(string.IsNullOrEmpty(response.Cursor) ? null : response.Cursor, 0, next,
                _backoff.AddJitter(PollPeriod(next)), null);
        }

        var failure = result.Failure;
        failures++;
        Update(s => s with { ConsecutivePollFailures = failures });
        var period = PollPeriod(serverPollAfter);
        TimeSpan nextDelay;
        TimeSpan notBefore;
        switch (failure.Kind)
        {
            case AgentBackendFailureKind.Unauthorized:
                SetRestriction(AgentChannelState.Unauthorized, null, "poll rejected the credential");
                nextDelay = _backoff.AddJitter(period);
                notBefore = TimeSpan.Zero;
                break;
            case AgentBackendFailureKind.ProtocolIncompatible:
                SetRestriction(AgentChannelState.ProtocolIncompatible, failure.Error?.Action, "poll reported a protocol mismatch");
                nextDelay = _backoff.AddJitter(period);
                notBefore = TimeSpan.Zero;
                break;
            case AgentBackendFailureKind.RateLimited:
                notBefore = ClampRetryAfter(failure.RetryAfter) ?? _backoff.GetDelay(failures - 1);
                nextDelay = TimeSpan.FromTicks(Math.Max(_backoff.AddJitter(period).Ticks, notBefore.Ticks));
                break;
            default:
                notBefore = _backoff.GetDelay(failures - 1);
                nextDelay = TimeSpan.FromTicks(Math.Max(_backoff.AddJitter(period).Ticks, notBefore.Ticks));
                break;
        }
        LogPollFailed(identity.AgentId, failure.Kind, failure.Description, failures, nextDelay, _backendHost);
        return new PollOutcome(null, failures, serverPollAfter, nextDelay, notBefore);
    }
}
