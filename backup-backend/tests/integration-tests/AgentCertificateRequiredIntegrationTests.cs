using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using BackupPlatform.Api.Security;
using BackupPlatform.Contracts.Agents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.IntegrationTests;

/// <summary>With <c>AgentSecurity:Mtls:RequireCertificate</c> every agent route authenticates by client certificate only. The route table is enumerated, so a new agent route cannot slip past.</summary>
public sealed class AgentCertificateRequiredIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string IdentityOid = "1.3.6.1.4.1.57264.1.1";

    [Fact]
    public async Task EveryAgentRouteRequiresTheClientCertificateAndIgnoresABearerCredential()
    {
        using var issuerKey = RSA.Create(2048);
        var issuerRequest = new CertificateRequest("CN=required-ca", issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var issuer = issuerRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
        await using var world = await AgentWorld.CreateAsync(fixture, configureServices: services =>
        {
            services.AddSingleton(new AgentCertificateAuthority(issuer, TimeProvider.System, TimeSpan.FromDays(7)));
            services.AddSingleton(new AgentMtlsPolicy(RequireCertificate: true));
        });
        var setup = await world.CreateTenantAsync();
        var agentA = await world.EnrollAsync(setup);
        var agentB = await world.EnrollAsync(setup);
        using var certA = await IssueAsync(world, agentA);
        using var certB = await IssueAsync(world, agentB);
        using var foreign = SelfSigned("CN=other-issuer", identity: null);
        using var forged = SelfSigned("CN=backup-agent-" + agentA.AgentId.ToString("N"), identity: agentA);
        var bearerA = AgentAuthorizationScheme.FormatHeaderValue(agentA.CredentialId, agentA.Secret);

        var routes = world.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(data => data.Policy == AuthorizationPolicies.Agent))
            .Select(endpoint => (Endpoint: endpoint, Method: endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET",
                Path: Concretize(endpoint.RoutePattern.RawText!)))
            .ToList();
        Assert.True(routes.Count >= 25, $"Only {routes.Count} agent routes found; the enumeration is broken.");
        Assert.Contains(routes, route => route.Path == "/api/agent/heartbeat");
        Assert.Contains(routes, route => route.Path.StartsWith("/hubs/agent", StringComparison.Ordinal));
        Assert.Contains(routes, route => route.Path.Contains("/storage/", StringComparison.Ordinal));
        Assert.Contains(routes, route => route.Path.StartsWith("/api/agent/downloads/", StringComparison.Ordinal));

        var bootstrap = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (endpoint, method, path) in routes)
        {
            var label = $"{method} {path}";
            if (endpoint.Metadata.GetMetadata<AllowBearerBootstrap>() is not null) bootstrap.Add(label);
            Assert.True(401 == await CallAsync(world, method, path, null, null), $"{label}: no credential");
            Assert.True(401 == await CallAsync(world, method, path, foreign, bearerA), $"{label}: certificate from another issuer, valid bearer");
            Assert.True(401 == await CallAsync(world, method, path, forged, bearerA), $"{label}: forged identity from another issuer");
            if (endpoint.Metadata.GetMetadata<AllowBearerBootstrap>() is null)
                Assert.True(401 == await CallAsync(world, method, path, null, bearerA), $"{label}: bearer alone");
            var withCertificate = await CallAsync(world, method, path, certA, null);
            Assert.True(withCertificate is not (401 or 403), $"{label}: valid certificate was refused ({withCertificate})");
        }
        Assert.Equal(["GET /api/agent/me", "POST /api/agent/certificate/enroll"], bootstrap.ToArray());
    }

    [Fact]
    public async Task TheCertificateDecidesWhichAgentTheCallerIsEvenWhenAnotherAgentsBearerRidesAlong()
    {
        using var issuerKey = RSA.Create(2048);
        var issuerRequest = new CertificateRequest("CN=identity-ca", issuerKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var issuer = issuerRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
        await using var world = await AgentWorld.CreateAsync(fixture, configureServices: services =>
        {
            services.AddSingleton(new AgentCertificateAuthority(issuer, TimeProvider.System, TimeSpan.FromDays(7)));
            services.AddSingleton(new AgentMtlsPolicy(RequireCertificate: true));
        });
        var setup = await world.CreateTenantAsync();
        var agentA = await world.EnrollAsync(setup);
        var agentB = await world.EnrollAsync(setup);
        using var certB = await IssueAsync(world, agentB);
        var response = await world.Factory.Server.SendAsync(context =>
        {
            context.Request.Scheme = "https";
            context.Request.Method = "GET";
            context.Request.Path = "/api/agent/me";
            context.Request.Headers.Authorization = AgentAuthorizationScheme.FormatHeaderValue(agentA.CredentialId, agentA.Secret);
            context.Connection.ClientCertificate = certB;
        });
        Assert.Equal(200, response.Response.StatusCode);
        using var json = await JsonDocument.ParseAsync(response.Response.Body);
        Assert.Equal(agentB.AgentId, json.RootElement.GetProperty("agentId").GetGuid());
    }

    private static async Task<X509Certificate2> IssueAsync(AgentWorld world, EnrolledAgent agent)
    {
        using var key = RSA.Create(2048);
        var csr = new CertificateRequest("CN=ignored", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
        using var response = await world.SendAgentAsync(HttpMethod.Post, "/api/agent/certificate/enroll", agent.CredentialId, agent.Secret, body: new { signingRequestPem = csr });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return X509Certificate2.CreateFromPem(AgentWorld.ReadJson(response).GetProperty("certificatePem").GetString()!);
    }

    private static X509Certificate2 SelfSigned(string subject, EnrolledAgent? identity)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, true));
        if (identity is not null)
        {
            var json = JsonSerializer.Serialize(new { Agent = identity.AgentId, Tenant = identity.TenantId, Credential = identity.CredentialId, Generation = 1 });
            request.CertificateExtensions.Add(new X509Extension(IdentityOid, Encoding.UTF8.GetBytes(json), false));
        }
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static string Concretize(string pattern)
    {
        var path = System.Text.RegularExpressions.Regex.Replace(pattern, @"\{[^}:]+:guid\}", _ => Guid.NewGuid().ToString("D"));
        path = System.Text.RegularExpressions.Regex.Replace(path, @"\{[^}]+\}", "x");
        return path.TrimEnd('?');
    }

    private static async Task<int> CallAsync(AgentWorld world, string method, string path, X509Certificate2? certificate, string? bearer)
    {
        var response = await world.Factory.Server.SendAsync(context =>
        {
            context.Request.Scheme = "https";
            context.Request.Method = method;
            context.Request.Path = path;
            context.Request.Headers[AgentProtocolHeader.Name] = "1.0";
            if (bearer is not null) context.Request.Headers.Authorization = bearer;
            if (certificate is not null) context.Connection.ClientCertificate = certificate;
        });
        return response.Response.StatusCode;
    }
}
