using System.Net;
using System.Net.Http.Headers;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Transport;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.AgentTests;

/// <summary>
/// Runs the real service, HTTP client (through IHttpClientFactory), and SignalR channel through failure scenarios and
/// asserts nothing the agent logs, including exception text, contains the credential secret or the Authorization header.
/// </summary>
public sealed class SecretLeakageTests
{
    private static readonly string[] Forbidden = [TestIdentity.Secret, "BackupAgent ", "Authorization"];

    private static void AssertNoSecrets(CaptureLoggerProvider logs, params string[] forbidden)
    {
        var text = logs.AllText;
        Assert.NotEmpty(logs.Entries);
        foreach (var token in forbidden)
        {
            Assert.DoesNotContain(token, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly CancellationTokenSource _cts = new();
        private Task? _run;

        public Rig(Uri pushBaseUri, FakeHttpHandler handler, LogLevel level)
        {
            Time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            Logs = new CaptureLoggerProvider();
            Handler = handler;
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(level).AddProvider(Logs));
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton(AgentBackendEndpoint.ForLoopbackTesting(pushBaseUri));
            services.AddSingleton(new AgentBackoff(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(1), () => 0.5));
            services.AddAgentTransport();
            services.AddHttpClient(nameof(IAgentBackendClient)).ConfigurePrimaryHttpMessageHandler(() => handler);
            services.AddSingleton(new AgentConnectivityOptions());
            _provider = services.BuildServiceProvider();
        }

        public FakeTimeProvider Time { get; }

        public CaptureLoggerProvider Logs { get; }

        public FakeHttpHandler Handler { get; }

        public AgentConnectivityService Service { get; private set; } = null!;

        public void Start()
        {
            var options = _provider.GetRequiredService<AgentConnectivityOptions>();
            Service = new AgentConnectivityService(
                new FakeCredentialStore(TestIdentity.Create()),
                _provider.GetRequiredService<IAgentBackendClient>(),
                _provider.GetRequiredService<IAgentPushChannel>(),
                new AgentSoftwareInfo("1.2.3", "linux", "Test OS", "10.0.0"),
                new NullCapabilityRevisionSource(), options,
                new AgentBackoff(options.ReconnectBaseDelay, options.ReconnectMaxDelay, () => 0.5),
                Time, _provider.GetRequiredService<ILogger<AgentConnectivityService>>());
            _run = Service.RunAsync(_cts.Token);
        }

        public async Task RunForAsync(TimeSpan duration)
        {
            for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += TimeSpan.FromSeconds(1))
            {
                Time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(25);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            if (_run is not null)
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await _provider.DisposeAsync();
        }
    }

    private static Uri ClosedLoopback() => new($"http://127.0.0.1:{TestHubServer.FreePort()}/");

    private static FakeHttpHandler HandlerReturning(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new() { Respond = (request, _) => Task.FromResult(respond(request)) };

    [Fact]
    public async Task Rejected_credential_over_http_and_refused_push_connection_leaks_nothing()
    {
        var handler = HandlerReturning(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Headers = { WwwAuthenticate = { new AuthenticationHeaderValue("BackupAgent") } },
        });
        await using var rig = new Rig(ClosedLoopback(), handler, LogLevel.Debug);

        rig.Start();
        await rig.RunForAsync(TimeSpan.FromSeconds(130));

        Assert.NotEmpty(handler.Requests);
        Assert.Contains("Unauthorized", rig.Logs.AllText, StringComparison.Ordinal);
        AssertNoSecrets(rig.Logs, Forbidden);
    }

    [Fact]
    public async Task Rate_limiting_leaks_nothing()
    {
        var handler = HandlerReturning(_ =>
        {
            var response = FakeHttpHandler.Json(HttpStatusCode.TooManyRequests, """{"error":"rate_limited","retryAfterSeconds":20}""");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
            return response;
        });
        await using var rig = new Rig(ClosedLoopback(), handler, LogLevel.Debug);

        rig.Start();
        await rig.RunForAsync(TimeSpan.FromSeconds(100));

        Assert.Contains("RateLimited", rig.Logs.AllText, StringComparison.Ordinal);
        AssertNoSecrets(rig.Logs, Forbidden);
    }

    [Fact]
    public async Task Protocol_mismatch_leaks_nothing()
    {
        var handler = HandlerReturning(_ => FakeHttpHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"protocol_incompatible","action":"upgrade_agent","supportedProtocol":{"current":"2.0","minimumSupported":"2.0","maximumSupported":"2.0"}}"""));
        await using var rig = new Rig(ClosedLoopback(), handler, LogLevel.Debug);

        rig.Start();
        await rig.RunForAsync(TimeSpan.FromSeconds(100));

        Assert.Contains("ProtocolIncompatible", rig.Logs.AllText, StringComparison.Ordinal);
        Assert.Contains("upgrade_agent", rig.Logs.AllText, StringComparison.Ordinal);
        AssertNoSecrets(rig.Logs, Forbidden);
    }

    [Fact]
    public async Task Network_errors_leak_nothing_even_in_exception_text()
    {
        var handler = new FakeHttpHandler
        {
            Respond = (_, _) => throw new HttpRequestException("Connection refused (backup.example.test:443)", new System.Net.Sockets.SocketException(111)),
        };
        await using var rig = new Rig(ClosedLoopback(), handler, LogLevel.Debug);

        rig.Start();
        await rig.RunForAsync(TimeSpan.FromSeconds(100));

        Assert.Contains("Connection refused", rig.Logs.AllText, StringComparison.Ordinal);
        AssertNoSecrets(rig.Logs, Forbidden);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(400)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Signalr_connection_failures_against_a_real_hub_leak_nothing(int hubStatus)
    {
        await using var server = await TestHubServer.StartAsync();
        server.State.RejectStatus = hubStatus;
        var handler = HandlerReturning(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await using var rig = new Rig(server.BaseUri, handler, LogLevel.Debug);

        rig.Start();
        await rig.RunForAsync(TimeSpan.FromSeconds(70));

        Assert.NotEmpty(server.State.Received);
        Assert.Contains("Push channel attempt failed", rig.Logs.AllText, StringComparison.Ordinal);
        AssertNoSecrets(rig.Logs, Forbidden);
    }

    [Fact]
    public async Task Even_trace_level_logging_of_every_component_never_contains_the_secret()
    {
        await using var server = await TestHubServer.StartAsync();
        var handler = HandlerReturning(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        await using var rig = new Rig(server.BaseUri, handler, LogLevel.Trace);

        rig.Start();
        await rig.RunForAsync(TimeSpan.FromSeconds(5));
        await server.StopAsync();
        await rig.RunForAsync(TimeSpan.FromSeconds(5));

        Assert.NotEmpty(rig.Logs.Entries);
        AssertNoSecrets(rig.Logs, TestIdentity.Secret);
    }
}
