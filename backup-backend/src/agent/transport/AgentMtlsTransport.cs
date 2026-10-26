using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography.X509Certificates;

namespace BackupPlatform.Agent.Transport;

/// <summary>Loads a locally protected agent PFX for backend TLS. Default server certificate validation is never weakened.</summary>
public static class AgentMtlsTransport
{
    public static HttpMessageHandler CreateHandler(IServiceProvider services)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), AllowAutoRedirect = false };
        var certificate = LoadCertificate();
        if (certificate is not null)
        {
            var backendHost = services.GetRequiredService<AgentBackendEndpoint>().BaseUri.IdnHost;
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate };
            handler.SslOptions.LocalCertificateSelectionCallback = (_, targetHost, _, _, _) =>
                string.Equals(targetHost, backendHost, StringComparison.OrdinalIgnoreCase) ? certificate : null!;
            return new CertificateOwningHandler(handler, certificate);
        }
        return handler;
    }

    internal static X509Certificate2? LoadCertificate()
    {
        var path = Environment.GetEnvironmentVariable("AGENT_MTLS_CERTIFICATE_PATH");
        if (string.IsNullOrEmpty(path)) return null;
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("The agent mTLS certificate path must be absolute.");
        return X509CertificateLoader.LoadPkcs12FromFile(path, Environment.GetEnvironmentVariable("AGENT_MTLS_CERTIFICATE_PASSWORD"), X509KeyStorageFlags.EphemeralKeySet);
    }

    private sealed class CertificateOwningHandler(HttpMessageHandler inner, X509Certificate2 certificate) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            finally { if (disposing) certificate.Dispose(); }
        }
    }
}
