using Microsoft.Extensions.Logging;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Discovery;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Discovery;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.AgentTests;

public sealed class DiscoveryScanRunnerTests
{
    private static readonly Guid ScanId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private sealed class FakeClient : IAgentDiscoveryClient
    {
        public List<(Guid ScanId, AgentDiscoveryReport Report)> Reports { get; } = [];

        public int Claims { get; private set; }

        public Func<CancellationToken, Task<AgentBackendResult<AgentDiscoveryPlan?>>> OnClaim { get; set; } =
            static _ => Task.FromResult(AgentBackendResult<AgentDiscoveryPlan?>.Success(new AgentDiscoveryPlan { ScanId = ScanId }));

        public Func<AgentBackendResult<bool>> OnReport { get; set; } = static () => AgentBackendResult<bool>.Success(true);

        public Task<AgentBackendResult<AgentDiscoveryPlan?>> ClaimAsync(AgentLocalIdentity identity, CancellationToken cancellationToken)
        {
            Claims++;
            return OnClaim(cancellationToken);
        }

        public Task<AgentBackendResult<bool>> ReportAsync(AgentLocalIdentity identity, Guid scanId, AgentDiscoveryReport report, CancellationToken cancellationToken)
        {
            Reports.Add((scanId, report));
            return Task.FromResult(OnReport());
        }
    }

    private sealed class Detector(Func<string> source) : IInstanceDetector
    {
        public string Source => source();

        public Task<DetectionResult> DetectAsync(DiscoveryDetectionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(DetectionResult.Scanned([new DetectedInstance
            {
                Engine = DiscoveryEngines.PostgreSql,
                Source = DiscoverySources.Service,
                Key = "postgres|service|16-main",
                DisplayName = "PostgreSQL",
                Port = 5432,
            }]));
    }

    private sealed class NoProbe : IListenerProbe
    {
        public Task<ListenerProbeResult> ProbeAsync(string engine, int port, CancellationToken cancellationToken) => Task.FromResult(new ListenerProbeResult(false, false, null));
    }

    private sealed class NoCredentials : IBackupTaskCredentialResolver
    {
        public ValueTask<DatabaseCredential> ResolveAsync(string reference, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private static DiscoveryScanRunner Create(FakeClient client, Func<string>? source = null, CaptureLoggerProvider? logs = null) => new(
        client,
        new DiscoveryScanner([new Detector(source ?? (() => DiscoverySources.Service))], new NoProbe(), [], new NoCredentials(), TimeProvider.System, NullLogger<DiscoveryScanner>.Instance),
        (logs ?? new CaptureLoggerProvider()).CreateFactory().CreateLogger<DiscoveryScanRunner>());

    [Fact]
    public async Task Nothing_due_means_no_scan_and_no_report()
    {
        var client = new FakeClient { OnClaim = static _ => Task.FromResult(AgentBackendResult<AgentDiscoveryPlan?>.NoContent()) };

        await Create(client).RunAsync(TestIdentity.Create(), default);

        Assert.Equal(1, client.Claims);
        Assert.Empty(client.Reports);
    }

    [Fact]
    public async Task A_refused_claim_is_logged_and_nothing_is_reported()
    {
        var logs = new CaptureLoggerProvider();
        var client = new FakeClient
        {
            OnClaim = static _ => Task.FromResult(AgentBackendResult<AgentDiscoveryPlan?>.Fail(new AgentBackendFailure(AgentBackendFailureKind.Transient, "network down"))),
        };

        await Create(client, logs: logs).RunAsync(TestIdentity.Create(), default);

        Assert.Empty(client.Reports);
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("could not be claimed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_claimed_scan_is_run_and_its_report_is_sent_under_the_claimed_scan_id()
    {
        var client = new FakeClient();

        await Create(client).RunAsync(TestIdentity.Create(), default);

        var (scanId, report) = Assert.Single(client.Reports);
        Assert.Equal(ScanId, scanId);
        Assert.Equal(DiscoveryOutcomes.Completed, report.Outcome);
        Assert.Contains(report.Instances, instance => instance.InstanceKey == "postgres|service|16-main");
    }

    [Fact]
    public async Task A_crashing_scan_is_reported_as_failed_with_a_fixed_code_and_never_thrown()
    {
        var logs = new CaptureLoggerProvider();
        var client = new FakeClient();
        // A detector whose identity cannot even be read crashes the scan itself, not just a source.
        var runner = Create(client, () => throw new InvalidOperationException($"secret {TestIdentity.Secret}"), logs);

        await runner.RunAsync(TestIdentity.Create(), default);

        var (scanId, report) = Assert.Single(client.Reports);
        Assert.Equal(ScanId, scanId);
        Assert.Equal(DiscoveryOutcomes.Failed, report.Outcome);
        Assert.Equal("scan_failed", report.FailureCode);
        Assert.Empty(report.Instances);
        Assert.DoesNotContain(TestIdentity.Secret, logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_report_is_logged_and_never_thrown()
    {
        var logs = new CaptureLoggerProvider();
        var client = new FakeClient
        {
            OnReport = static () => AgentBackendResult<bool>.Fail(new AgentBackendFailure(AgentBackendFailureKind.Rejected, "scan_superseded") { StatusCode = 409 }),
        };

        await Create(client, logs: logs).RunAsync(TestIdentity.Create(), default);

        Assert.Single(client.Reports);
        Assert.Contains(logs.Entries, entry => entry.Message.Contains("not accepted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_client_that_breaks_its_contract_is_contained()
    {
        var client = new FakeClient { OnClaim = static _ => throw new HttpRequestException("unexpected") };

        await Create(client).RunAsync(TestIdentity.Create(), default);

        Assert.Empty(client.Reports);
    }

    [Fact]
    public async Task Only_one_run_is_in_flight_at_a_time_and_the_next_one_is_possible_afterwards()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient
        {
            OnClaim = async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                return AgentBackendResult<AgentDiscoveryPlan?>.NoContent();
            },
        };
        var runner = Create(client);

        var first = runner.RunAsync(TestIdentity.Create(), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await runner.RunAsync(TestIdentity.Create(), default);

        Assert.Equal(1, client.Claims);

        release.SetResult();
        await first;
        await runner.RunAsync(TestIdentity.Create(), default);

        Assert.Equal(2, client.Claims);
    }

    [Fact]
    public async Task Cancellation_is_the_only_thing_that_propagates()
    {
        using var cts = new CancellationTokenSource();
        var client = new FakeClient
        {
            OnClaim = async token =>
            {
                await cts.CancelAsync();
                token.ThrowIfCancellationRequested();
                return AgentBackendResult<AgentDiscoveryPlan?>.NoContent();
            },
        };
        var runner = Create(client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(TestIdentity.Create(), cts.Token));

        // The flag was released: a later run is not locked out.
        client.OnClaim = static _ => Task.FromResult(AgentBackendResult<AgentDiscoveryPlan?>.NoContent());
        await runner.RunAsync(TestIdentity.Create(), default);
        Assert.Equal(2, client.Claims);
    }
}
