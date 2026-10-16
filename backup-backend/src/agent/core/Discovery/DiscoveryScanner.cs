using System.Globalization;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Discovery;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Core.Discovery;

/// <summary>
/// One discovery scan: passive detection (service managers, Docker CLI), a credential-free loopback handshake per candidate port, and
/// enrichment of the instances a user bound a credential reference to, through the engine's own
/// <see cref="IDatabaseBackupProvider.DiscoverAsync"/>. Everything is bounded: detector, probe and enrichment concurrency, every
/// connection's timeout, and the whole scan by the plan's budget. Provider and credential failures are reduced to fixed reason codes;
/// no exception text, connection string or secret ever reaches the report or a log line.
/// </summary>
public sealed partial class DiscoveryScanner
{
    private const int DetectorConcurrency = 3;
    private const int ProbeConcurrency = 4;
    private const int EnrichmentConcurrency = 2;
    private const int MinimumBudgetSeconds = 10;
    private const int MaximumBudgetSeconds = 600;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    // Ports probed even when no detector reported them; the only ports never taken from a detector.
    private static readonly (string Engine, int Port)[] WellKnownPorts =
    [
        (DiscoveryEngines.MsSql, DiscoveryParsers.MssqlDefaultPort),
        (DiscoveryEngines.PostgreSql, DiscoveryParsers.PostgreSqlDefaultPort),
    ];

    private readonly IReadOnlyList<IInstanceDetector> _detectors;
    private readonly IListenerProbe _probe;
    private readonly IReadOnlyDictionary<string, IDatabaseBackupProvider> _providers;
    private readonly IBackupTaskCredentialResolver _credentials;
    private readonly TimeProvider _time;
    private readonly ILogger<DiscoveryScanner> _logger;

    public DiscoveryScanner(
        IEnumerable<IInstanceDetector> detectors,
        IListenerProbe probe,
        IEnumerable<IDatabaseBackupProvider> providers,
        IBackupTaskCredentialResolver credentials,
        TimeProvider timeProvider,
        ILogger<DiscoveryScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(detectors);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _detectors = detectors.ToList();
        _probe = probe;
        _providers = providers.ToDictionary(provider => provider.Engine.Value, StringComparer.Ordinal);
        _credentials = credentials;
        _time = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Runs the scan within the plan's budget. Throws only <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/>
    /// fires; an expired budget yields a <see cref="DiscoveryOutcomes.Partial"/> (or, before any source finished, <see cref="DiscoveryOutcomes.Failed"/>) report.
    /// </summary>
    public async Task<AgentDiscoveryReport> ScanAsync(AgentDiscoveryPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(plan.TimeoutSeconds, MinimumBudgetSeconds, MaximumBudgetSeconds)), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        var scan = new ScanState(cancellationToken, linked.Token);

        var detections = await BoundedAsync(_detectors, DetectorConcurrency,
            detector => DetectAsync(detector, new DiscoveryDetectionContext(plan.IncludeDocker), scan)).ConfigureAwait(false);
        var statuses = detections.GroupBy(detection => detection.Source)
            .Select(group => Combine(group.Key, group.Select(detection => detection.Result)))
            .ToList();
        var instances = MergeByPort(detections.SelectMany(detection => detection.Result.Instances));

        var (probed, listenerStatus) = await ProbeAsync(instances, scan).ConfigureAwait(false);
        statuses.Add(listenerStatus);
        instances = AddReachability(instances, probed);

        var bindings = new Dictionary<string, AgentDiscoveryBinding>(StringComparer.Ordinal);
        foreach (var binding in plan.Bindings)
        {
            bindings.TryAdd(binding.InstanceKey, binding);
        }
        var enriched = await BoundedAsync(instances, EnrichmentConcurrency,
            instance => EnrichAsync(instance, bindings.GetValueOrDefault(instance.Key), plan.OnlyInstanceKey, scan)).ConfigureAwait(false);

        statuses.Sort(static (left, right) => string.CompareOrdinal(left.Source, right.Source));
        var finished = statuses.Any(status => status.Status != DiscoverySourceStates.Failed);
        var report = new AgentDiscoveryReport
        {
            Outcome = scan.TimedOut && !finished ? DiscoveryOutcomes.Failed
                : scan.TimedOut || statuses.Any(status => status.Status == DiscoverySourceStates.Failed) ? DiscoveryOutcomes.Partial
                : DiscoveryOutcomes.Completed,
            FailureCode = scan.TimedOut ? "scan_timeout" : null,
            Sources = statuses,
            Instances = scan.TimedOut && !finished ? [] : WithinReportBudget(enriched),
        };
        LogScanFinished(report.Outcome, report.Instances.Count, report.Sources.Count);
        return report;
    }

    /// <summary>
    /// A database list that would not fit in one report is omitted, never truncated: the backend replaces a stored list by the reported one, so a
    /// truncated list would turn the databases beyond the cut into "missing".
    /// </summary>
    private static List<AgentDiscoveredInstance> WithinReportBudget(IEnumerable<AgentDiscoveredInstance> instances)
    {
        var remaining = DiscoveryProtocol.MaximumDatabasesPerReport;
        var result = new List<AgentDiscoveredInstance>();
        foreach (var instance in instances)
        {
            if (instance.Databases is { } databases && databases.Count > Math.Min(remaining, DiscoveryProtocol.MaximumDatabasesPerInstance))
            {
                result.Add(instance with { Databases = null, AccessReasonCode = "too_many_databases" });
                continue;
            }
            remaining -= instance.Databases?.Count ?? 0;
            result.Add(instance);
        }
        return result;
    }

    // ---- detection --------------------------------------------------------------------------------------------------

    private async Task<(string Source, DetectionResult Result)> DetectAsync(IInstanceDetector detector, DiscoveryDetectionContext context, ScanState scan)
    {
        try
        {
            return (detector.Source, await detector.DetectAsync(context, scan.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!scan.Caller.IsCancellationRequested)
        {
            scan.TimedOut = true;
            return (detector.Source, DetectionResult.Failed("timeout"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogDetectorFailed(detector.Source, exception.GetType().Name);
            return (detector.Source, DetectionResult.Failed("detector_failed"));
        }
    }

    /// <summary>
    /// One status per source from the statuses of its detectors. The worst one wins (Failed, Unavailable, Scanned, Disabled): a source is
    /// reported <see cref="DiscoverySourceStates.Scanned"/> only when every detector of it could look, because the backend treats an
    /// instance absent from a scanned source as missing.
    /// </summary>
    private AgentDiscoverySourceStatus Combine(string source, IEnumerable<DetectionResult> results)
    {
        var worst = results.OrderByDescending(result => StateRank(result.Status)).First();
        LogSourceStatus(source, worst.Status, worst.ReasonCode ?? "none");
        return new AgentDiscoverySourceStatus { Source = source, Status = worst.Status, ReasonCode = worst.ReasonCode };
    }

    private static int StateRank(string status) => status switch
    {
        DiscoverySourceStates.Failed => 3,
        DiscoverySourceStates.Unavailable => 2,
        DiscoverySourceStates.Scanned => 1,
        _ => 0,
    };

    private static int SourceRank(string source) => source switch
    {
        DiscoverySources.Service => 0,
        DiscoverySources.Docker => 1,
        _ => 2,
    };

    /// <summary>
    /// One instance per loopback port and engine: a service registration wins the identity, other sources only fill what it lacks. Instances
    /// without a port cannot collide and stay as they are. The result is ordered and capped to what one report may carry.
    /// </summary>
    private static List<DetectedInstance> MergeByPort(IEnumerable<DetectedInstance> found)
    {
        var ordered = found.DistinctBy(instance => instance.Key, StringComparer.Ordinal)
            .OrderBy(instance => SourceRank(instance.Source)).ThenBy(instance => instance.Key, StringComparer.Ordinal).ToList();
        var merged = ordered.Where(instance => instance.Port is null).ToList();
        foreach (var group in ordered.Where(instance => instance.Port is not null).GroupBy(instance => (instance.Engine, instance.Port)))
        {
            var members = group.ToList();
            merged.Add(members.Skip(1).Aggregate(members[0], static (primary, other) => primary with
            {
                InstanceName = primary.InstanceName ?? other.InstanceName,
                Version = primary.Version ?? other.Version,
                Edition = primary.Edition ?? other.Edition,
                HostPlatform = primary.HostPlatform ?? other.HostPlatform,
                ContainerName = primary.ContainerName ?? other.ContainerName,
                ContainerImage = primary.ContainerImage ?? other.ContainerImage,
            }));
        }
        return [.. merged.OrderBy(instance => SourceRank(instance.Source)).ThenBy(instance => instance.Key, StringComparer.Ordinal)
            .Take(DiscoveryProtocol.MaximumInstancesPerReport)];
    }

    // ---- listener probe ---------------------------------------------------------------------------------------------

    private async Task<(Dictionary<(string Engine, int Port), ListenerProbeResult> Answers, AgentDiscoverySourceStatus Status)> ProbeAsync(
        List<DetectedInstance> instances, ScanState scan)
    {
        var candidates = WellKnownPorts
            .Concat(instances.Where(instance => instance.Port is not null).Select(instance => (Engine: instance.Engine, Port: instance.Port!.Value)))
            .Distinct().ToList();
        var failed = false;
        var results = await BoundedAsync(candidates, ProbeConcurrency, async candidate =>
        {
            try
            {
                return (Key: candidate, Result: (ListenerProbeResult?)await _probe.ProbeAsync(candidate.Engine, candidate.Port, scan.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!scan.Caller.IsCancellationRequested)
            {
                scan.TimedOut = true;
                failed = true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogDetectorFailed(DiscoverySources.Listener, exception.GetType().Name);
                failed = true;
            }
            return (Key: candidate, Result: (ListenerProbeResult?)null);
        }).ConfigureAwait(false);

        var answers = results.Where(result => result.Result is not null).ToDictionary(result => result.Key, result => result.Result!);
        var status = new AgentDiscoverySourceStatus
        {
            Source = DiscoverySources.Listener,
            Status = failed ? DiscoverySourceStates.Failed : DiscoverySourceStates.Scanned,
            ReasonCode = failed ? scan.TimedOut ? "timeout" : "probe_failed" : null,
        };
        LogSourceStatus(status.Source, status.Status, status.ReasonCode ?? "none");
        return (answers, status);
    }

    /// <summary>Applies what the handshakes learned: reachability for every instance with a port, plus the instances only a handshake found.</summary>
    private static List<DetectedInstance> AddReachability(List<DetectedInstance> instances, Dictionary<(string Engine, int Port), ListenerProbeResult> answers)
    {
        var result = new List<DetectedInstance>(instances.Count);
        foreach (var instance in instances)
        {
            result.Add(instance.Port is { } port && answers.TryGetValue((instance.Engine, port), out var answer)
                ? instance with { Version = instance.Version ?? answer.Version, Reachability = answer.Recognized ? DiscoveryReachability.Reachable : answer.Connected ? DiscoveryReachability.Unknown : DiscoveryReachability.Unreachable }
                : instance);
        }
        foreach (var ((engine, port), answer) in answers.OrderBy(entry => entry.Key.Port))
        {
            if (answer.Recognized && !instances.Any(instance => instance.Engine == engine && instance.Port == port)
                && result.Count < DiscoveryProtocol.MaximumInstancesPerReport)
            {
                result.Add(new DetectedInstance
                {
                    Engine = engine,
                    Source = DiscoverySources.Listener,
                    Key = string.Create(CultureInfo.InvariantCulture, $"{engine}|listener|{port}"),
                    DisplayName = string.Create(CultureInfo.InvariantCulture, $"{(engine == DiscoveryEngines.MsSql ? "SQL Server" : "PostgreSQL")} on localhost:{port}"),
                    Port = port,
                    Version = answer.Version,
                    Reachability = DiscoveryReachability.Reachable,
                });
            }
        }
        return result;
    }

    // ---- enrichment -------------------------------------------------------------------------------------------------

    private async Task<AgentDiscoveredInstance> EnrichAsync(DetectedInstance instance, AgentDiscoveryBinding? binding, string? onlyInstanceKey, ScanState scan)
    {
        var basic = new AgentDiscoveredInstance
        {
            InstanceKey = instance.Key,
            Engine = instance.Engine,
            Source = instance.Source,
            DisplayName = instance.DisplayName,
            InstanceName = instance.InstanceName,
            Host = instance.Host,
            Port = instance.Port,
            Version = instance.Version,
            Edition = instance.Edition,
            HostPlatform = instance.HostPlatform,
            ContainerName = instance.ContainerName,
            ContainerImage = instance.ContainerImage,
            Reachability = instance.Reachability,
        };

        // A targeted scan verifies one instance; the others keep whatever the backend knows about their access.
        if (onlyInstanceKey is not null && !string.Equals(onlyInstanceKey, instance.Key, StringComparison.Ordinal))
        {
            return basic;
        }
        if (binding is null || (binding.CredentialReference is not { Length: > 0 } && !binding.IntegratedSecurity))
        {
            return basic with { Access = DiscoveryAccess.CredentialsRequired };
        }
        if (scan.Token.IsCancellationRequested)
        {
            scan.Caller.ThrowIfCancellationRequested();
            scan.TimedOut = true;
            return basic with { AccessReasonCode = "scan_timeout" };
        }
        if (instance.Port is null && !(instance.Engine == DiscoveryEngines.MsSql && instance.InstanceName is not null))
        {
            return basic with { Access = DiscoveryAccess.Unreachable, AccessReasonCode = instance.ReasonCode ?? "no_endpoint" };
        }
        if (!_providers.TryGetValue(instance.Engine, out var provider))
        {
            return basic with { Access = DiscoveryAccess.Error, AccessReasonCode = "provider_unavailable" };
        }

        try
        {
            var credential = await ResolveCredentialAsync(binding, scan.Token).ConfigureAwait(false);
            if (credential is null)
            {
                LogEnrichmentFailed(instance.Key, "credential_unavailable");
                return basic with { Access = DiscoveryAccess.Error, AccessReasonCode = "credential_unavailable" };
            }
            var endpoint = instance.Port is { } port
                ? new DatabaseEndpoint(instance.Host, port)
                : new DatabaseEndpoint(instance.Host, null, instance.InstanceName);
            var connection = new DatabaseConnectionSettings(endpoint,
                new DatabaseConnectionOptions(ConnectTimeout, CommandTimeout, binding.Encrypt, binding.TrustServerCertificate));
            var discovery = await provider.DiscoverAsync(connection, credential, scan.Token).ConfigureAwait(false);
            return Authenticated(basic, provider, discovery);
        }
        catch (OperationCanceledException) when (!scan.Caller.IsCancellationRequested)
        {
            scan.TimedOut = true;
            LogEnrichmentFailed(instance.Key, "scan_timeout");
            return basic with { AccessReasonCode = "scan_timeout" };
        }
        catch (DatabaseProviderException exception)
        {
            // A server whose handshake just answered is not unreachable: the connection failed for another reason (typically TLS on a server without it).
            var answered = basic.Reachability == DiscoveryReachability.Reachable;
            var (access, reason) = exception.Kind switch
            {
                DatabaseFailureKind.AuthenticationFailed => (DiscoveryAccess.AuthenticationFailed, "authentication_failed"),
                DatabaseFailureKind.Unreachable or DatabaseFailureKind.Timeout when answered => (DiscoveryAccess.Error, "connection_failed"),
                DatabaseFailureKind.Unreachable => (DiscoveryAccess.Unreachable, "unreachable"),
                DatabaseFailureKind.Timeout => (DiscoveryAccess.Unreachable, "timeout"),
                DatabaseFailureKind.PermissionDenied => (DiscoveryAccess.Error, "permission_denied"),
                DatabaseFailureKind.InvalidConfiguration => (DiscoveryAccess.Error, "invalid_configuration"),
                _ => (DiscoveryAccess.Error, "unexpected"),
            };
            LogEnrichmentFailed(instance.Key, reason);
            return basic with
            {
                Access = access,
                AccessReasonCode = reason,
                Reachability = exception.Kind is DatabaseFailureKind.Unreachable or DatabaseFailureKind.Timeout && !answered ? DiscoveryReachability.Unreachable
                    : exception.Kind is DatabaseFailureKind.AuthenticationFailed or DatabaseFailureKind.PermissionDenied ? DiscoveryReachability.Reachable
                    : basic.Reachability,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The type only: a provider message can quote the connection or the login.
            LogEnrichmentFailed(instance.Key, exception.GetType().Name);
            return basic with { Access = DiscoveryAccess.Error, AccessReasonCode = "unexpected" };
        }
    }

    /// <summary>Integrated security needs no secret; a reference is resolved at use time. Null when the reference cannot be resolved.</summary>
    private async Task<DatabaseCredential?> ResolveCredentialAsync(AgentDiscoveryBinding binding, CancellationToken cancellationToken)
    {
        if (binding.IntegratedSecurity)
        {
            return IntegratedDatabaseCredential.Instance;
        }
        try
        {
            return await _credentials.ResolveAsync(binding.CredentialReference!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private static AgentDiscoveredInstance Authenticated(AgentDiscoveredInstance basic, IDatabaseBackupProvider provider, DatabaseInstanceDiscovery discovery)
    {
        var fullBackup = new DatabaseCapabilityId(provider.Engine.Value + ".backup.full");
        var (support, reason) = Support(discovery.Capabilities, fullBackup);
        return basic with
        {
            Version = discovery.Instance.Version ?? basic.Version,
            Edition = discovery.Instance.Edition ?? basic.Edition,
            HostPlatform = discovery.Instance.HostPlatform ?? basic.HostPlatform,
            Reachability = DiscoveryReachability.Reachable,
            Access = DiscoveryAccess.Authenticated,
            AccessReasonCode = null,
            FullBackup = support,
            FullBackupReasonCode = reason,
            Databases = [.. discovery.Databases.Select(database =>
            {
                var (databaseSupport, databaseReason) = Support(database.Capabilities, fullBackup);
                return new AgentDiscoveredDatabase
                {
                    Name = database.Name,
                    IsSystem = database.IsSystem,
                    State = database.Availability switch
                    {
                        DatabaseAvailability.Online => DiscoveryDatabaseStates.Online,
                        DatabaseAvailability.Unavailable => DiscoveryDatabaseStates.Offline,
                        _ => DiscoveryDatabaseStates.Unknown,
                    },
                    FullBackup = databaseSupport,
                    FullBackupReasonCode = databaseReason,
                };
            })],
        };
    }

    private static (string Support, string? ReasonCode) Support(DatabaseCapabilitySet capabilities, DatabaseCapabilityId id) =>
        (capabilities.SupportOf(id) switch
        {
            DatabaseCapabilitySupport.Supported => DiscoverySupportNames.Supported,
            DatabaseCapabilitySupport.Unsupported => DiscoverySupportNames.Unsupported,
            _ => DiscoverySupportNames.Unknown,
        }, capabilities.TryGet(id, out var capability) ? capability.ReasonCode : null);

    // ---- plumbing ---------------------------------------------------------------------------------------------------

    /// <summary>Runs <paramref name="work"/> for every item with at most <paramref name="concurrency"/> in flight, keeping the input order. The work handles its own cancellation.</summary>
    private static async Task<TResult[]> BoundedAsync<TItem, TResult>(IReadOnlyCollection<TItem> items, int concurrency, Func<TItem, Task<TResult>> work)
    {
        using var gate = new SemaphoreSlim(concurrency);
        return await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await work(item).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
    }

    private sealed class ScanState(CancellationToken caller, CancellationToken token)
    {
        /// <summary>Shutdown: stops everything and propagates.</summary>
        public CancellationToken Caller { get; } = caller;

        /// <summary>Shutdown or the plan's budget.</summary>
        public CancellationToken Token { get; } = token;

        public volatile bool TimedOut;
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Information, Message = "Discovery source finished: Source={Source} Status={Status} Reason={Reason}")]
    private partial void LogSourceStatus(string source, string status, string reason);

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning, Message = "Discovery detector failed: Source={Source} ExceptionType={ExceptionType}")]
    private partial void LogDetectorFailed(string source, string exceptionType);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information, Message = "Discovery could not read an instance: InstanceKey={InstanceKey} Reason={Reason}")]
    private partial void LogEnrichmentFailed(string instanceKey, string reason);

    [LoggerMessage(EventId = 103, Level = LogLevel.Information, Message = "Discovery scan finished: Outcome={Outcome} Instances={Instances} Sources={Sources}")]
    private partial void LogScanFinished(string outcome, int instances, int sources);
}
