using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;

namespace BackupPlatform.Api.Security;

/// <summary>
/// nginx terminates TLS and forwards the presented client certificate (URL-escaped PEM, unverified: <c>ssl_verify_client optional_no_ca</c>) in this header.
/// The header is honoured only from a trusted proxy and only becomes the connection's client certificate; <see cref="AgentCertificateAuthority"/> still validates it against the issuer CA.
/// </summary>
public static class ProxyClientCertificate
{
    public const string HeaderName = "X-Client-Cert";

    /// <summary>Must run before forwarded-header processing: that rewrites RemoteIpAddress to the end client, and trust is decided on the TCP peer.</summary>
    public static IApplicationBuilder UseProxyClientCertificate(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var header = context.Request.Headers[HeaderName].ToString();
        context.Request.Headers.Remove(HeaderName);
        var peer = context.Connection.RemoteIpAddress;
        var trusted = peer is not null && context.RequestServices.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value
            .KnownProxies.Contains(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer);
        if (trusted && header.Length is > 0 and <= 16384)
        {
            try { context.Connection.ClientCertificate = X509Certificate2.CreateFromPem(Uri.UnescapeDataString(header)); }
            catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or ArgumentException or UriFormatException) { }
        }
        await next(context);
    });
}
