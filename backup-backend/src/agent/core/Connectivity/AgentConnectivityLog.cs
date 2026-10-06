using BackupPlatform.Contracts.Agents;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Core.Connectivity;

// Log statements never receive credentials, headers or tokens; failure descriptions come from the transport and are secret-free by contract.
public sealed partial class AgentConnectivityService
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Agent connectivity started: AgentId={AgentId} BackendHost={BackendHost} Protocol={ProtocolVersion} HeartbeatInterval={HeartbeatInterval} PollInterval={PollInterval} PushFallbackDelay={PushFallbackDelay}")]
    private partial void LogStarted(Guid agentId, string backendHost, string protocolVersion, TimeSpan heartbeatInterval, TimeSpan pollInterval, TimeSpan pushFallbackDelay);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Agent connectivity stopped: AgentId={AgentId}")]
    private partial void LogStopped(Guid agentId);

    [LoggerMessage(EventId = 10, Level = LogLevel.Debug, Message = "Connecting push channel: AgentId={AgentId} BackendHost={BackendHost} Attempt={Attempt}")]
    private partial void LogPushConnecting(Guid agentId, string backendHost, int attempt);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "Push channel connected: AgentId={AgentId} BackendHost={BackendHost} Protocol={ProtocolVersion}")]
    private partial void LogPushConnected(Guid agentId, string backendHost, string protocolVersion);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "Push channel restored: AgentId={AgentId} BackendHost={BackendHost}")]
    private partial void LogPushRestored(Guid agentId, string backendHost);

    [LoggerMessage(EventId = 13, Level = LogLevel.Warning, Message = "Push channel connection lost, retrying: AgentId={AgentId} Reason={Reason}")]
    private partial void LogPushLost(Guid agentId, string reason);

    [LoggerMessage(EventId = 14, Level = LogLevel.Warning, Message = "Push channel closed: AgentId={AgentId} Reason={Reason}")]
    private partial void LogPushClosed(Guid agentId, string reason);

    [LoggerMessage(EventId = 15, Level = LogLevel.Warning,
        Message = "Push channel attempt failed: AgentId={AgentId} Failure={Failure} Reason={Reason} RetryCount={RetryCount} NextAttemptIn={Delay} BackendHost={BackendHost}")]
    private partial void LogPushFailed(Guid agentId, AgentBackendFailureKind failure, string reason, int retryCount, TimeSpan delay, string backendHost);

    [LoggerMessage(EventId = 16, Level = LogLevel.Information, Message = "Poll hint received: AgentId={AgentId} Reason={Reason}")]
    private partial void LogPollHint(Guid agentId, string reason);

    [LoggerMessage(EventId = 17, Level = LogLevel.Warning,
        Message = "Protocol notice received: AgentId={AgentId} Kind={Kind} Action={Action} ServerMinimum={ServerMinimum} ServerMaximum={ServerMaximum}")]
    private partial void LogProtocolNotice(Guid agentId, string kind, string action, string serverMinimum, string serverMaximum);

    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "Polling fallback active: AgentId={AgentId} PushUnavailableFor={Unavailable} PollPeriod={PollPeriod}")]
    private partial void LogPollingActivated(Guid agentId, TimeSpan unavailable, TimeSpan pollPeriod);

    [LoggerMessage(EventId = 21, Level = LogLevel.Information, Message = "Polling fallback idle, push channel is back: AgentId={AgentId}")]
    private partial void LogPollingDeactivated(Guid agentId);

    [LoggerMessage(EventId = 22, Level = LogLevel.Debug, Message = "Poll succeeded: AgentId={AgentId} Tasks={TaskCount} NextPollAfterSeconds={NextPollAfterSeconds} PollingActive={PollingActive}")]
    private partial void LogPollSucceeded(Guid agentId, int taskCount, int nextPollAfterSeconds, bool pollingActive);

    [LoggerMessage(EventId = 23, Level = LogLevel.Warning,
        Message = "Poll failed: AgentId={AgentId} Failure={Failure} Reason={Reason} ConsecutiveFailures={RetryCount} NextPollIn={Delay} BackendHost={BackendHost}")]
    private partial void LogPollFailed(Guid agentId, AgentBackendFailureKind failure, string reason, int retryCount, TimeSpan delay, string backendHost);

    [LoggerMessage(EventId = 30, Level = LogLevel.Debug, Message = "Heartbeat acknowledged: AgentId={AgentId} ServerTime={ServerTime}")]
    private partial void LogHeartbeatSucceeded(Guid agentId, DateTimeOffset serverTime);

    [LoggerMessage(EventId = 31, Level = LogLevel.Warning,
        Message = "Heartbeat failed: AgentId={AgentId} Failure={Failure} Reason={Reason} ConsecutiveFailures={RetryCount} NextHeartbeatIn={Delay} BackendHost={BackendHost}")]
    private partial void LogHeartbeatFailed(Guid agentId, AgentBackendFailureKind failure, string reason, int retryCount, TimeSpan delay, string backendHost);

    [LoggerMessage(EventId = 32, Level = LogLevel.Information,
        Message = "Server advertises a shorter heartbeat interval: AgentId={AgentId} Configured={Configured} ServerSeconds={ServerSeconds} Effective={Effective}")]
    private partial void LogHeartbeatIntervalLowered(Guid agentId, TimeSpan configured, int serverSeconds, TimeSpan effective);

    [LoggerMessage(EventId = 33, Level = LogLevel.Information, Message = "Capability sync completed: AgentId={AgentId}")]
    private partial void LogCapabilitySyncSucceeded(Guid agentId);

    [LoggerMessage(EventId = 34, Level = LogLevel.Warning,
        Message = "Capability sync did not complete, the backend will ask again on a later heartbeat: AgentId={AgentId} BackendHost={BackendHost}")]
    private partial void LogCapabilitySyncFailed(Guid agentId, string backendHost);

    [LoggerMessage(EventId = 40, Level = LogLevel.Warning,
        Message = "Connectivity paused: AgentId={AgentId} ChannelState={ChannelState} Action={Action} Reason={Reason} RetryIn={Delay} BackendHost={BackendHost} Protocol={ProtocolVersion}")]
    private partial void LogRestricted(Guid agentId, AgentChannelState channelState, string action, string reason, TimeSpan delay, string backendHost, string protocolVersion);

    [LoggerMessage(EventId = 41, Level = LogLevel.Information, Message = "Connectivity recovered from {ChannelState}: AgentId={AgentId}")]
    private partial void LogRecoveredCore(AgentChannelState channelState, Guid agentId);

    private void LogRecovered(Guid agentId, AgentChannelState previous) => LogRecoveredCore(previous, agentId);

    [LoggerMessage(EventId = 42, Level = LogLevel.Information, Message = "Agent identity reloaded from the credential store: AgentId={AgentId} CredentialGeneration={Generation}")]
    private partial void LogIdentityReloaded(Guid agentId, int generation);

    [LoggerMessage(EventId = 43, Level = LogLevel.Warning, Message = "Agent identity could not be reloaded, keeping the previous one: AgentId={AgentId} Reason={Reason}")]
    private partial void LogIdentityReloadFailed(Guid agentId, string reason);

    [LoggerMessage(EventId = 50, Level = LogLevel.Error, Message = "Unexpected error in {Component}: {ExceptionType}")]
    private partial void LogUnexpected(string component, string exceptionType);
}
