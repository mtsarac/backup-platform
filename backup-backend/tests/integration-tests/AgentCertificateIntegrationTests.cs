using System.Net.Http.Json;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BackupPlatform.Api.Security;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.IntegrationTests;

public sealed class AgentCertificateIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task OnlyAnAuthenticatedAgentCanObtainACertificateForItsOwnIdentity()
    {
        using var authorityKey = RSA.Create(2048);
        var issuerRequest = new CertificateRequest("CN=agent-test-ca", authorityKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var authority = issuerRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
        await using var world = await AgentWorld.CreateAsync(fixture, configureServices: services =>
        {
            services.AddSingleton(new AgentCertificateAuthority(authority, TimeProvider.System, TimeSpan.FromDays(7)));
        });
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        using var key = RSA.Create(2048);
        var csr = new CertificateRequest("CN=attacker-chosen-name", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
        using var denied = await world.Anonymous.PostAsJsonAsync("/api/agent/certificate/enroll", new { signingRequestPem = csr });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var response = await world.SendAgentAsync(HttpMethod.Post, "/api/agent/certificate/enroll", agent.CredentialId, agent.Secret, body: new { signingRequestPem = csr });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pem = AgentWorld.ReadJson(response).GetProperty("certificatePem").GetString()!;
        using var certificate = X509Certificate2.CreateFromPem(pem);
        Assert.DoesNotContain("attacker", certificate.Subject);
        var identity = world.Factory.Services.GetRequiredService<AgentCertificateAuthority>().Validate(certificate);
        Assert.NotNull(identity);
        Assert.Equal(agent.AgentId, identity.AgentId.Value);
        Assert.Equal(agent.CredentialId, identity.CredentialId);
    }

    [Fact]
    public async Task CertificateRotationRequiresPendingProofAndImmediatelyFencesTheOldGeneration()
    {
        using var issuerKey = RSA.Create(2048);
        var issuerRequest = new CertificateRequest("CN=rotation-ca", issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var issuer = issuerRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
        await using var world = await AgentWorld.CreateAsync(fixture, configureServices: services =>
            services.AddSingleton(new AgentCertificateAuthority(issuer, TimeProvider.System, TimeSpan.FromDays(7))));
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        using var key = RSA.Create(2048);
        var csr = new CertificateRequest("CN=ignored", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
        using var enrolled = await world.SendAgentAsync(HttpMethod.Post, "/api/agent/certificate/enroll", agent.CredentialId, agent.Secret, body: new { signingRequestPem = csr });
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
        using var oldCertificate = X509Certificate2.CreateFromPem(AgentWorld.ReadJson(enrolled).GetProperty("certificatePem").GetString()!);
        Assert.Equal(200, await CertificateCallAsync("/api/agent/me", "GET", oldCertificate));
        var pending = await world.BeginRotationAsync(agent.CredentialId, agent.Secret);
        using var rotated = await world.SendAgentAsync(HttpMethod.Post, "/api/agent/certificate/enroll", pending.CredentialId, pending.Secret, body: new { signingRequestPem = csr });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        using var newCertificate = X509Certificate2.CreateFromPem(AgentWorld.ReadJson(rotated).GetProperty("certificatePem").GetString()!);
        Assert.Equal(401, await CertificateCallAsync("/api/agent/me", "GET", newCertificate));
        Assert.Equal(200, await CertificateCallAsync("/api/agent/credential/confirm", "POST", newCertificate));
        Assert.Equal(401, await CertificateCallAsync("/api/agent/me", "GET", oldCertificate));
        Assert.Equal(200, await CertificateCallAsync("/api/agent/me", "GET", newCertificate));
        using var revoked = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{setup.Tenant.Id.Value}/agents/{agent.AgentId}/revoke");
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(401, await CertificateCallAsync("/api/agent/me", "GET", newCertificate));

        async Task<int> CertificateCallAsync(string path, string method, X509Certificate2 certificate)
        {
            var response = await world.Factory.Server.SendAsync(context =>
            {
                context.Request.Scheme = "https";
                context.Request.Method = method;
                context.Request.Path = path;
                context.Connection.ClientCertificate = certificate;
            });
            return response.Response.StatusCode;
        }
    }

    [Theory]
    [InlineData("X-Client-Cert", "UrlEncodedPem")]
    [InlineData("X-Forwarded-Tls-Client-Cert", "UrlEncodedPem")]
    [InlineData("X-Forwarded-Tls-Client-Cert", "Traefik")]
    public async Task ProxyForwardedCertificateIsHonouredOnlyFromTheTrustedProxyAndStillValidatedAgainstTheIssuer(string headerName, string format)
    {
        using var issuerKey = RSA.Create(2048);
        var issuerRequest = new CertificateRequest("CN=proxy-ca", issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var issuer = issuerRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
        await using var world = await AgentWorld.CreateAsync(fixture,
            configuration: new Dictionary<string, string?>
            {
                ["Authentication:TrustedProxies:0"] = "10.9.8.7",
                ["Authentication:ClientCertificateHeader:Name"] = headerName,
                ["Authentication:ClientCertificateHeader:Format"] = format
            },
            configureServices: services => services.AddSingleton(new AgentCertificateAuthority(issuer, TimeProvider.System, TimeSpan.FromDays(7))));
        var setup = await world.CreateTenantAsync();
        var agent = await world.EnrollAsync(setup);
        using var key = RSA.Create(2048);
        var csr = new CertificateRequest("CN=ignored", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
        using var enrolled = await world.SendAgentAsync(HttpMethod.Post, "/api/agent/certificate/enroll", agent.CredentialId, agent.Secret, body: new { signingRequestPem = csr });
        var valid = AgentWorld.ReadJson(enrolled).GetProperty("certificatePem").GetString()!;
        using var foreignKey = RSA.Create(2048);
        var foreignRequest = new CertificateRequest("CN=foreign", foreignKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        foreignRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var foreign = foreignRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        using var validCertificate = X509Certificate2.CreateFromPem(valid);
        var validHeader = format == "Traefik" ? Convert.ToBase64String(validCertificate.RawData) + "," + Convert.ToBase64String(issuer.RawData) : Uri.EscapeDataString(valid);
        var foreignHeader = format == "Traefik" ? Convert.ToBase64String(foreign.RawData) : Uri.EscapeDataString(foreign.ExportCertificatePem());
        Assert.Equal(200, await ForwardedAsync(validHeader, "10.9.8.7"));
        Assert.Equal(401, await ForwardedAsync(validHeader, "203.0.113.5"));
        Assert.Equal(401, await ForwardedAsync(validHeader, null));
        Assert.Equal(401, await ForwardedAsync(foreignHeader, "10.9.8.7"));
        Assert.Equal(401, await ForwardedAsync("not a certificate", "10.9.8.7"));

        async Task<int> ForwardedAsync(string header, string? peer)
        {
            var response = await world.Factory.Server.SendAsync(context =>
            {
                context.Request.Scheme = "https";
                context.Request.Method = "GET";
                context.Request.Path = "/api/agent/me";
                context.Request.Headers[headerName] = header;
                if (peer is not null) context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(peer);
            });
            return response.Response.StatusCode;
        }
    }
}
