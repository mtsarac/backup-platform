using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BackupPlatform.Agent.Transport;

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
}
