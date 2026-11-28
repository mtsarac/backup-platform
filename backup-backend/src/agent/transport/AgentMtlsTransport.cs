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

    /// <summary>Reads the file named by <c>AGENT_MTLS_CERTIFICATE_PASSWORD_FILE</c>. The Windows host replaces it with a DPAPI reader; Linux reads a root-owned file (for example a systemd credential).</summary>
    public static Func<string, string> ReadPasswordFile { get; set; } = path => File.ReadAllText(path).Trim();

    /// <summary>The PFX password: the password file when configured, else the process environment. Null when neither is set.</summary>
    public static string? ResolvePassword()
    {
        var file = Environment.GetEnvironmentVariable("AGENT_MTLS_CERTIFICATE_PASSWORD_FILE");
        if (!string.IsNullOrEmpty(file))
        {
            if (!Path.IsPathFullyQualified(file)) throw new InvalidOperationException("The agent mTLS password file path must be absolute.");
            return ReadPasswordFile(file);
        }
        return Environment.GetEnvironmentVariable("AGENT_MTLS_CERTIFICATE_PASSWORD");
    }

    public static X509Certificate2? LoadCertificate()
    {
        var path = Environment.GetEnvironmentVariable("AGENT_MTLS_CERTIFICATE_PATH");
        if (string.IsNullOrEmpty(path)) return null;
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("The agent mTLS certificate path must be absolute.");
        return LoadCertificate(path, ResolvePassword());
    }

    internal static X509Certificate2 LoadCertificate(string path, string? password)
    {
        var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;
        return X509CertificateLoader.LoadPkcs12FromFile(path, password, flags);
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
