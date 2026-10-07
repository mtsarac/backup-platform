using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.Agent.Core.Connectivity;

public sealed partial class AgentConnectivityService
{
    private PushSession? _currentSession;

    private async Task PushLoopAsync(CancellationToken cancellationToken)
    {
        // Counts failed attempts and short-lived sessions; reset only after a connection proved stable, so two agents
        // replacing each other's connection back off instead of ping-ponging.
        var attempt = 0;
        while (true)
        {
            var connectAttempt = await WaitForRestrictionAsync("push", cancellationToken).ConfigureAwait(false);
            var identity = Identity;
            var session = new PushSession(this, connectAttempt);
            _currentSession = session;
            LogPushConnecting(identity.AgentId, _backendHost, attempt);
            var connect = await GuardPushAsync(identity, session, cancellationToken).ConfigureAwait(false);

            AgentBackendFailure? failure;
            if (connect.IsConnected)
            {
                var connectedAt = Now;
                MarkPushUp(identity.AgentId, restored: false, connectAttempt);
                await _time.ObserveAsync("push", session.Ended, cancellationToken).ConfigureAwait(false);
                MarkPushDown();
                await _channel.StopAsync(cancellationToken).ConfigureAwait(false);
                failure = session.EndFailure;
                if (_time.GetElapsedTime(connectedAt) >= _options.ReconnectMaxDelay)
                {
                    attempt = 0;
                }
                LogPushClosed(identity.AgentId, failure?.Description ?? (session.EndedByNotice ? "protocol notice" : "closed by server"));
            }
            else
            {
                failure = connect.Failure;
            }

            if (failure is null && session.EndedByNotice)
            {
                continue; // the restriction set by the notice paces the next attempt
            }

            var failed = attempt + 1;
            Update(s => s with { ConsecutivePushFailures = failed });
            var delay = TimeSpan.Zero;
            switch (failure?.Kind)
            {
                case AgentBackendFailureKind.Unauthorized:
                    SetRestriction(AgentChannelState.Unauthorized, null, "push channel rejected the credential");
                    break;
                case AgentBackendFailureKind.ProtocolIncompatible:
                    SetRestriction(AgentChannelState.ProtocolIncompatible, failure.Error?.Action, "push channel reported a protocol mismatch");
                    break;
                default:
                    delay = _backoff.GetDelay(attempt);
                    if (failure is { Kind: AgentBackendFailureKind.RateLimited } && ClampRetryAfter(failure.RetryAfter) is { } retryAfter && retryAfter > delay)
                    {
                        delay = retryAfter;
                    }
                    break;
            }
            attempt++;
            if (failure is not null)
            {
                LogPushFailed(identity.AgentId, failure.Kind, failure.Description, attempt, delay, _backendHost);
            }
            if (delay > TimeSpan.Zero)
            {
                await _time.ObserveAsync("push", Task.Delay(delay, _time, cancellationToken)).ConfigureAwait(false);
            }
        }
    }

    private async Task<AgentPushConnectResult> GuardPushAsync(AgentLocalIdentity identity, PushSession session, CancellationToken cancellationToken)
    {
        try
        {
            return await _channel.ConnectAsync(identity, session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogUnexpected("push connect", exception.GetType().Name);
            return AgentPushConnectResult.Failed(new AgentBackendFailure(AgentBackendFailureKind.Transient, $"unexpected {exception.GetType().Name}"));
        }
    }

    private void MarkPushUp(Guid agentId, bool restored, long attemptSequence)
    {
        lock (_gate)
        {
            _pushUp = true;
            _catchUpRequested = true;
        }
        ClearRestriction(attemptSequence);
        Update(static s => s with { ConsecutivePushFailures = 0 });
        SignalWake();
        if (restored)
        {
            LogPushRestored(agentId, _backendHost);
        }
        else
        {
            LogPushConnected(agentId, _backendHost, AgentProtocol.Current.Version.ToString());
        }
    }

    private void MarkPushDown()
    {
        lock (_gate)
        {
            if (!_pushUp)
            {
                return;
            }
            _pushUp = false;
            _pushDownSince = Now;
        }
        Update(static s => s);
        SignalWake();
    }

    private sealed class PushSession(AgentConnectivityService owner, long attemptSequence) : IAgentPushChannelListener
    {
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Ended => _ended.Task;

        public AgentBackendFailure? EndFailure { get; private set; }

        public bool EndedByNotice { get; private set; }

        private bool IsCurrent => ReferenceEquals(owner._currentSession, this);

        public void OnPollHint(AgentPollHint hint)
        {
            if (!IsCurrent)
            {
                return;
            }
            owner.LogPollHint(owner.Identity.AgentId, hint.Reason);
            lock (owner._gate)
            {
                owner._hintRequested = true;
            }
            owner.SignalWake();
        }

        public void OnProtocolNotice(AgentProtocolNotice notice)
        {
            if (!IsCurrent)
            {
                return;
            }
            owner.LogProtocolNotice(owner.Identity.AgentId, notice.Kind, notice.Action, notice.Protocol.MinimumSupported, notice.Protocol.MaximumSupported);
            owner.SetRestriction(AgentChannelState.ProtocolIncompatible, notice.Action, "server sent a protocol notice");
            EndedByNotice = true;
            _ended.TrySetResult();
        }

        public void OnReconnecting(AgentBackendFailure? reason)
        {
            if (!IsCurrent)
            {
                return;
            }
            owner.MarkPushDown();
            owner.LogPushLost(owner.Identity.AgentId, reason?.Description ?? "connection lost");
        }

        public void OnReconnected()
        {
            if (IsCurrent)
            {
                owner.MarkPushUp(owner.Identity.AgentId, restored: true, attemptSequence);
            }
        }

        public void OnClosed(AgentBackendFailure? failure)
        {
            if (!IsCurrent)
            {
                return;
            }
            EndFailure = failure;
            _ended.TrySetResult();
        }
    }
}
