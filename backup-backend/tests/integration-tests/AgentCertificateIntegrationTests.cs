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
}
