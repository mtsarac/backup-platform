using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BackupPlatform.Agent.Transport;

/// <summary>The backend the agent talks to. Always an absolute HTTPS URI: credentials only ever travel over TLS.</summary>
public sealed class AgentBackendEndpoint
{
    private readonly byte[]? _trustedLoopbackHash;

    public AgentBackendEndpoint(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The backend address must be an absolute HTTPS URI.", nameof(baseUri));
        }
        BaseUri = Normalize(baseUri);
    }

    private AgentBackendEndpoint(Uri baseUri, byte[]? trustedLoopbackHash)
    {
        BaseUri = Normalize(baseUri);
        _trustedLoopbackHash = trustedLoopbackHash;
    }

    /// <summary>The backend root, with a trailing slash so relative request paths append to it.</summary>
    public Uri BaseUri { get; }

    internal bool HasLoopbackServerTrust => _trustedLoopbackHash is not null;

    internal bool ValidateLoopbackServer(X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (_trustedLoopbackHash is null || errors != SslPolicyErrors.RemoteCertificateChainErrors || certificate is null) return false;
        using var loaded = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return CryptographicOperations.FixedTimeEquals(loaded.GetCertHash(HashAlgorithmName.SHA256), _trustedLoopbackHash);
    }

    /// <summary>
    /// Test seam: allows a plain-HTTP loopback address or an explicit loopback server certificate so tests can run a real server.
    /// Not reachable from other assemblies, so the production HTTPS and trust requirements stay intact.
    /// </summary>
    internal static AgentBackendEndpoint ForLoopbackTesting(Uri baseUri, X509Certificate2? trustedServerCertificate = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri || !baseUri.IsLoopback || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Only loopback addresses are allowed for testing.", nameof(baseUri));
        }
        return new AgentBackendEndpoint(baseUri, trustedServerCertificate?.GetCertHash(HashAlgorithmName.SHA256));
    }

    private static Uri Normalize(Uri uri)
    {
        var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }
        return builder.Uri;
    }
}
