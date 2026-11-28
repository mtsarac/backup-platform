using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BackupPlatform.Agent.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.AgentTests;

public sealed class AgentMtlsTransportTests
{
    [Fact]
    public async Task ImportedClientCertificateAuthenticatesOverTls()
    {
        using var serverKey = RSA.Create(2048);
        using var clientKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var clientRequest = new CertificateRequest("CN=agent", clientKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var serverCertificate = serverRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var originalClient = clientRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pfx");
        const string password = "test-password";
        try
        {
            await File.WriteAllBytesAsync(path, originalClient.Export(X509ContentType.Pfx, password));
            using var clientCertificate = AgentMtlsTransport.LoadCertificate(path, password);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var server = AcceptAsync();
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            using var tls = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ClientCertificates = new X509CertificateCollection { clientCertificate },
                LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, timeout.Token);
            Assert.Equal(originalClient.Thumbprint, await server);

            async Task<string?> AcceptAsync()
            {
                using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
                using var stream = new SslStream(connection.GetStream(), false, (_, _, _, _) => true);
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCertificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, timeout.Token);
                using var received = stream.RemoteCertificate is null ? null : X509CertificateLoader.LoadCertificate(stream.RemoteCertificate.GetRawCertData());
                return received?.Thumbprint;
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task NextRequestUsesRenewedCertificateImmediatelyWithoutReusingPooledConnection()
    {
        using var serverKey = RSA.Create(2048);
        var serverReq = new CertificateRequest("CN=127.0.0.1", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        serverReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        serverReq.CertificateExtensions.Add(san.Build());
        using var serverCertificate = serverReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));

        using var issuerKey = RSA.Create(2048);
        var issuerReq = new CertificateRequest("CN=test-agent-ca", issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        issuerReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var issuer = issuerReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));

        using var firstClient = CreateClientCertificate(issuer, "CN=agent-1");
        using var secondClient = CreateClientCertificate(issuer, "CN=agent-2");

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var port = BackupPlatform.AgentTests.Support.TestHubServer.FreePort();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port, listen =>
            listen.UseHttps(https =>
            {
                https.ServerCertificate = serverCertificate;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (_, _, _) => true;
            })));
        await using var app = builder.Build();
        app.MapGet("/serial", (HttpContext ctx) =>
            Results.Text(ctx.Connection.ClientCertificate?.SerialNumber ?? "none"));
        await app.StartAsync();

        try
        {
            using var provider = AgentClientCertificateProvider.FromCertificate(ExportAndReload(firstClient));
            var services = new ServiceCollection()
                .AddSingleton(AgentBackendEndpoint.ForLoopbackTesting(new Uri($"https://127.0.0.1:{port}/"), serverCertificate))
                .AddSingleton(provider)
                .BuildServiceProvider();
            using var handler = AgentMtlsTransport.CreateHandler(services);
            using var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port}/") };

            var firstObserved = await client.GetStringAsync("/serial");
            Assert.Equal(firstClient.SerialNumber, firstObserved);

            provider.Update(ExportAndReload(secondClient));

            var secondObserved = await client.GetStringAsync("/serial");
            Assert.Equal(secondClient.SerialNumber, secondObserved);
        }
        finally
        {
            await app.StopAsync();
        }

        static X509Certificate2 CreateClientCertificate(X509Certificate2 issuer, string subject)
        {
            using var key = RSA.Create(2048);
            var req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], true));
            using var publicCert = req.Create(issuer, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
            return publicCert.CopyWithPrivateKey(key);
        }

        static X509Certificate2 ExportAndReload(X509Certificate2 source)
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".pfx");
            try
            {
                File.WriteAllBytes(path, source.Export(X509ContentType.Pfx, "test-password-123"));
                return AgentMtlsTransport.LoadCertificate(path, "test-password-123");
            }
            finally { File.Delete(path); }
        }
    }
}
