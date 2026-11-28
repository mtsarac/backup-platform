using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Transport;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Agents;

namespace BackupPlatform.AgentTests;

[Collection("Agent mTLS environment")]
public sealed class SignalRAgentPushChannelTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private static string ExpectedAuthorization => $"BackupAgent {TestIdentity.CredentialId:N}.{TestIdentity.Secret}";

    private static SignalRAgentPushChannel CreateChannel(Uri baseUri, CaptureLoggerProvider? logs = null, double random = 0.999, TimeSpan? helloTimeout = null) =>
        new(AgentBackendEndpoint.ForLoopbackTesting(baseUri),
            new AgentBackoff(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200), () => random),
            TimeProvider.System,
            (logs ?? new CaptureLoggerProvider()).CreateFactory(),
            helloTimeout ?? TimeSpan.FromSeconds(5));

    [Fact]
    public async Task Connects_over_websockets_with_credential_and_protocol_headers_and_no_secret_in_the_url()
    {
        await using var server = await TestHubServer.StartAsync();
        server.State.RequiredAuthorization = ExpectedAuthorization;
        await using var channel = CreateChannel(server.BaseUri);
        var listener = new RecordingListener();

        var result = await channel.ConnectAsync(TestIdentity.Create(), listener, CancellationToken.None);

        Assert.True(result.IsConnected, result.Failure?.Description);
        var received = server.State.Received.ToArray();
        Assert.NotEmpty(received);
        Assert.All(received, r =>
        {
            Assert.Equal(ExpectedAuthorization, r.Authorization);
            Assert.Equal(AgentProtocol.Current.Version.ToString(), r.Protocol);
            Assert.DoesNotContain("access_token", r.Query, StringComparison.Ordinal);
            Assert.DoesNotContain(TestIdentity.Secret, r.Query + r.Path, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Receives_poll_hints_and_protocol_notices_from_the_server()
    {
        await using var server = await TestHubServer.StartAsync();
        await using var channel = CreateChannel(server.BaseUri);
        var listener = new RecordingListener();
        Assert.True((await channel.ConnectAsync(TestIdentity.Create(), listener, CancellationToken.None)).IsConnected);

        await server.SendPollHintAsync("work-available");
        await server.SendAsync(AgentPushChannelContract.ProtocolNoticeMethod, new AgentProtocolNotice
        {
            Kind = AgentPushChannelContract.NoticeUpgradeRequired,
            Action = AgentErrorActions.UpgradeAgent,
        });
        await listener.NoticeSignal.Task.WaitAsync(Patience);

        Assert.Equal(1, listener.Hints);
        Assert.Contains("hint:work-available", listener.Events);
        Assert.Equal(AgentErrorActions.UpgradeAgent, listener.Notice!.Action);
    }

    [Fact]
    public async Task Hello_for_a_different_agent_is_a_failure_and_leaves_nothing_connected()
    {
        await using var server = await TestHubServer.StartAsync();
        server.State.HelloAgentId = Guid.NewGuid();
        await using var channel = CreateChannel(server.BaseUri);
        var listener = new RecordingListener();

        var result = await channel.ConnectAsync(TestIdentity.Create(), listener, CancellationToken.None);

        Assert.False(result.IsConnected);
        Assert.Equal(AgentBackendFailureKind.Rejected, result.Failure!.Kind);
        Assert.DoesNotContain("closed", listener.Events);
    }

    [Fact]
    public async Task Hello_for_a_different_agent_after_a_reconnect_closes_the_channel()
    {
        await using var server = await TestHubServer.StartAsync();
        await using var channel = CreateChannel(server.BaseUri);
        var listener = new RecordingListener();
        Assert.True((await channel.ConnectAsync(TestIdentity.Create(), listener, CancellationToken.None)).IsConnected);

        await server.StopAsync();
        await listener.ReconnectingSignal.Task.WaitAsync(Patience);
        server.State.HelloAgentId = Guid.NewGuid();
        await server.RestartAsync();
        await listener.ClosedSignal.Task.WaitAsync(Patience);

        Assert.Equal(AgentBackendFailureKind.Rejected, listener.ClosedFailure!.Kind);
        Assert.Equal(["reconnecting", "closed"], listener.Events.Take(2));
    }

    [Fact]
    public async Task Clean_server_side_close_reports_the_channel_closed_without_a_failure()
    {
        await using var server = await TestHubServer.StartAsync();
        await using var channel = CreateChannel(server.BaseUri);
        var listener = new RecordingListener();
        Assert.True((await channel.ConnectAsync(TestIdentity.Create(), listener, CancellationToken.None)).IsConnected);

        server.AbortAll();
        await listener.ClosedSignal.Task.WaitAsync(Patience);

        Assert.Null(listener.ClosedFailure);
    }

    [Theory]
    [InlineData(401, AgentBackendFailureKind.Unauthorized)]
    [InlineData(400, AgentBackendFailureKind.ProtocolIncompatible)]
    [InlineData(429, AgentBackendFailureKind.RateLimited)]
    [InlineData(503, AgentBackendFailureKind.Transient)]
    [InlineData(404, AgentBackendFailureKind.Rejected)]
    public async Task Connect_rejections_map_to_failure_kinds(int status, AgentBackendFailureKind expected)
    {
        await using var server = await TestHubServer.StartAsync();
        server.State.RejectStatus = status;
        await using var channel = CreateChannel(server.BaseUri);

        var result = await channel.ConnectAsync(TestIdentity.Create(), new RecordingListener(), CancellationToken.None);

        Assert.False(result.IsConnected);
        Assert.Equal(expected, result.Failure!.Kind);
    }

    [Fact]
    public async Task Connect_to_a_closed_port_is_a_transient_failure_not_an_exception()
    {
        await using var channel = CreateChannel(new Uri($"http://127.0.0.1:{TestHubServer.FreePort()}/"));

        var result = await channel.ConnectAsync(TestIdentity.Create(), new RecordingListener(), CancellationToken.None);

        Assert.Equal(AgentBackendFailureKind.Transient, result.Failure!.Kind);
    }

    [Fact]
    public async Task Server_stop_is_detected_and_the_channel_reconnects_when_the_server_returns_on_the_same_port()
    {
        await using var server = await TestHubServer.StartAsync();
        await using var channel = CreateChannel(server.BaseUri);
        var listener = new RecordingListener();
        Assert.True((await channel.ConnectAsync(TestIdentity.Create(), listener, CancellationToken.None)).IsConnected);

        await server.StopAsync();
        await listener.ReconnectingSignal.Task.WaitAsync(Patience);
        Assert.Equal(0, listener.Reconnected);

        await server.RestartAsync();
        await listener.ReconnectedSignal.Task.WaitAsync(Patience);
        await server.SendPollHintAsync();
        await Wait.UntilAsync(() => listener.Hints == 1, "poll hint after reconnect");

        Assert.Equal(["reconnecting", "reconnected", "hint:work"], listener.Events);
    }

    [Fact]
    public async Task Reconnect_attempts_are_unbounded_in_number_but_bounded_in_rate()
    {
        await using var server = await TestHubServer.StartAsync();
        await using var channel = CreateChannel(server.BaseUri); // delays: ~50, 100, 200 ms, then capped at 200 ms
        var listener = new RecordingListener();
        Assert.True((await channel.ConnectAsync(TestIdentity.Create(), listener, CancellationToken.None)).IsConnected);
        var port = server.Port;
        await server.StopAsync();
        await listener.ReconnectingSignal.Task.WaitAsync(Patience);

        // A plain TCP listener on the same port counts and immediately drops every reconnect attempt.
        var attempts = new List<DateTimeOffset>();
        using var tcp = new TcpListener(IPAddress.Loopback, port);
        tcp.Start();
        using var stop = new CancellationTokenSource();
        var accepting = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await tcp.AcceptTcpClientAsync(stop.Token);
                    lock (attempts)
                    {
                        attempts.Add(DateTimeOffset.UtcNow);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await Task.Delay(TimeSpan.FromSeconds(2.5));
        await stop.CancelAsync();
        await accepting;

        DateTimeOffset[] connections;
        lock (attempts)
        {
            connections = [.. attempts];
        }
        // The HTTP stack opens a few sockets per negotiate attempt within milliseconds; those count as one attempt.
        var seen = new List<DateTimeOffset>();
        foreach (var connection in connections)
        {
            if (seen.Count == 0 || (connection - seen[^1]).TotalMilliseconds > 60)
            {
                seen.Add(connection);
            }
        }
        var gaps = seen.Zip(seen.Skip(1), (a, b) => (b - a).TotalMilliseconds).ToArray();
        var description = string.Join(",", gaps.Select(g => (int)g));
        Assert.InRange(seen.Count, 6, 25); // keeps trying, but at most every ~200 ms
        Assert.True(gaps.Max() < 1_000, $"largest gap exceeds the capped delay: {description}");
        Assert.True(gaps.Skip(3).Min() > 100, $"late attempts must be spaced by the capped delay, not hammered: {description}");
        Assert.DoesNotContain("closed", listener.Events);
    }

    [Fact]
    public async Task Credential_rejection_while_reconnecting_ends_the_channel_so_the_core_can_reload_the_identity()
    {
        await using var server = await TestHubServer.StartAsync();
        await using var channel = CreateChannel(server.BaseUri);
        var listener = new RecordingListener();
        Assert.True((await channel.ConnectAsync(TestIdentity.Create(), listener, CancellationToken.None)).IsConnected);

        await server.StopAsync();
        await listener.ReconnectingSignal.Task.WaitAsync(Patience);
        server.State.RejectStatus = 401;
        await server.RestartAsync();
        await listener.ClosedSignal.Task.WaitAsync(Patience);

        Assert.Equal(AgentBackendFailureKind.Unauthorized, listener.ClosedFailure!.Kind);
    }

    [Fact]
    public async Task Stopping_releases_the_connection_without_further_callbacks()
    {
        await using var server = await TestHubServer.StartAsync();
        await using var channel = CreateChannel(server.BaseUri);
        var listener = new RecordingListener();
        Assert.True((await channel.ConnectAsync(TestIdentity.Create(), listener, CancellationToken.None)).IsConnected);

        await channel.StopAsync(CancellationToken.None);
        await Wait.UntilAsync(() => server.State.Connections.IsEmpty, "server sees the connection gone");
        await server.SendPollHintAsync();

        Assert.Empty(listener.Events);
    }

    [Fact]
    public async Task Connecting_again_replaces_the_previous_connection()
    {
        await using var server = await TestHubServer.StartAsync();
        await using var channel = CreateChannel(server.BaseUri);
        var first = new RecordingListener();
        var second = new RecordingListener();
        Assert.True((await channel.ConnectAsync(TestIdentity.Create(), first, CancellationToken.None)).IsConnected);
        Assert.True((await channel.ConnectAsync(TestIdentity.Create(), second, CancellationToken.None)).IsConnected);

        await server.SendPollHintAsync();
        await Wait.UntilAsync(() => second.Hints == 1, "hint on the new connection");

        Assert.Equal(0, first.Hints);
        Assert.Single(server.State.Connections);
    }

    [Fact]
    public async Task Cancelling_a_connect_throws_and_leaves_nothing_connected()
    {
        await using var server = await TestHubServer.StartAsync();
        await using var channel = CreateChannel(server.BaseUri);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.ConnectAsync(TestIdentity.Create(), new RecordingListener(), cts.Token));

        Assert.Empty(server.State.Connections);
    }

    [Fact]
    public async Task Server_that_never_says_hello_fails_the_connect_after_the_hello_timeout()
    {
        // A TCP server that completes neither negotiate nor hello: the connect must give up, not hang.
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                var client = await tcp.AcceptTcpClientAsync();
                _ = client; // accept and stay silent
            }
        });
        await using var channel = CreateChannel(new Uri($"http://127.0.0.1:{port}/"), helloTimeout: TimeSpan.FromMilliseconds(300));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var outcome = await Record.ExceptionAsync(async () =>
        {
            var result = await channel.ConnectAsync(TestIdentity.Create(), new RecordingListener(), cts.Token);
            Assert.False(result.IsConnected);
        });

        Assert.True(outcome is null or OperationCanceledException, outcome?.ToString());
    }

    [Fact]
    public async Task Renewing_the_client_certificate_reconnects_the_push_channel_with_the_new_certificate()
    {
        using var serverKey = System.Security.Cryptography.RSA.Create(2048);
        var serverReq = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=127.0.0.1", serverKey, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        serverReq.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(false, false, 0, true));
        serverReq.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
            System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature | System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.KeyEncipherment, true));
        serverReq.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
            [new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")], true));
        var san = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        serverReq.CertificateExtensions.Add(san.Build());
        using var serverCert = serverReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));

        using var issuerKey = System.Security.Cryptography.RSA.Create(2048);
        var issuerReq = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=push-ca", issuerKey, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        issuerReq.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(true, false, 0, true));
        using var issuer = issuerReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));

        using var firstClient = IssueClient(issuer, "CN=agent-push-1");
        using var secondClient = IssueClient(issuer, "CN=agent-push-2");
        using var provider = AgentClientCertificateProvider.FromCertificate(ReloadPfx(firstClient));

        await using var server = await TestHubServer.StartAsync(serverCert);
        await using var channel = new SignalRAgentPushChannel(
            AgentBackendEndpoint.ForLoopbackTesting(server.BaseUri, serverCert),
            new AgentBackoff(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200), () => 0.5),
            TimeProvider.System,
            new CaptureLoggerProvider().CreateFactory(),
            TimeSpan.FromSeconds(5),
            provider);
        var listener = new RecordingListener();
        Assert.True((await channel.ConnectAsync(TestIdentity.Create(), listener, CancellationToken.None)).IsConnected);
        Assert.Contains(server.State.Received, r => r.ClientCertificateSerial == firstClient.SerialNumber);

        provider.Update(ReloadPfx(secondClient));

        await listener.ReconnectedSignal.Task.WaitAsync(Patience);
        await server.SendPollHintAsync("after-renewal");
        await Wait.UntilAsync(() => listener.Hints == 1, "poll hint on renewed connection");

        Assert.Contains(server.State.Received, r => r.ClientCertificateSerial == secondClient.SerialNumber);
        Assert.Equal(["reconnecting", "reconnected", "hint:after-renewal"], listener.Events);

        static System.Security.Cryptography.X509Certificates.X509Certificate2 IssueClient(
            System.Security.Cryptography.X509Certificates.X509Certificate2 ca, string subject)
        {
            using var key = System.Security.Cryptography.RSA.Create(2048);
            var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                subject, key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(false, false, 0, true));
            req.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
                System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature, true));
            req.CertificateExtensions.Add(new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(
                [new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.2")], true));
            using var pub = req.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1),
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            return pub.CopyWithPrivateKey(key);
        }

        static System.Security.Cryptography.X509Certificates.X509Certificate2 ReloadPfx(
            System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pfx");
            try
            {
                File.WriteAllBytes(path, cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "test-password-123"));
                return AgentMtlsTransport.LoadCertificate(path, "test-password-123");
            }
            finally { File.Delete(path); }
        }
    }
}
