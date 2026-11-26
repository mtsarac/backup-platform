using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;

namespace BackupPlatform.Api.Security;

/// <summary>
/// A TLS-terminating proxy forwards the presented client certificate without validating its chain.
/// The header is honoured only from a trusted proxy and only becomes the connection's client certificate; <see cref="AgentCertificateAuthority"/> still validates it against the issuer CA.
/// </summary>
public static class ProxyClientCertificate
{
    public const string HeaderName = "X-Client-Cert";

    /// <summary>Must run before forwarded-header processing: that rewrites RemoteIpAddress to the end client, and trust is decided on the TCP peer.</summary>
    public static IApplicationBuilder UseProxyClientCertificate(this IApplicationBuilder app)
    {
        var configuration = app.ApplicationServices.GetRequiredService<IConfiguration>();
        var headerName = configuration["Authentication:ClientCertificateHeader:Name"] ?? HeaderName;
        var format = configuration["Authentication:ClientCertificateHeader:Format"] ?? "UrlEncodedPem";
        if (string.IsNullOrWhiteSpace(headerName) || format is not ("UrlEncodedPem" or "Traefik"))
            throw new InvalidOperationException("Invalid client certificate header configuration.");

        return app.Use(async (context, next) =>
        {
            var header = context.Request.Headers[headerName].ToString();
            context.Request.Headers.Remove(headerName);
            var peer = context.Connection.RemoteIpAddress;
            var trusted = peer is not null && context.RequestServices.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value
                .KnownProxies.Contains(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer);
            if (trusted && header.Length is > 0 and <= 16384)
            {
                try
                {
                    context.Connection.ClientCertificate = format == "Traefik"
                        ? X509CertificateLoader.LoadCertificate(Convert.FromBase64String(header.Split(',', 2)[0]))
                        : X509Certificate2.CreateFromPem(Uri.UnescapeDataString(header));
                }
                catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or ArgumentException or UriFormatException or FormatException) { }
            }
            await next(context);
        });
    }
}
