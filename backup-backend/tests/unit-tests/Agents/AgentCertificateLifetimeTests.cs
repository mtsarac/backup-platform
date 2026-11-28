using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Agents;
using BackupPlatform.Domain.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.UnitTests.Agents;

public sealed class AgentCertificateLifetimeTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mtls-lifetime").FullName;
    private readonly string _issuerPath;

    public AgentCertificateLifetimeTests()
    {
        Environment.SetEnvironmentVariable("AGENT_MTLS_ISSUER_PASSWORD", "issuer-test-pass-123");
        _issuerPath = Path.Combine(_directory, "issuer.pfx");
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=test-ca", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = req.CreateSelfSigned(new DateTimeOffset(2026, 11, 28, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 11, 28, 0, 0, 0, TimeSpan.Zero));
        File.WriteAllBytes(_issuerPath, ca.Export(X509ContentType.Pfx, "issuer-test-pass-123"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AGENT_MTLS_ISSUER_PASSWORD", null);
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void ExplicitTimeSpanLifetimeConfiguresShortRenewalCertificatesAndEmptyValueFallsBackToDays()
    {
        var start = new DateTimeOffset(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);
        using var csrKey = RSA.Create(2048);
        var csr = new CertificateRequest("CN=agent", csrKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
        var identity = new AgentIdentity(new AgentId(Guid.NewGuid()), new TenantId(Guid.NewGuid()), Guid.NewGuid(), 1);

        using (var shortServices = BuildServices(clock, lifetime: "00:02:00", days: null))
        {
            var authority = shortServices.GetRequiredService<AgentCertificateAuthority>();
            using var issued = X509Certificate2.CreateFromPem(authority.Issue(csr, identity));
            Assert.Equal(start.AddMinutes(2).UtcDateTime, issued.NotAfter.ToUniversalTime());
        }

        using (var defaultServices = BuildServices(clock, lifetime: "", days: null))
        {
            var authority = defaultServices.GetRequiredService<AgentCertificateAuthority>();
            using var issued = X509Certificate2.CreateFromPem(authority.Issue(csr, identity));
            Assert.Equal(start.AddDays(30).UtcDateTime, issued.NotAfter.ToUniversalTime());
        }

        Assert.Throws<InvalidOperationException>(() => BuildServices(clock, lifetime: "00:00:30", days: null));
    }

    private ServiceProvider BuildServices(TimeProvider clock, string? lifetime, string? days)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentSecurity:Mtls:Enabled"] = "true",
                ["AgentSecurity:Mtls:IssuerPfxPath"] = _issuerPath,
                ["AgentSecurity:Mtls:Lifetime"] = lifetime,
                ["AgentSecurity:Mtls:LifetimeDays"] = days
            })
            .Build();
        return new ServiceCollection()
            .AddSingleton(clock)
            .AddAgentCertificates(config)
            .BuildServiceProvider();
    }
}
