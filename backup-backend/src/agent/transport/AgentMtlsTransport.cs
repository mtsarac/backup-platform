using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography.X509Certificates;

namespace BackupPlatform.Agent.Transport;

/// <summary>Holds the active client certificate with reference-counted leases so renewals swap cleanly without dropping in-flight streams.</summary>
public sealed class AgentClientCertificateProvider : IDisposable
{
    private readonly object _gate = new();
    private Entry? _current;
    private long _version;
    private bool _disposed;

    private AgentClientCertificateProvider(string? certificatePath, X509Certificate2? initial)
    {
        CertificatePath = certificatePath;
        if (initial is not null) _current = new Entry(initial, ++_version);
    }

    public string? CertificatePath { get; }
    public bool IsConfigured { get => CertificatePath is not null || CurrentValidityWindow is not null; }
    public long Version => Volatile.Read(ref _version);
    public event Action? Changed;

    public (DateTimeOffset NotBefore, DateTimeOffset NotAfter, string SerialNumber)? CurrentValidityWindow
    {
        get
        {
            lock (_gate)
            {
                if (_current is null) return null;
                var cert = _current.Certificate;
                return (new DateTimeOffset(cert.NotBefore.ToUniversalTime(), TimeSpan.Zero),
                    new DateTimeOffset(cert.NotAfter.ToUniversalTime(), TimeSpan.Zero),
                    cert.SerialNumber);
            }
        }
    }

    public static AgentClientCertificateProvider FromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("AGENT_MTLS_CERTIFICATE_PATH");
        if (string.IsNullOrEmpty(path)) return new AgentClientCertificateProvider(null, null);
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("The agent mTLS certificate path must be absolute.");
        return new AgentClientCertificateProvider(path, AgentMtlsTransport.LoadCertificate(path, AgentMtlsTransport.ResolvePassword()));
    }

    public static AgentClientCertificateProvider FromCertificate(X509Certificate2 certificate, string? certificatePath = null)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return new AgentClientCertificateProvider(certificatePath, certificate);
    }

    public IDisposable? AcquireLease(out long version, out X509Certificate2? certificate)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            version = _version;
            if (_current is null) { certificate = null; return null; }
            _current.Retain();
            certificate = _current.Certificate;
            return new Lease(_current);
        }
    }

    public void Update(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        Entry? previous;
        lock (_gate)
        {
            if (_disposed)
            {
                certificate.Dispose();
                throw new ObjectDisposedException(nameof(AgentClientCertificateProvider));
            }
            previous = _current;
            _current = new Entry(certificate, ++_version);
        }
        previous?.Release();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        Entry? previous;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            previous = _current;
            _current = null;
        }
        previous?.Release();
    }

    private sealed class Entry(X509Certificate2 certificate, long version)
    {
        private int _refs = 1;
        public X509Certificate2 Certificate { get; } = certificate;
        public long Version { get; } = version;
        public void Retain() => Interlocked.Increment(ref _refs);
        public void Release()
        {
            if (Interlocked.Decrement(ref _refs) == 0) Certificate.Dispose();
        }
    }

    private sealed class Lease(Entry entry) : IDisposable
    {
        private Entry? _entry = entry;
        public void Dispose() => Interlocked.Exchange(ref _entry, null)?.Release();
    }
}

/// <summary>Loads a locally protected agent PFX for backend TLS. Default server certificate validation is never weakened.</summary>
public static class AgentMtlsTransport
{
    public static HttpMessageHandler CreateHandler(IServiceProvider services)
    {
        var provider = services.GetService<AgentClientCertificateProvider>();
        if (provider is not null)
        {
            var endpoint = services.GetRequiredService<AgentBackendEndpoint>();
            if (!provider.IsConfigured)
            {
                return CreateSocketsHandler(endpoint, null);
            }
            return new RotatingCertificateHttpHandler(provider, endpoint);
        }

        var certificate = LoadCertificate();
        if (certificate is not null)
        {
            var endpoint = services.GetRequiredService<AgentBackendEndpoint>();
            return new CertificateOwningHandler(CreateSocketsHandler(endpoint, certificate), certificate);
        }
        return new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), AllowAutoRedirect = false };
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

    public static X509Certificate2 LoadCertificate(string path, string? password)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("The agent mTLS certificate path must be absolute.");
        var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;
        return X509CertificateLoader.LoadPkcs12FromFile(path, password, flags);
    }

    private static SocketsHttpHandler CreateSocketsHandler(AgentBackendEndpoint endpoint, X509Certificate2? certificate)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), AllowAutoRedirect = false };
        if (endpoint.HasLoopbackServerTrust)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, remote, _, errors) => endpoint.ValidateLoopbackServer(remote, errors);
        }
        if (certificate is not null)
        {
            var backendHost = endpoint.BaseUri.IdnHost;
            var ipEndpoint = endpoint.BaseUri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6;
            handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate };
            handler.SslOptions.LocalCertificateSelectionCallback = (_, targetHost, _, _, _) =>
                string.Equals(targetHost, backendHost, StringComparison.OrdinalIgnoreCase)
                || (ipEndpoint && string.IsNullOrEmpty(targetHost))
                    ? certificate
                    : null!;
        }
        return handler;
    }

    private sealed class RotatingCertificateHttpHandler(AgentClientCertificateProvider provider, AgentBackendEndpoint endpoint) : HttpMessageHandler
    {
        private readonly object _gate = new();
        private HandlerSlot? _active;
        private bool _disposed;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HandlerSlot slot;
            HandlerSlot? retired = null;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_active is null || _active.Version != provider.Version)
                {
                    var lease = provider.AcquireLease(out var version, out var certificate);
                    retired = _active;
                    _active = new HandlerSlot(version, CreateSocketsHandler(endpoint, certificate), lease);
                }
                slot = _active;
                slot.Retain();
            }
            retired?.Release();
            try
            {
                return await slot.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                slot.Release();
            }
        }

        protected override void Dispose(bool disposing)
        {
            HandlerSlot? retired = null;
            if (disposing)
            {
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        _disposed = true;
                        retired = _active;
                        _active = null;
                    }
                }
            }
            retired?.Release();
            base.Dispose(disposing);
        }

        private sealed class HandlerSlot(long version, SocketsHttpHandler handler, IDisposable? certificateLease)
        {
            private readonly HttpMessageInvoker _invoker = new(handler, disposeHandler: true);
            private int _refs = 1;
            public long Version { get; } = version;
            public void Retain() => Interlocked.Increment(ref _refs);
            public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                _invoker.SendAsync(request, cancellationToken);
            public void Release()
            {
                if (Interlocked.Decrement(ref _refs) == 0)
                {
                    try { _invoker.Dispose(); }
                    finally { certificateLease?.Dispose(); }
                }
            }
        }
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
