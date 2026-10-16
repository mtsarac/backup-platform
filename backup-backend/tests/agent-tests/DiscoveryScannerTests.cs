using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;
using BackupPlatform.Agent.Core.Discovery;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Discovery;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.AgentTests;

public sealed class DiscoveryScannerTests
{
    private const string Secret = "S3cr3t-Pass-9f2c";

    // ---- fakes ----------------------------------------------------------------------------------------------------

    private sealed class FakeDetector(string source, Func<DiscoveryDetectionContext, CancellationToken, Task<DetectionResult>> detect) : IInstanceDetector
    {
        public FakeDetector(string source, DetectionResult result)
            : this(source, (_, _) => Task.FromResult(result))
        {
        }

        public string Source => source;

        public Task<DetectionResult> DetectAsync(DiscoveryDetectionContext context, CancellationToken cancellationToken) => detect(context, cancellationToken);
    }

    private sealed class FakeProbe : IListenerProbe
    {
        public ConcurrentDictionary<(string Engine, int Port), ListenerProbeResult> Answers { get; } = new();

        public ConcurrentQueue<(string Engine, int Port)> Calls { get; } = new();

        public Func<string, int, CancellationToken, Task<ListenerProbeResult>>? Override { get; set; }

        public Task<ListenerProbeResult> ProbeAsync(string engine, int port, CancellationToken cancellationToken)
        {
            Calls.Enqueue((engine, port));
            return Override is { } custom
                ? custom(engine, port, cancellationToken)
                : Task.FromResult(Answers.GetValueOrDefault((engine, port), new ListenerProbeResult(false, false, null)));
        }
    }

    private sealed class FakeProvider(DatabaseEngineId engine) : IDatabaseBackupProvider
    {
        private int _active;
        private int _peak;

        public DatabaseEngineId Engine => engine;

        public Func<DatabaseConnectionSettings, DatabaseCredential, CancellationToken, Task<DatabaseInstanceDiscovery>> OnDiscover { get; set; } =
            (_, _, _) => Task.FromResult(Discovery(engine));

        public ConcurrentQueue<(DatabaseConnectionSettings Connection, DatabaseCredential Credential)> Calls { get; } = new();

        public int PeakConcurrency => Volatile.Read(ref _peak);

        public async Task<DatabaseInstanceDiscovery> DiscoverAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, CancellationToken cancellationToken)
        {
            Calls.Enqueue((connection, credential));
            var active = Interlocked.Increment(ref _active);
            InterlockedMax(ref _peak, active);
            try
            {
                return await OnDiscover(connection, credential, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public Task<BackupPreCheckReport> PreCheckFullBackupAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, FullBackupPreCheckRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<FullBackupResult> ExecuteFullBackupAsync(DatabaseConnectionSettings connection, DatabaseCredential credential, FullBackupRequest request, IProgress<BackupProgress>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }

    private sealed class FakeResolver : IBackupTaskCredentialResolver
    {
        public ConcurrentQueue<string> References { get; } = new();

        public Func<string, DatabaseCredential> Resolve { get; set; } = static _ => new PasswordDatabaseCredential("scanner", new DatabaseSecret(Secret));

        public ValueTask<DatabaseCredential> ResolveAsync(string reference, CancellationToken cancellationToken)
        {
            References.Enqueue(reference);
            return ValueTask.FromResult(Resolve(reference));
        }
    }

    private static DatabaseCapabilitySet Capabilities(string engine, DatabaseCapabilitySupport support, string reason) =>
        new([new DatabaseCapability(new DatabaseCapabilityId(engine + ".backup.full"), support, reason, "Fixed explanation.")]);

    private static DatabaseInstanceDiscovery Discovery(DatabaseEngineId engine, params DatabaseInfo[] databases) => new(
        new DatabaseInstanceInfo(engine, "server", null, "16.0.4135.4", "Developer Edition", "Linux"),
        Capabilities(engine.Value, DatabaseCapabilitySupport.Supported, "supported"),
        databases);

    private static DatabaseInfo Database(string name, bool system, DatabaseAvailability availability, DatabaseCapabilitySupport support, string reason, string engine = "mssql") =>
        new(name, system, availability, Capabilities(engine, support, reason));

    private static DetectedInstance Mssql(string key, int? port, string source = DiscoverySources.Service, string? name = null) => new()
    {
        Engine = DiscoveryEngines.MsSql,
        Source = source,
        Key = key,
        DisplayName = key,
        InstanceName = name,
        Port = port,
    };

    private static DetectedInstance Postgres(string key, int? port, string source = DiscoverySources.Service) => new()
    {
        Engine = DiscoveryEngines.PostgreSql,
        Source = source,
        Key = key,
        DisplayName = key,
        Port = port,
    };

    private sealed class Rig
    {
        public FakeProbe Probe { get; } = new();
        public FakeProvider Mssql { get; } = new(DatabaseEngineId.MsSql);
        public FakeProvider Postgres { get; } = new(DatabaseEngineId.PostgreSql);
        public FakeResolver Resolver { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public CaptureLoggerProvider Logs { get; } = new();
        public List<IInstanceDetector> Detectors { get; } = [];

        public DiscoveryScanner Scanner => new(Detectors, Probe, [Mssql, Postgres], Resolver, Time, Logs.CreateFactory().CreateLogger<DiscoveryScanner>());
    }

    private static AgentDiscoveryBinding Binding(string key, string engine = DiscoveryEngines.MsSql, string? reference = "sa-ref", bool integrated = false, bool encrypt = true, bool trust = false) =>
        new() { InstanceKey = key, Engine = engine, CredentialReference = reference, IntegratedSecurity = integrated, Encrypt = encrypt, TrustServerCertificate = trust };

    private static Task<AgentDiscoveryReport> ScanAsync(Rig rig, AgentDiscoveryPlan? plan = null) => rig.Scanner.ScanAsync(plan ?? new AgentDiscoveryPlan(), default);

    // ---- detection, merge, status ---------------------------------------------------------------------------------

    [Fact]
    public async Task Instances_found_by_several_sources_merge_by_loopback_port_and_the_service_keeps_the_identity()
    {
        var rig = new Rig();
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service, DetectionResult.Scanned([Mssql("mssql|service|default", 1433) with { Edition = "Express" }])));
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Docker, DetectionResult.Scanned([
            Mssql("mssql|docker|sql1|1433", 1433, DiscoverySources.Docker) with { ContainerName = "sql1" },
            Mssql("mssql|docker|sql2|1433", 14333, DiscoverySources.Docker) with { ContainerName = "sql2" }])));
        rig.Probe.Answers[(DiscoveryEngines.MsSql, 1433)] = new ListenerProbeResult(true, true, "16.0.4135");
        rig.Probe.Answers[(DiscoveryEngines.MsSql, 14333)] = new ListenerProbeResult(true, true, "15.0.2000");

        var report = await ScanAsync(rig);

        Assert.Equal(DiscoveryOutcomes.Completed, report.Outcome);
        Assert.Equal(2, report.Instances.Count);
        var service = Assert.Single(report.Instances, instance => instance.InstanceKey == "mssql|service|default");
        Assert.Equal(DiscoverySources.Service, service.Source);
        Assert.Equal("16.0.4135", service.Version);
        Assert.Equal("sql1", service.ContainerName);
        Assert.Equal("Express", service.Edition);
        Assert.Equal(DiscoveryReachability.Reachable, service.Reachability);
        Assert.Contains(report.Instances, instance => instance.InstanceKey == "mssql|docker|sql2|1433" && instance.Version == "15.0.2000");
    }

    [Fact]
    public async Task Only_well_known_ports_and_ports_found_by_detectors_are_probed()
    {
        var rig = new Rig();
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service, DetectionResult.Scanned([Postgres("postgres|service|16-main", 5433), Mssql("mssql|service|X", null)])));

        await ScanAsync(rig);

        (string Engine, int Port)[] expected = [(DiscoveryEngines.MsSql, 1433), (DiscoveryEngines.PostgreSql, 5432), (DiscoveryEngines.PostgreSql, 5433)];
        Assert.Equal(expected, rig.Probe.Calls.OrderBy(call => call.Port).ToArray());
    }

    [Fact]
    public async Task A_handshake_on_a_well_known_port_without_any_detector_hit_is_a_listener_instance()
    {
        var rig = new Rig();
        rig.Probe.Answers[(DiscoveryEngines.PostgreSql, 5432)] = new ListenerProbeResult(true, true, null);
        rig.Probe.Answers[(DiscoveryEngines.MsSql, 1433)] = new ListenerProbeResult(true, false, null);

        var report = await ScanAsync(rig);

        var instance = Assert.Single(report.Instances);
        Assert.Equal("postgres|listener|5432", instance.InstanceKey);
        Assert.Equal(DiscoverySources.Listener, instance.Source);
        Assert.Equal(5432, instance.Port);
        Assert.Equal(DiscoveryReachability.Reachable, instance.Reachability);
        Assert.Equal(DiscoveryAccess.CredentialsRequired, instance.Access);
        Assert.Null(instance.Databases);
        Assert.Equal(DiscoverySourceStates.Scanned, Assert.Single(report.Sources).Status);
    }

    [Fact]
    public async Task Reachability_follows_the_handshake()
    {
        var rig = new Rig();
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service, DetectionResult.Scanned([
            Mssql("mssql|service|A", 1433), Mssql("mssql|service|B", 1500), Mssql("mssql|service|C", 1600), Mssql("mssql|service|D", null)])));
        rig.Probe.Answers[(DiscoveryEngines.MsSql, 1433)] = new ListenerProbeResult(true, true, null);
        rig.Probe.Answers[(DiscoveryEngines.MsSql, 1500)] = new ListenerProbeResult(true, false, null);

        var report = await ScanAsync(rig);

        string Reach(string key) => report.Instances.Single(instance => instance.InstanceKey == key).Reachability;
        Assert.Equal(DiscoveryReachability.Reachable, Reach("mssql|service|A"));
        Assert.Equal(DiscoveryReachability.Unknown, Reach("mssql|service|B"));
        Assert.Equal(DiscoveryReachability.Unreachable, Reach("mssql|service|C"));
        Assert.Equal(DiscoveryReachability.Unknown, Reach("mssql|service|D"));
    }

    [Fact]
    public async Task Unavailable_sources_by_design_still_complete_while_a_failed_source_makes_the_scan_partial()
    {
        var unavailable = new Rig();
        unavailable.Detectors.Add(new FakeDetector(DiscoverySources.Docker, DetectionResult.Unavailable("docker_not_found")));
        var failed = new Rig();
        failed.Detectors.Add(new FakeDetector(DiscoverySources.Docker, DetectionResult.Failed("docker_timeout")));

        var completed = await ScanAsync(unavailable);
        var partial = await ScanAsync(failed);

        Assert.Equal(DiscoveryOutcomes.Completed, completed.Outcome);
        Assert.Contains(completed.Sources, status => status is { Source: DiscoverySources.Docker, Status: DiscoverySourceStates.Unavailable, ReasonCode: "docker_not_found" });
        Assert.Equal(DiscoveryOutcomes.Partial, partial.Outcome);
        Assert.Contains(partial.Sources, status => status is { Source: DiscoverySources.Docker, Status: DiscoverySourceStates.Failed, ReasonCode: "docker_timeout" });
        Assert.Null(partial.FailureCode);
    }

    [Fact]
    public async Task The_docker_flag_of_the_plan_reaches_the_detectors()
    {
        var rig = new Rig();
        bool? included = null;
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Docker, (context, _) =>
        {
            included = context.IncludeDocker;
            return Task.FromResult(DetectionResult.Disabled("disabled_by_settings"));
        }));

        var report = await ScanAsync(rig, new AgentDiscoveryPlan { IncludeDocker = false });

        Assert.False(included);
        Assert.Contains(report.Sources, status => status is { Source: DiscoverySources.Docker, Status: DiscoverySourceStates.Disabled });
        Assert.Equal(DiscoveryOutcomes.Completed, report.Outcome);
    }

    [Fact]
    public async Task One_status_per_source_and_the_worst_detector_status_wins()
    {
        var rig = new Rig();
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service, DetectionResult.Scanned([Mssql("mssql|service|default", 1433)])));
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service, DetectionResult.Unavailable("registry_access_denied")));

        var report = await ScanAsync(rig);

        Assert.Equal(2, report.Sources.Count);
        var service = Assert.Single(report.Sources, status => status.Source == DiscoverySources.Service);
        Assert.Equal(DiscoverySourceStates.Unavailable, service.Status);
        Assert.Equal("registry_access_denied", service.ReasonCode);
        Assert.Single(report.Instances);
    }

    [Fact]
    public async Task A_throwing_detector_is_a_failed_source_with_a_fixed_code_and_no_message()
    {
        var rig = new Rig();
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service, (_, _) => throw new InvalidOperationException($"boom Password={Secret}")));

        var report = await ScanAsync(rig);

        Assert.Equal(DiscoveryOutcomes.Partial, report.Outcome);
        Assert.Contains(report.Sources, status => status is { Source: DiscoverySources.Service, Status: DiscoverySourceStates.Failed, ReasonCode: "detector_failed" });
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(report), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, rig.Logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Instances_are_capped_to_what_one_report_may_carry()
    {
        var rig = new Rig();
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service,
            DetectionResult.Scanned([.. Enumerable.Range(0, DiscoveryProtocol.MaximumInstancesPerReport + 6).Select(i => Mssql($"mssql|service|I{i:D3}", null))])));

        var report = await ScanAsync(rig);

        Assert.Equal(DiscoveryProtocol.MaximumInstancesPerReport, report.Instances.Count);
    }

    // ---- the plan's budget ----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_expired_budget_with_one_finished_source_is_partial_and_keeps_what_was_found()
    {
        var rig = new Rig();
        var hung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service, DetectionResult.Scanned([Mssql("mssql|service|default", 1433)])));
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Docker, async (_, token) =>
        {
            hung.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return DetectionResult.Scanned([]);
        }));

        var scan = ScanAsync(rig, new AgentDiscoveryPlan { TimeoutSeconds = 30 });
        await hung.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Time.Advance(TimeSpan.FromSeconds(30));
        var report = await scan.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DiscoveryOutcomes.Partial, report.Outcome);
        Assert.Equal("scan_timeout", report.FailureCode);
        Assert.Contains(report.Sources, status => status is { Source: DiscoverySources.Docker, Status: DiscoverySourceStates.Failed, ReasonCode: "timeout" });
        Assert.Single(report.Instances);
    }

    [Fact]
    public async Task A_budget_that_expires_before_any_source_finished_fails_the_scan()
    {
        var rig = new Rig();
        var hung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service, async (_, token) =>
        {
            hung.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return DetectionResult.Scanned([]);
        }));
        rig.Probe.Override = (_, _, token) => Task.Delay(Timeout.InfiniteTimeSpan, token).ContinueWith(_ => new ListenerProbeResult(false, false, null), token);

        var scan = ScanAsync(rig, new AgentDiscoveryPlan { TimeoutSeconds = 30 });
        await hung.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Time.Advance(TimeSpan.FromSeconds(30));
        var report = await scan.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DiscoveryOutcomes.Failed, report.Outcome);
        Assert.Equal("scan_timeout", report.FailureCode);
        Assert.Empty(report.Instances);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_a_result_it_propagates()
    {
        var rig = new Rig();
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service, (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token).ContinueWith(_ => DetectionResult.Scanned([]), token)));
        using var cts = new CancellationTokenSource();

        var scan = rig.Scanner.ScanAsync(new AgentDiscoveryPlan(), cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
    }

    // ---- enrichment and access ------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_a_binding_an_instance_is_reported_as_credentials_required_and_no_provider_is_called()
    {
        var rig = new Rig();
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service, DetectionResult.Scanned([Mssql("mssql|service|default", 1433)])));

        var report = await ScanAsync(rig);

        var instance = Assert.Single(report.Instances);
        Assert.Equal(DiscoveryAccess.CredentialsRequired, instance.Access);
        Assert.Null(instance.Databases);
        Assert.Equal(DiscoverySupportNames.Unknown, instance.FullBackup);
        Assert.Empty(rig.Mssql.Calls);
        Assert.Empty(rig.Resolver.References);
    }

    [Fact]
    public async Task A_bound_instance_is_enriched_through_the_provider_with_the_binding_flags_and_finite_timeouts()
    {
        var rig = new Rig();
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service, DetectionResult.Scanned([Mssql("mssql|service|default", 1433)])));
        rig.Mssql.OnDiscover = (_, _, _) => Task.FromResult(Discovery(DatabaseEngineId.MsSql,
            Database("master", true, DatabaseAvailability.Online, DatabaseCapabilitySupport.Unsupported, "system_database"),
            Database("Sales", false, DatabaseAvailability.Online, DatabaseCapabilitySupport.Supported, "supported"),
            Database("Old", false, DatabaseAvailability.Unavailable, DatabaseCapabilitySupport.Unknown, "metadata_missing"),
            Database("Weird", false, DatabaseAvailability.Unknown, DatabaseCapabilitySupport.Supported, "supported")));
        var plan = new AgentDiscoveryPlan { Bindings = [Binding("mssql|service|default", encrypt: false, trust: true)] };

        var report = await ScanAsync(rig, plan);

        var instance = Assert.Single(report.Instances);
        Assert.Equal(DiscoveryAccess.Authenticated, instance.Access);
        Assert.Null(instance.AccessReasonCode);
        Assert.Equal("16.0.4135.4", instance.Version);
        Assert.Equal("Developer Edition", instance.Edition);
        Assert.Equal("Linux", instance.HostPlatform);
        Assert.Equal(DiscoverySupportNames.Supported, instance.FullBackup);
        Assert.Equal("supported", instance.FullBackupReasonCode);
        Assert.Equal(DiscoveryReachability.Reachable, instance.Reachability);
        Assert.Equal(["master", "Sales", "Old", "Weird"], instance.Databases!.Select(database => database.Name));
        var master = instance.Databases![0];
        Assert.True(master.IsSystem);
        Assert.Equal(DiscoverySupportNames.Unsupported, master.FullBackup);
        Assert.Equal("system_database", master.FullBackupReasonCode);
        Assert.Equal([DiscoveryDatabaseStates.Online, DiscoveryDatabaseStates.Online, DiscoveryDatabaseStates.Offline, DiscoveryDatabaseStates.Unknown],
            instance.Databases.Select(database => database.State));
        Assert.Equal(DiscoverySupportNames.Unknown, instance.Databases[2].FullBackup);

        var call = Assert.Single(rig.Mssql.Calls);
        Assert.Equal("localhost", call.Connection.Endpoint.Host);
        Assert.Equal(1433, call.Connection.Endpoint.Port);
        Assert.False(call.Connection.Options.RequireEncryption);
        Assert.True(call.Connection.Options.TrustServerCertificate);
        Assert.InRange(call.Connection.Options.ConnectTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        Assert.InRange(call.Connection.Options.CommandTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        Assert.Equal("scanner", Assert.IsType<PasswordDatabaseCredential>(call.Credential).UserName);
        Assert.Equal("sa-ref", Assert.Single(rig.Resolver.References));
    }

    [Fact]
    public async Task Integrated_security_needs_no_resolver_and_the_postgresql_provider_serves_postgresql()
    {
        var rig = new Rig();
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service, DetectionResult.Scanned([Mssql("mssql|service|default", 1433), Postgres("postgres|service|16-main", 5433)])));
        var plan = new AgentDiscoveryPlan
        {
            Bindings = [Binding("mssql|service|default", reference: null, integrated: true), Binding("postgres|service|16-main", DiscoveryEngines.PostgreSql, "pg-ref")],
        };

        var report = await ScanAsync(rig, plan);

        Assert.All(report.Instances, instance => Assert.Equal(DiscoveryAccess.Authenticated, instance.Access));
        Assert.Same(IntegratedDatabaseCredential.Instance, Assert.Single(rig.Mssql.Calls).Credential);
        var postgres = Assert.Single(rig.Postgres.Calls);
        Assert.Equal(5433, postgres.Connection.Endpoint.Port);
        Assert.Equal("pg-ref", Assert.Single(rig.Resolver.References));
    }

    [Theory]
    [InlineData(DatabaseFailureKind.Unreachable)]
    [InlineData(DatabaseFailureKind.Timeout)]
    public async Task A_server_that_answered_the_handshake_is_not_reported_unreachable_when_only_the_login_connection_fails(DatabaseFailureKind kind)
    {
        var rig = new Rig();
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service, DetectionResult.Scanned([Mssql("mssql|service|default", 1433)])));
        rig.Probe.Answers[(DiscoveryEngines.MsSql, 1433)] = new ListenerProbeResult(true, true, "17.0.1");
        rig.Mssql.OnDiscover = (_, _, _) => throw new DatabaseProviderException(kind, "user message", new InvalidOperationException("tls"));

        var instance = Assert.Single((await ScanAsync(rig, new AgentDiscoveryPlan { Bindings = [Binding("mssql|service|default")] })).Instances);

        Assert.Equal(DiscoveryAccess.Error, instance.Access);
        Assert.Equal("connection_failed", instance.AccessReasonCode);
        Assert.Equal(DiscoveryReachability.Reachable, instance.Reachability);
    }

    [Theory]
    [InlineData(DatabaseFailureKind.AuthenticationFailed, DiscoveryAccess.AuthenticationFailed, "authentication_failed", DiscoveryReachability.Reachable)]
    [InlineData(DatabaseFailureKind.PermissionDenied, DiscoveryAccess.Error, "permission_denied", DiscoveryReachability.Reachable)]
    [InlineData(DatabaseFailureKind.Unreachable, DiscoveryAccess.Unreachable, "unreachable", DiscoveryReachability.Unreachable)]
    [InlineData(DatabaseFailureKind.Timeout, DiscoveryAccess.Unreachable, "timeout", DiscoveryReachability.Unreachable)]
    [InlineData(DatabaseFailureKind.InvalidConfiguration, DiscoveryAccess.Error, "invalid_configuration", DiscoveryReachability.Unknown)]
    [InlineData(DatabaseFailureKind.Unexpected, DiscoveryAccess.Error, "unexpected", DiscoveryReachability.Unknown)]
    public async Task Provider_failures_map_to_fixed_access_states_and_codes(DatabaseFailureKind kind, string access, string reason, string reachability)
    {
        var rig = new Rig();
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service, DetectionResult.Scanned([Mssql("mssql|service|default", 1433)])));
        rig.Probe.Answers[(DiscoveryEngines.MsSql, 1433)] = new ListenerProbeResult(true, false, null);
        rig.Mssql.OnDiscover = (_, _, _) => throw new DatabaseProviderException(kind, "user message", new InvalidOperationException($"Login failed Password={Secret}"));

        var report = await ScanAsync(rig, new AgentDiscoveryPlan { Bindings = [Binding("mssql|service|default")] });

        var instance = Assert.Single(report.Instances);
        Assert.Equal(access, instance.Access);
        Assert.Equal(reason, instance.AccessReasonCode);
        Assert.Equal(reachability, instance.Reachability);
        Assert.Null(instance.Databases);
        Assert.Equal(DiscoveryOutcomes.Completed, report.Outcome);
    }

    [Fact]
    public async Task An_unresolvable_credential_reference_is_a_fixed_error_that_never_reaches_the_provider()
    {
        var rig = new Rig();
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service, DetectionResult.Scanned([Mssql("mssql|service|default", 1433)])));
        rig.Resolver.Resolve = _ => throw new InvalidOperationException($"The referenced database credential is unavailable. {Secret}");

        var report = await ScanAsync(rig, new AgentDiscoveryPlan { Bindings = [Binding("mssql|service|default")] });

        var instance = Assert.Single(report.Instances);
        Assert.Equal(DiscoveryAccess.Error, instance.Access);
        Assert.Equal("credential_unavailable", instance.AccessReasonCode);
        Assert.Empty(rig.Mssql.Calls);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_instance_without_an_endpoint_is_unreachable_with_the_detectors_reason()
    {
        var rig = new Rig();
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Docker, DetectionResult.Scanned([
            Postgres("postgres|docker|pg|5432", null, DiscoverySources.Docker) with { ReasonCode = "no_published_port" }])));

        var report = await ScanAsync(rig, new AgentDiscoveryPlan { Bindings = [Binding("postgres|docker|pg|5432", DiscoveryEngines.PostgreSql)] });

        var instance = Assert.Single(report.Instances);
        Assert.Equal(DiscoveryAccess.Unreachable, instance.Access);
        Assert.Equal("no_published_port", instance.AccessReasonCode);
        Assert.Empty(rig.Postgres.Calls);
    }

    [Fact]
    public async Task A_named_sql_server_instance_without_a_port_is_addressed_by_name()
    {
        var rig = new Rig();
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service, DetectionResult.Scanned([Mssql("mssql|service|SQLEXPRESS", null, name: "SQLEXPRESS")])));

        await ScanAsync(rig, new AgentDiscoveryPlan { Bindings = [Binding("mssql|service|SQLEXPRESS")] });

        var endpoint = Assert.Single(rig.Mssql.Calls).Connection.Endpoint;
        Assert.Null(endpoint.Port);
        Assert.Equal("SQLEXPRESS", endpoint.InstanceName);
    }

    [Fact]
    public async Task A_targeted_scan_enriches_only_the_requested_instance_and_leaves_the_others_unknown()
    {
        var rig = new Rig();
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service, DetectionResult.Scanned([Mssql("mssql|service|A", 1433), Mssql("mssql|service|B", 1500)])));
        var plan = new AgentDiscoveryPlan
        {
            OnlyInstanceKey = "mssql|service|B",
            Bindings = [Binding("mssql|service|A"), Binding("mssql|service|B")],
        };

        var report = await ScanAsync(rig, plan);

        Assert.Equal(DiscoveryAccess.Unknown, report.Instances.Single(instance => instance.InstanceKey == "mssql|service|A").Access);
        Assert.Equal(DiscoveryAccess.Authenticated, report.Instances.Single(instance => instance.InstanceKey == "mssql|service|B").Access);
        Assert.Equal(1500, Assert.Single(rig.Mssql.Calls).Connection.Endpoint.Port);
    }

    [Fact]
    public async Task At_most_two_instances_are_enriched_at_once()
    {
        var rig = new Rig();
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service,
            DetectionResult.Scanned([.. Enumerable.Range(0, 6).Select(i => Mssql($"mssql|service|I{i}", 2000 + i))])));
        rig.Mssql.OnDiscover = async (_, _, _) =>
        {
            await Task.Delay(30);
            return Discovery(DatabaseEngineId.MsSql);
        };
        var plan = new AgentDiscoveryPlan { Bindings = [.. Enumerable.Range(0, 6).Select(i => Binding($"mssql|service|I{i}"))] };

        var report = await ScanAsync(rig, plan);

        Assert.All(report.Instances, instance => Assert.Equal(DiscoveryAccess.Authenticated, instance.Access));
        Assert.Equal(2, rig.Mssql.PeakConcurrency);
    }

    [Fact]
    public async Task A_database_list_too_large_for_one_report_is_omitted_not_truncated()
    {
        var rig = new Rig();
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service, DetectionResult.Scanned([Mssql("mssql|service|default", 1433)])));
        rig.Mssql.OnDiscover = (_, _, _) => Task.FromResult(Discovery(DatabaseEngineId.MsSql,
            [.. Enumerable.Range(0, DiscoveryProtocol.MaximumDatabasesPerInstance + 10)
                .Select(i => Database($"db{i}", false, DatabaseAvailability.Online, DatabaseCapabilitySupport.Supported, "supported"))]));

        var report = await ScanAsync(rig, new AgentDiscoveryPlan { Bindings = [Binding("mssql|service|default")] });

        // The backend replaces its list by a reported one, so a cut list would turn the databases beyond the cut into "missing".
        var instance = Assert.Single(report.Instances);
        Assert.Null(instance.Databases);
        Assert.Equal("too_many_databases", instance.AccessReasonCode);
    }

    // ---- secrets --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Neither_the_report_nor_the_logs_ever_contain_a_secret_connection_text_or_exception_message()
    {
        var rig = new Rig();
        rig.Detectors.Add(new FakeDetector(DiscoverySources.Service, DetectionResult.Scanned([Mssql("mssql|service|A", 1433), Mssql("mssql|service|B", 1500), Mssql("mssql|service|C", 1600)])));
        var leak = $"Server=localhost;User Id=scan-login-user-7;Password={Secret}";
        rig.Mssql.OnDiscover = (connection, _, _) => connection.Endpoint.Port switch
        {
            1433 => throw new DatabaseProviderException(DatabaseFailureKind.AuthenticationFailed, leak, new InvalidOperationException(leak)),
            1500 => throw new InvalidOperationException(leak),
            _ => Task.FromResult(Discovery(DatabaseEngineId.MsSql, Database("Sales", false, DatabaseAvailability.Online, DatabaseCapabilitySupport.Supported, "supported"))),
        };
        var plan = new AgentDiscoveryPlan { Bindings = [Binding("mssql|service|A"), Binding("mssql|service|B"), Binding("mssql|service|C")] };

        var report = await ScanAsync(rig, plan);

        var text = JsonSerializer.Serialize(report, JsonSerializerOptions.Web) + "\n" + rig.Logs.AllText;
        Assert.NotEmpty(rig.Logs.Entries);
        foreach (var forbidden in new[] { Secret, "Password", "User Id", "Server=", "scan-login-user-7", "sa-ref" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
