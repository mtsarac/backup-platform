using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Contracts.Discovery;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Core.Discovery;

/// <summary>
/// Talks to the backend about discovery scans. Same contract as <see cref="IAgentCapabilityClient"/>: the identity is passed per call,
/// every non-success outcome (including network errors) is an <see cref="AgentBackendFailure"/>, and only cancellation throws.
/// </summary>
public interface IAgentDiscoveryClient
{
    /// <summary>Claims a due scan: a success with a null value means nothing is due (or a scan already runs).</summary>
    Task<AgentBackendResult<AgentDiscoveryPlan?>> ClaimAsync(AgentLocalIdentity identity, CancellationToken cancellationToken);

    Task<AgentBackendResult<bool>> ReportAsync(AgentLocalIdentity identity, Guid scanId, AgentDiscoveryReport report, CancellationToken cancellationToken);
}

/// <summary>What the connectivity service starts when a heartbeat answer asks for a discovery scan.</summary>
public interface IDiscoveryScanRunner
{
    /// <summary>Claims, scans and reports one scan. Never throws except for cancellation; a failure is logged and, once claimed, reported.</summary>
    Task RunAsync(AgentLocalIdentity identity, CancellationToken cancellationToken);
}

public sealed partial class DiscoveryScanRunner : IDiscoveryScanRunner
{
    private readonly IAgentDiscoveryClient _client;
    private readonly DiscoveryScanner _scanner;
    private readonly ILogger<DiscoveryScanRunner> _logger;
    private readonly TimeProvider _time;
    private int _running;

    /// <summary>Attempts at delivering one report; a scan is expensive and an unreported claim only expires after ten minutes.</summary>
    private const int ReportAttempts = 4;
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(60);

    public DiscoveryScanRunner(IAgentDiscoveryClient client, DiscoveryScanner scanner, ILogger<DiscoveryScanRunner> logger, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(logger);
        _client = client;
        _scanner = scanner;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task RunAsync(AgentLocalIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        // Single flight: a second request while a scan runs is dropped, the backend asks again on a later heartbeat.
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }
        try
        {
            var claim = await _client.ClaimAsync(identity, cancellationToken).ConfigureAwait(false);
            if (!claim.IsSuccess)
            {
                LogClaimFailed(identity.AgentId, claim.Failure.Kind);
                return;
            }
            if (claim.Value is not { } plan)
            {
                return;
            }

            AgentDiscoveryReport report;
            try
            {
                report = await _scanner.ScanAsync(plan, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // The type only: the message could quote a connection string.
                LogScanFailed(plan.ScanId, exception.GetType().Name);
                report = new AgentDiscoveryReport { Outcome = DiscoveryOutcomes.Failed, FailureCode = "scan_failed" };
            }

            var reported = await ReportWithRetryAsync(identity, plan.ScanId, report, cancellationToken).ConfigureAwait(false);
            if (reported.IsSuccess)
            {
                LogReported(plan.ScanId, report.Outcome);
            }
            else
            {
                LogReportFailed(plan.ScanId, reported.Failure.Kind);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogUnexpected(exception.GetType().Name);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>Only a throttled or transient failure is retried; a refusal (stale scan, bad request, credential) cannot succeed later.</summary>
    private async Task<AgentBackendResult<bool>> ReportWithRetryAsync(AgentLocalIdentity identity, Guid scanId, AgentDiscoveryReport report, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await _client.ReportAsync(identity, scanId, report, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess || attempt == ReportAttempts
                || result.Failure.Kind is not (AgentBackendFailureKind.RateLimited or AgentBackendFailureKind.Transient))
            {
                return result;
            }
            var delay = result.Failure.RetryAfter is { } requested && requested > TimeSpan.Zero ? requested : DefaultRetryDelay * attempt;
            await Task.Delay(delay < MaximumRetryDelay ? delay : MaximumRetryDelay, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 110, Level = LogLevel.Warning, Message = "Discovery scan could not be claimed: AgentId={AgentId} Failure={Failure}")]
    private partial void LogClaimFailed(Guid agentId, AgentBackendFailureKind failure);

    [LoggerMessage(EventId = 111, Level = LogLevel.Warning, Message = "Discovery scan failed, reporting the failure: ScanId={ScanId} ExceptionType={ExceptionType}")]
    private partial void LogScanFailed(Guid scanId, string exceptionType);

    [LoggerMessage(EventId = 112, Level = LogLevel.Information, Message = "Discovery report accepted: ScanId={ScanId} Outcome={Outcome}")]
    private partial void LogReported(Guid scanId, string outcome);

    [LoggerMessage(EventId = 113, Level = LogLevel.Warning, Message = "Discovery report was not accepted: ScanId={ScanId} Failure={Failure}")]
    private partial void LogReportFailed(Guid scanId, AgentBackendFailureKind failure);

    [LoggerMessage(EventId = 114, Level = LogLevel.Error, Message = "Unexpected error in discovery: {ExceptionType}")]
    private partial void LogUnexpected(string exceptionType);
}
