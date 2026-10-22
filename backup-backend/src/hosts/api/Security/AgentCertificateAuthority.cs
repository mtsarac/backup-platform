using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using BackupPlatform.Application.Agents;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Api.Security;

/// <summary>Issues client-auth-only certificates bound to the existing revocable agent credential generation.</summary>
public sealed class AgentCertificateAuthority(X509Certificate2 issuer, TimeProvider time, TimeSpan lifetime) : IDisposable
{
    private const string IdentityOid = "1.3.6.1.4.1.57264.1.1";
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";
    private sealed record Identity(Guid Agent, Guid Tenant, Guid Credential, int Generation);
    public void Dispose() => issuer.Dispose();

    public string Issue(string signingRequestPem, AgentIdentity identity)
    {
        if (signingRequestPem.Length is < 32 or > 16384) throw new ArgumentException("Invalid signing request.");
        var csr = CertificateRequest.LoadSigningRequestPem(signingRequestPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default, RSASignaturePadding.Pkcs1);
        using var rsa = csr.PublicKey.GetRSAPublicKey();
        using var ec = csr.PublicKey.GetECDsaPublicKey();
        if ((rsa is null || rsa.KeySize < 2048) && (ec is null || ec.KeySize < 256)) throw new ArgumentException("A strong RSA or ECDSA public key is required.");
        var request = new CertificateRequest(new X500DistinguishedName("CN=backup-agent-" + identity.AgentId.Value.ToString("N")), csr.PublicKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(ClientAuthOid) }, true));
        request.CertificateExtensions.Add(new X509Extension(IdentityOid, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Identity(identity.AgentId.Value, identity.TenantId.Value, identity.CredentialId, identity.CredentialGeneration))), false));
        var now = time.GetUtcNow();
        var expires = now + lifetime;
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromDays(90) || expires > issuer.NotAfter.ToUniversalTime()) throw new InvalidOperationException("The agent certificate issuer must be renewed.");
        using var certificate = request.Create(issuer, now.AddMinutes(-1), expires, RandomNumberGenerator.GetBytes(16));
        return certificate.ExportCertificatePem();
    }

    public AgentIdentity? Validate(X509Certificate2 certificate)
    {
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(issuer);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationTime = time.GetUtcNow().UtcDateTime;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ClientAuthOid));
            if (!chain.Build(certificate)) return null;
            var extension = certificate.Extensions.SingleOrDefault(extension => extension.Oid?.Value == IdentityOid);
            if (extension is null) return null;
            var identity = JsonSerializer.Deserialize<Identity>(extension.RawData);
            if (identity is null || identity.Agent == Guid.Empty || identity.Tenant == Guid.Empty || identity.Credential == Guid.Empty || identity.Generation < 1) return null;
            return new AgentIdentity(new AgentId(identity.Agent), new TenantId(identity.Tenant), identity.Credential, identity.Generation);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException or InvalidOperationException) { return null; }
    }
}

/// <summary>Explicit mTLS issuer configuration; private-key material is loaded at runtime, never copied into configuration.</summary>
public static class AgentCertificateServiceCollectionExtensions
{
    public static IServiceCollection AddAgentCertificates(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("AgentSecurity:Mtls");
        if (!section.GetValue<bool>("Enabled")) return services;
        var path = section["IssuerPfxPath"];
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidOperationException("mTLS requires an absolute issuer PFX path.");
        var days = section.GetValue<int?>("LifetimeDays") ?? 30;
        if (days is < 1 or > 90) throw new InvalidOperationException("mTLS certificate lifetime must be 1–90 days.");
        services.AddSingleton(sp =>
        {
            var issuer = X509CertificateLoader.LoadPkcs12FromFile(path, Environment.GetEnvironmentVariable("AGENT_MTLS_ISSUER_PASSWORD"), X509KeyStorageFlags.EphemeralKeySet);
            if (!issuer.HasPrivateKey || issuer.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault()?.CertificateAuthority != true)
                throw new InvalidOperationException("The mTLS issuer must be a CA certificate with a private key.");
            return new AgentCertificateAuthority(issuer, sp.GetRequiredService<TimeProvider>(), TimeSpan.FromDays(days));
        });
        return services;
    }
}
