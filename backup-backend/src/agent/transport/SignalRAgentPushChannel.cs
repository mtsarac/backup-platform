using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Agents;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Transport;

/// <summary>
/// <see cref="IAgentPushChannel"/> over a SignalR hub, WebSockets only. The credential and protocol headers travel as
/// connection HTTP headers (never in the query string). After a successful connect SignalR's own automatic reconnect
/// handles connection loss with the core backoff; the channel gives up (reports closed) only when the backend rejects
/// the credential or protocol, so the core can reload the identity and decide, and when the server's hello announces a
/// different agent identity than the one connecting.
/// </summary>
public sealed class SignalRAgentPushChannel : IAgentPushChannel
{
    private static readonly TimeSpan DefaultServerHelloTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DisposeGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly string ProtocolHeaderValue = AgentProtocol.Current.Version.ToString();

    private readonly AgentBackendEndpoint _endpoint;
    private readonly AgentBackoff _backoff;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeSpan _helloTimeout;
    private readonly object _gate = new();
    private Session? _session;
    private bool _disposed;

    public SignalRAgentPushChannel(AgentBackendEndpoint endpoint, AgentBackoff backoff, TimeProvider timeProvider, ILoggerFactory loggerFactory, TimeSpan? serverHelloTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(backoff);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _endpoint = endpoint;
        _backoff = backoff;
        _time = timeProvider;
        _loggerFactory = loggerFactory;
        _helloTimeout = serverHelloTimeout ?? DefaultServerHelloTimeout;
    }

    public async Task<AgentPushConnectResult> ConnectAsync(AgentLocalIdentity identity, IAgentPushChannelListener listener, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(listener);
        await StopAsync(cancellationToken).ConfigureAwait(false);
        var session = new Session(this, identity, listener);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _session = session;
        }
        try
        {
            var failure = await session.StartAsync(cancellationToken).ConfigureAwait(false);
            if (failure is null)
            {
                return AgentPushConnectResult.Connected;
            }
            await DetachAsync(session, CancellationToken.None).ConfigureAwait(false);
            return AgentPushConnectResult.Failed(failure);
        }
        catch (OperationCanceledException)
        {
            await DetachAsync(session, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }
        if (session is not null)
        {
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
        }
        using var grace = new CancellationTokenSource(DisposeGracePeriod, _time);
        try
        {
            await StopAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Bounded: the connection is abandoned if it does not stop in time.
        }
    }

    private async Task DetachAsync(Session session, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_session, session))
            {
                _session = null;
            }
        }
        await session.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class Session
    {
        private readonly SignalRAgentPushChannel _owner;
        private readonly Guid _expectedAgentId;
        private readonly IAgentPushChannelListener _listener;
        private readonly HubConnection _connection;
        private volatile TaskCompletionSource<AgentBackendFailure?> _hello = NewHelloSignal();
        private volatile bool _stopping;
        private volatile bool _established;
        private AgentBackendFailure? _terminalFailure;
        private int _closedReported;

        public Session(SignalRAgentPushChannel owner, AgentLocalIdentity identity, IAgentPushChannelListener listener)
        {
            _owner = owner;
            _expectedAgentId = identity.AgentId;
            _listener = listener;

            var url = new Uri(owner._endpoint.BaseUri, AgentPushChannelContract.HubPath.TrimStart('/'));
            var authorization = identity.Current.ToAuthorizationHeaderValue();
            var builder = new HubConnectionBuilder()
                .WithUrl(url, options =>
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.Headers["Authorization"] = authorization;
                    options.Headers[AgentProtocolHeader.Name] = ProtocolHeaderValue;
                    options.HttpMessageHandlerFactory = handler =>
                    {
                        if (handler is HttpClientHandler client)
                        {
                            client.AllowAutoRedirect = false;
                        }
                        return handler;
                    };
                })
                .WithAutomaticReconnect(new RetryPolicy(this));
            builder.Services.AddSingleton(owner._loggerFactory);
            _connection = builder.Build();

            _connection.On<AgentServerHello>(AgentPushChannelContract.ServerHelloMethod, OnServerHello);
            _connection.On<AgentProtocolNotice>(AgentPushChannelContract.ProtocolNoticeMethod, notice =>
            {
                if (!_stopping)
                {
                    _listener.OnProtocolNotice(notice);
                }
            });
            _connection.On<AgentPollHint>(AgentPushChannelContract.PollHintMethod, hint =>
            {
                if (!_stopping)
                {
                    _listener.OnPollHint(hint);
                }
            });
            _connection.Reconnecting += exception =>
            {
                if (_established && !_stopping)
                {
                    _hello = NewHelloSignal(); // every connection starts with its own hello
                    _listener.OnReconnecting(AgentTransportFailures.FromException(exception));
                }
                return Task.CompletedTask;
            };
            _connection.Reconnected += connectionId =>
            {
                if (_established && !_stopping)
                {
                    _ = ReportReconnectedAsync(_hello);
                }
                return Task.CompletedTask;
            };
            _connection.Closed += OnClosed;
        }

        /// <summary>Connects and waits for a valid server hello. Returns null when connected, otherwise the failure.</summary>
        public async Task<AgentBackendFailure?> StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return AgentTransportFailures.FromException(exception);
            }

            try
            {
                var failure = await _hello.Task.WaitAsync(_owner._helloTimeout, _owner._time, cancellationToken).ConfigureAwait(false);
                if (failure is null)
                {
                    _established = true;
                }
                return failure;
            }
            catch (TimeoutException)
            {
                return new AgentBackendFailure(AgentBackendFailureKind.Transient, "the server did not send its hello in time");
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _stopping = true;
            try
            {
                await _connection.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Stopping a broken connection must not fail shutdown or reconnect.
            }
            finally
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static TaskCompletionSource<AgentBackendFailure?> NewHelloSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void OnServerHello(AgentServerHello hello)
        {
            if (_stopping)
            {
                return;
            }
            _hello.TrySetResult(hello.AgentId == _expectedAgentId
                ? null
                : new AgentBackendFailure(AgentBackendFailureKind.Rejected, "the server hello announced a different agent identity"));
        }

        /// <summary>A reconnected connection only counts once its hello confirms the expected agent; otherwise the channel is closed.</summary>
        private async Task ReportReconnectedAsync(TaskCompletionSource<AgentBackendFailure?> hello)
        {
            AgentBackendFailure? failure;
            try
            {
                failure = await hello.Task.WaitAsync(_owner._helloTimeout, _owner._time).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                failure = new AgentBackendFailure(AgentBackendFailureKind.Transient, "the server did not send its hello in time");
            }
            if (_stopping)
            {
                return;
            }
            if (failure is null)
            {
                _listener.OnReconnected();
            }
            else
            {
                Abort(failure);
            }
        }

        private Task OnClosed(Exception? exception)
        {
            if (_stopping)
            {
                return Task.CompletedTask;
            }
            var failure = _terminalFailure ?? (exception is null ? null : AgentTransportFailures.FromException(exception));
            if (!_established)
            {
                _hello.TrySetResult(failure ?? new AgentBackendFailure(AgentBackendFailureKind.Transient, "the server closed the connection before its hello"));
                return Task.CompletedTask;
            }
            Report(failure);
            return Task.CompletedTask;
        }

        /// <summary>Gives up on an established connection: reports it closed once and stops it in the background.</summary>
        private void Abort(AgentBackendFailure failure)
        {
            _stopping = true;
            Report(failure);
            _ = Task.Run(async () =>
            {
                try
                {
                    await _connection.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Already closing; the core stops and disposes the session anyway.
                }
            });
        }

        private void Report(AgentBackendFailure? failure)
        {
            if (Interlocked.Exchange(ref _closedReported, 1) == 0)
            {
                _listener.OnClosed(failure);
            }
        }

        private void GiveUp(AgentBackendFailure failure) => _terminalFailure = failure;

        private sealed class RetryPolicy(Session session) : IRetryPolicy
        {
            public TimeSpan? NextRetryDelay(RetryContext retryContext)
            {
                if (session._stopping)
                {
                    return null;
                }
                var failure = AgentTransportFailures.FromException(retryContext.RetryReason);
                if (failure.Kind is AgentBackendFailureKind.Unauthorized or AgentBackendFailureKind.ProtocolIncompatible)
                {
                    session.GiveUp(failure);
                    return null;
                }
                return session._owner._backoff.GetDelay((int)Math.Min(retryContext.PreviousRetryCount, int.MaxValue));
            }
        }
    }
}
