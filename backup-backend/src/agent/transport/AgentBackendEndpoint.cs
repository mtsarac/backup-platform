namespace BackupPlatform.Agent.Transport;

/// <summary>The backend the agent talks to. Always an absolute HTTPS URI: credentials only ever travel over TLS.</summary>
public sealed class AgentBackendEndpoint
{
    public AgentBackendEndpoint(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The backend address must be an absolute HTTPS URI.", nameof(baseUri));
        }
        BaseUri = Normalize(baseUri);
    }

    private AgentBackendEndpoint(Uri baseUri, bool _) => BaseUri = Normalize(baseUri);

    /// <summary>The backend root, with a trailing slash so relative request paths append to it.</summary>
    public Uri BaseUri { get; }

    /// <summary>
    /// Test seam: allows a plain-HTTP loopback address so tests can run a real server without certificates.
    /// Not reachable from other assemblies, so the production HTTPS requirement stays intact.
    /// </summary>
    internal static AgentBackendEndpoint ForLoopbackTesting(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri || !baseUri.IsLoopback || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Only loopback addresses are allowed for testing.", nameof(baseUri));
        }
        return new AgentBackendEndpoint(baseUri, true);
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
