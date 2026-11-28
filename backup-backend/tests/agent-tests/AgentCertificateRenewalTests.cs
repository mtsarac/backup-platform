using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Host;
using BackupPlatform.Agent.Transport;
using BackupPlatform.AgentTests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.AgentTests;

[Collection("Agent mTLS environment")]
public sealed class AgentCertificateRenewalTests : IDisposable
{
    private const string PathVariable = "AGENT_MTLS_CERTIFICATE_PATH";
    private const string PasswordFileVariable = "AGENT_MTLS_CERTIFICATE_PASSWORD_FILE";
    private const string PasswordVariable = "AGENT_MTLS_CERTIFICATE_PASSWORD";
    private static readonly DateTimeOffset StartUtc = new(2026, 11, 28, 0, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Directory.CreateTempSubdirectory("mtls-renewal").FullName;

    public AgentCertificateRenewalTests() => ResetEnvironment();

    public void Dispose()
    {
        ResetEnvironment();
        Directory.Delete(_directory, recursive: true);
    }

    private static void ResetEnvironment()
    {
        Environment.SetEnvironmentVariable(PathVariable, null);
        Environment.SetEnvironmentVariable(PasswordFileVariable, null);
        Environment.SetEnvironmentVariable(PasswordVariable, null);
    }

    [Fact]
    public async Task RenewsAtTwoThirdsOfLifetimePersistsPfxAndNextRequestUsesNewCertificate()
    {
        var clock = new FakeTimeProvider(StartUtc);
        var scheduled = new TimerTrackingTimeProvider(clock);
        using var harness = await RenewalTestHarness.StartAsync(_directory, scheduled);

        var initialSerial = harness.ActiveSerial;
        Assert.Equal(initialSerial, await harness.AppClient.GetStringAsync("/api/agent/ping"));

        await harness.StartServiceAsync();

        clock.Advance(TimeSpan.FromDays(19) + TimeSpan.FromHours(23));
        await SettleAsync();
        Assert.Equal(0, harness.EnrollCalls);
        Assert.Equal(initialSerial, await harness.AppClient.GetStringAsync("/api/agent/ping"));

        var renewed = harness.WaitForNextUpdateAsync();
        clock.Advance(TimeSpan.FromHours(1));
        await renewed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, harness.EnrollCalls);
        var secondSerial = harness.ActiveSerial;
        Assert.NotEqual(initialSerial, secondSerial);

        using (var onDisk = AgentMtlsTransport.LoadCertificate(harness.CertificatePath, harness.Password))
        {
            Assert.Equal(secondSerial, onDisk.SerialNumber);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(harness.CertificatePath));
            }
        }

        Assert.Equal(secondSerial, await harness.AppClient.GetStringAsync("/api/agent/ping"));

        await scheduled.WaitForTimerAsync();
        var thirdRenewed = harness.WaitForNextUpdateAsync();
        clock.Advance(TimeSpan.FromDays(20));
        await thirdRenewed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, harness.EnrollCalls);
        var thirdSerial = harness.ActiveSerial;
        Assert.NotEqual(secondSerial, thirdSerial);
        Assert.Equal(thirdSerial, await harness.AppClient.GetStringAsync("/api/agent/ping"));
    }

    [Fact]
    public async Task RetriesWithBackoffWhenRenewalFailsAndKeepsCurrentCertificateWorking()
    {
        var clock = new FakeTimeProvider(StartUtc);
        var scheduled = new TimerTrackingTimeProvider(clock);
        using var harness = await RenewalTestHarness.StartAsync(_directory, scheduled, failuresBeforeSuccess: 2);

        var initialSerial = harness.ActiveSerial;
        await harness.StartServiceAsync();

        // Trigger at 2/3 of 30 days = 20 days; attempt 1 fails with 503 and schedules 5s backoff (base 10s * 0.5).
        clock.Advance(TimeSpan.FromDays(20));
        await scheduled.WaitForTimerAsync();
        Assert.Equal(1, harness.EnrollCalls);
        Assert.Equal(initialSerial, await harness.AppClient.GetStringAsync("/api/agent/ping"));

        clock.Advance(TimeSpan.FromSeconds(4));
        await SettleAsync();
        Assert.Equal(1, harness.EnrollCalls);

        // At +5s total, attempt 2 runs, fails with 503, and schedules 10s backoff (20s * 0.5).
        clock.Advance(TimeSpan.FromSeconds(1));
        await scheduled.WaitForTimerAsync();
        Assert.Equal(2, harness.EnrollCalls);

        clock.Advance(TimeSpan.FromSeconds(9));
        await SettleAsync();
        Assert.Equal(2, harness.EnrollCalls);

        // At +10s, attempt 3 runs and succeeds.
        var renewed = harness.WaitForNextUpdateAsync();
        clock.Advance(TimeSpan.FromSeconds(1));
        await renewed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, harness.EnrollCalls);
        var renewedSerial = harness.ActiveSerial;
        Assert.NotEqual(initialSerial, renewedSerial);
        Assert.Equal(renewedSerial, await harness.AppClient.GetStringAsync("/api/agent/ping"));
    }

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 10; i++) await Task.Yield();
        await Task.Delay(10);
    }

    private sealed class TimerTrackingTimeProvider(FakeTimeProvider inner) : TimeProvider
    {
        private readonly global::System.Threading.Channels.Channel<bool> _timers = global::System.Threading.Channels.Channel.CreateUnbounded<bool>();
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override long GetTimestamp() => inner.GetTimestamp();
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            _timers.Writer.TryWrite(true);
            return timer;
        }

        public async Task WaitForTimerAsync()
        {
            await _timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            while (_timers.Reader.TryRead(out _)) { }
        }

        public void Drain()
        {
            while (_timers.Reader.TryRead(out _)) { }
        }
    }

    private sealed class RenewalTestHarness : IDisposable
    {
        private readonly WebApplication _app;
        private readonly RSA _serverKey;
        private readonly X509Certificate2 _serverCert;
        private readonly RSA _issuerKey;
        private readonly X509Certificate2 _issuer;
        private readonly TimerTrackingTimeProvider _time;
        private readonly ServiceProvider _services;
        private readonly HttpMessageHandler _appHandler;
        private readonly HttpMessageHandler _renewHandler;
        private readonly HttpClient _renewClient;
        private readonly AgentCertificateRenewalService _service;
        private string _expectedEnrollSerial;
        private int _remainingFailures;
        private int _enrollCalls;

        private RenewalTestHarness(
            WebApplication app,
            RSA serverKey,
            X509Certificate2 serverCert,
            RSA issuerKey,
            X509Certificate2 issuer,
            TimerTrackingTimeProvider time,
            string certificatePath,
            string password,
            AgentClientCertificateProvider provider,
            ServiceProvider services,
            HttpMessageHandler appHandler,
            HttpClient appClient,
            HttpMessageHandler renewHandler,
            HttpClient renewClient,
            AgentCertificateRenewalService service,
            string initialSerial,
            int failuresBeforeSuccess)
        {
            _app = app;
            _serverKey = serverKey;
            _serverCert = serverCert;
            _issuerKey = issuerKey;
            _issuer = issuer;
            _time = time;
            CertificatePath = certificatePath;
            Password = password;
            Provider = provider;
            _services = services;
            _appHandler = appHandler;
            AppClient = appClient;
            _renewHandler = renewHandler;
            _renewClient = renewClient;
            _service = service;
            _expectedEnrollSerial = initialSerial;
            _remainingFailures = failuresBeforeSuccess;
        }

        public string CertificatePath { get; }
        public string Password { get; }
        public AgentClientCertificateProvider Provider { get; }
        public HttpClient AppClient { get; }
        public int EnrollCalls => Volatile.Read(ref _enrollCalls);
        public string ActiveSerial => Provider.CurrentValidityWindow!.Value.SerialNumber;

        public static async Task<RenewalTestHarness> StartAsync(string directory, TimerTrackingTimeProvider time, int failuresBeforeSuccess = 0)
        {
            var serverKey = RSA.Create(2048);
            var serverReq = new CertificateRequest("CN=127.0.0.1", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            serverReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            serverReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            serverReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true));
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback);
            serverReq.CertificateExtensions.Add(san.Build());
            var serverCert = serverReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));

            var issuerKey = RSA.Create(2048);
            var issuerReq = new CertificateRequest("CN=renewal-ca", issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            issuerReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            var issuer = issuerReq.CreateSelfSigned(StartUtc.AddDays(-1), StartUtc.AddDays(365));

            var password = "renewal-test-password-12345";
            var passwordFile = Path.Combine(directory, "mtls.pass");
            await File.WriteAllTextAsync(passwordFile, password);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(passwordFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            var certPath = Path.Combine(directory, "client-certificate.pfx");
            using (var initialKey = RSA.Create(2048))
            {
                var initialCsr = new CertificateRequest("CN=backup-agent", initialKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using var initialPub = Issue(issuer, initialCsr.PublicKey, StartUtc, StartUtc.AddDays(30));
                using var initialFull = initialPub.CopyWithPrivateKey(initialKey);
                await File.WriteAllBytesAsync(certPath, initialFull.Export(X509ContentType.Pfx, password));
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(certPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            Environment.SetEnvironmentVariable(PathVariable, certPath);
            Environment.SetEnvironmentVariable(PasswordFileVariable, passwordFile);

            var port = TestHubServer.FreePort();
            var baseUri = new Uri($"https://127.0.0.1:{port}/");
            var provider = AgentClientCertificateProvider.FromEnvironment();
            var initialSerial = provider.CurrentValidityWindow!.Value.SerialNumber;

            RenewalTestHarness? instance = null;
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port, listen =>
                listen.UseHttps(https =>
                {
                    https.ServerCertificate = serverCert;
                    https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                    https.ClientCertificateValidation = (_, _, _) => true;
                })));
            var app = builder.Build();
            app.MapGet("/api/agent/ping", (HttpContext ctx) =>
                Results.Text(ctx.Connection.ClientCertificate?.SerialNumber ?? "none"));
            app.MapPost("/api/agent/certificate/enroll", async (HttpContext ctx) =>
            {
                Interlocked.Increment(ref instance!._enrollCalls);
                var presented = ctx.Connection.ClientCertificate;
                if (presented is null || !string.Equals(presented.SerialNumber, instance._expectedEnrollSerial, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.StatusCode(StatusCodes.Status401Unauthorized);
                }
                if (instance._remainingFailures > 0)
                {
                    instance._remainingFailures--;
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                var csrPem = doc.RootElement.GetProperty("signingRequestPem").GetString()!;
                var csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
                var now = time.GetUtcNow();
                using var issued = Issue(issuer, csr.PublicKey, now, now.AddDays(30));
                instance._expectedEnrollSerial = issued.SerialNumber;
                return Results.Ok(new { certificatePem = issued.ExportCertificatePem() });
            });
            await app.StartAsync();

            var endpoint = AgentBackendEndpoint.ForLoopbackTesting(baseUri, serverCert);
            var services = new ServiceCollection()
                .AddSingleton(endpoint)
                .AddSingleton(provider)
                .BuildServiceProvider();
            var appHandler = AgentMtlsTransport.CreateHandler(services);
            var appClient = new HttpClient(appHandler) { BaseAddress = baseUri };
            var renewHandler = AgentMtlsTransport.CreateHandler(services);
            var renewClient = new HttpClient(renewHandler) { BaseAddress = baseUri };
            var backoff = new AgentBackoff(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), () => 0.5);
            var service = new AgentCertificateRenewalService(provider, endpoint, renewClient, backoff, time, NullLogger<AgentCertificateRenewalService>.Instance, rsaKeySizeBits: 2048);

            instance = new RenewalTestHarness(app, serverKey, serverCert, issuerKey, issuer, time, certPath, password, provider, services, appHandler, appClient, renewHandler, renewClient, service, initialSerial, failuresBeforeSuccess);
            return instance;
        }

        public async Task StartServiceAsync()
        {
            _time.Drain();
            await _service.StartAsync(CancellationToken.None);
            await _time.WaitForTimerAsync();
        }

        public Task WaitForNextUpdateAsync()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Action? handler = null;
            handler = () =>
            {
                Provider.Changed -= handler;
                tcs.TrySetResult();
            };
            Provider.Changed += handler;
            return tcs.Task;
        }

        public void Dispose()
        {
            _service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            _service.Dispose();
            _renewClient.Dispose();
            _renewHandler.Dispose();
            AppClient.Dispose();
            _appHandler.Dispose();
            _services.Dispose();
            Provider.Dispose();
            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _issuer.Dispose();
            _issuerKey.Dispose();
            _serverCert.Dispose();
            _serverKey.Dispose();
        }

        private static X509Certificate2 Issue(X509Certificate2 issuer, PublicKey publicKey, DateTimeOffset notBefore, DateTimeOffset notAfter)
        {
            var req = new CertificateRequest(new X500DistinguishedName("CN=backup-agent"), publicKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], true));
            return req.Create(issuer, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
        }
    }
}
