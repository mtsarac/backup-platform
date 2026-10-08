using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BackupPlatform.Agents;
using BackupPlatform.Agents.Capabilities;
using BackupPlatform.Agents.Connectivity;
using BackupPlatform.Api.Security;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupPlatform.IntegrationTests;

/// <summary>Controllable clock that starts at the real time so cookie and database defaults stay coherent.</summary>
internal sealed class MutableClock(DateTimeOffset? start = null, bool live = false) : TimeProvider
{
    // Whole seconds: PostgreSQL stores microseconds, so sub-microsecond ticks would not round-trip in assertions.
    private DateTimeOffset _now = start ?? DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    public override DateTimeOffset GetUtcNow() => live ? DateTimeOffset.UtcNow : _now;
    public void Advance(TimeSpan by) => _now += by;
}

internal sealed record Account(UserId UserId, string Identifier, string Password);

internal sealed record EnrolledAgent(Guid AgentId, Guid CredentialId, string Secret, Guid TenantId, Guid InstallationId);

/// <summary>A cookie-authenticated user; unsafe requests fetch a fresh antiforgery token first, as the frontend does.</summary>
internal sealed class BrowserSession(HttpClient client)
{
    public HttpClient Client { get; } = client;

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, bool includeAntiforgery = true)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        if (method != HttpMethod.Get && includeAntiforgery)
        {
            using var tokenResponse = await Client.GetAsync("/api/auth/antiforgery");
            var token = AgentWorld.ReadJson(tokenResponse).GetProperty("requestToken").GetString()!;
            request.Headers.Add("X-CSRF-TOKEN", token);
        }
        return await Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path);
}

internal sealed class AgentWorld : IAsyncDisposable
{
    public static readonly Guid DefaultInstallation = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private AgentWorld(PostgreSqlFixture database, MutableClock clock, AuthApiFactory factory, X509Certificate2? certificate)
    {
        Database = database;
        Clock = clock;
        Factory = factory;
        Certificate = certificate;
        if (certificate is not null)
        {
            // Must precede any client or service access so the factory hosts real Kestrel instead of TestServer.
            factory.UseKestrel();
            factory.StartServer();
            ServerAddress = new Uri(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        }
        Anonymous = certificate is null ? factory.CreateReplayClient() : CreateTrustingClient(ServerAddress!, handleCookies: false);
    }

    public X509Certificate2? Certificate { get; }
    /// <summary>Base address of the real Kestrel listener; null when the world runs on the in-memory TestServer.</summary>
    public Uri? ServerAddress { get; }

    public PostgreSqlFixture Database { get; }
    public MutableClock Clock { get; }
    public AuthApiFactory Factory { get; }
    public HttpClient Anonymous { get; }

    public static Task<AgentWorld> CreateAsync(
        PostgreSqlFixture database,
        AgentSecurityOptions? options = null,
        AgentConnectivityOptions? connectivity = null,
        AgentRateLimitOptions? limits = null,
        MutableClock? clock = null,
        bool kestrel = false,
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        clock ??= new MutableClock();
        var certificate = kestrel ? CreateSelfSignedCertificate() : null;
        return Task.FromResult(new AgentWorld(
            database, clock, new AuthApiFactory(database.ConnectionString, clock, options, connectivity, limits, certificate, configuration), certificate));
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
    }

    /// <summary>A client for the real listener that accepts the test's self-signed certificate.</summary>
    public static HttpClient CreateTrustingClient(Uri address, bool handleCookies, System.Net.CookieContainer? cookies = null) => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        UseCookies = handleCookies,
        CookieContainer = cookies ?? new System.Net.CookieContainer(),
        AllowAutoRedirect = false
    })
    { BaseAddress = address };

    public async ValueTask DisposeAsync()
    {
        Anonymous.Dispose();
        await Factory.DisposeAsync();
        Certificate?.Dispose();
    }

    /// <summary>A heartbeat body any real agent would send.</summary>
    public static AgentHeartbeatRequest Heartbeat(string agentVersion = "1.2.3", string? capabilityRevision = null) => new()
    {
        AgentVersion = agentVersion,
        Platform = "linux",
        CapabilityRevision = capabilityRevision,
        Runtime = new AgentRuntimeInfo { OsDescription = "test-os", RuntimeVersion = "10.0.0" }
    };

    public async Task<HttpResponseMessage> SendAgentAsync(
        HttpMethod method, string path, Guid credentialId, string secret, string? protocol = "1.0", object? body = null, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(credentialId, secret));
        if (protocol is not null)
        {
            request.Headers.TryAddWithoutValidation(AgentProtocolHeader.Name, protocol);
        }
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await (client ?? Anonymous).SendAsync(request);
    }

    public Task<HttpResponseMessage> HeartbeatAsync(EnrolledAgent agent, object? body = null, string? protocol = "1.0") =>
        SendAgentAsync(HttpMethod.Post, "/api/agent/heartbeat", agent.CredentialId, agent.Secret, protocol, body ?? Heartbeat());

    public Task<HttpResponseMessage> HeartbeatWithCredentialAsync(Guid credentialId, string secret) =>
        SendAgentAsync(HttpMethod.Post, "/api/agent/heartbeat", credentialId, secret, "1.0", Heartbeat());

    public Task<HttpResponseMessage> SyncCapabilitiesAsync(EnrolledAgent agent, object body, string? protocol = "1.0") =>
        SendAgentAsync(HttpMethod.Put, "/api/agent/capabilities", agent.CredentialId, agent.Secret, protocol, body);

    /// <summary>A sync body whose revision matches its capabilities, as the real agent builds it.</summary>
    public static AgentCapabilitySyncRequest CapabilitySnapshot(params AgentCapabilityReport[] capabilities) => new()
    {
        Revision = AgentCapabilityRevision.Compute(capabilities),
        Capabilities = capabilities
    };

    public static AgentCapabilityReport CapabilityReport(
        string identifier, string support = "Supported", IReadOnlyDictionary<string, string>? metadata = null) =>
        new() { Identifier = identifier, Support = support, Metadata = metadata };

    public async Task<List<AgentCapabilityRecord>> CapabilityRowsOfAsync(Guid agentId)
    {
        await using var context = Database.CreatePlatformContext();
        var id = new AgentId(agentId);
        return await context.AgentCapabilities.AsNoTracking().Where(row => row.AgentId == id).OrderBy(row => row.Identifier).ToListAsync();
    }

    public Task<HttpResponseMessage> PollAsync(EnrolledAgent agent, string? cursor = null, string? protocol = "1.0") =>
        SendAgentAsync(HttpMethod.Get, "/api/agent/tasks" + (cursor is null ? string.Empty : $"?cursor={cursor}"), agent.CredentialId, agent.Secret, protocol);

    public async Task<AgentConnectivityRecord?> ConnectivityOfAsync(Guid agentId)
    {
        await using var context = Database.CreatePlatformContext();
        return await context.AgentConnectivity.AsNoTracking().SingleOrDefaultAsync(row => row.AgentId == new AgentId(agentId));
    }

    public async Task<JsonElement> AgentViewAsync(TenantSetup setup, Guid agentId)
    {
        using var list = await setup.Admin.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/agents");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        return ReadJson(list).EnumerateArray().Single(entry => entry.GetProperty("agentId").GetGuid() == agentId);
    }

    public static JsonElement ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        return document.RootElement.Clone();
    }

    public async Task<Account> SeedUserAsync(bool platformAdmin = false)
    {
        var user = new User(UserId.New(), "Agent test user");
        var identifier = $"user-{Guid.NewGuid():N}";
        var password = $"Local-only-{Guid.NewGuid():N}!";
        await using var context = Database.CreatePlatformContext();
        context.Users.Add(user);
        context.PasswordCredentials.Add(new PasswordCredential(user.Id, identifier, new PasswordHashService().Hash(user.Id, password)));
        if (platformAdmin)
        {
            context.PlatformAdmins.Add(new PlatformAdmin(user.Id));
        }
        await context.SaveChangesAsync();
        return new Account(user.Id, identifier, password);
    }

    public async Task<Tenant> AddTenantAsync()
    {
        var tenant = new Tenant(TenantId.New(), $"Tenant {Guid.NewGuid():N}");
        await using var context = Database.CreatePlatformContext();
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();
        return tenant;
    }

    public async Task GrantAsync(Tenant tenant, Account account, TenantRole role)
    {
        await using var context = Database.CreatePlatformContext();
        context.TenantMemberships.Add(new TenantMembership(tenant.Id, account.UserId, role));
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// The legacy unlimited subscription, so the tenant's new backup runs are admitted. A new tenant has none, and that denies new runs by design;
    /// suites about running backups (not about the commercial layer) call this once after creating the tenant.
    /// </summary>
    public Task EntitleAsync(Tenant tenant) => ManagedService.ManagedServiceTestSupport.GrantLegacyUnlimitedAsync(Database, tenant.Id);

    public async Task<BrowserSession> SignInAsync(Account account, System.Net.CookieContainer? cookies = null)
    {
        var client = ServerAddress is null ? Factory.CreateBrowser() : CreateTrustingClient(ServerAddress, handleCookies: true, cookies);
        var session = new BrowserSession(client);
        using var response = await session.SendAsync(HttpMethod.Post, "/api/auth/login",
            new { loginIdentifier = account.Identifier, password = account.Password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return session;
    }

    /// <summary>A tenant with enrollment enabled and a signed-in administrator, the common starting point.</summary>
    public async Task<TenantSetup> CreateTenantAsync(TenantRole role = TenantRole.Admin, bool enrollmentEnabled = true)
    {
        var tenant = await AddTenantAsync();
        var account = await SeedUserAsync();
        await GrantAsync(tenant, account, role);
        if (enrollmentEnabled)
        {
            await SetEnrollmentAsync(tenant, true);
        }
        return new TenantSetup(tenant, account, await SignInAsync(account));
    }

    public async Task SetEnrollmentAsync(Tenant tenant, bool enabled)
    {
        var platformAdmin = await SeedUserAsync(platformAdmin: true);
        var session = await SignInAsync(platformAdmin);
        using var response = await session.SendAsync(HttpMethod.Put, $"/api/platform/tenants/{tenant.Id.Value}/agent-enrollment", new { enabled });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public async Task<(Guid TokenId, string Token)> CreateTokenAsync(TenantSetup setup, int? lifetimeMinutes = null)
    {
        using var response = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{setup.Tenant.Id.Value}/agent-enrollment-tokens",
            lifetimeMinutes is null ? null : new { lifetimeMinutes });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = ReadJson(response);
        return (json.GetProperty("tokenId").GetGuid(), json.GetProperty("token").GetString()!);
    }

    public static AgentEnrollmentRequest EnrollmentRequest(string token, Guid? installationId = null, string protocolVersion = "1.0") => new()
    {
        EnrollmentToken = token,
        InstallationId = installationId ?? Guid.NewGuid(),
        AgentVersion = "1.2.3",
        ProtocolVersion = protocolVersion,
        Platform = "linux",
        HostName = "integration-host",
        Capabilities = ["backup.file-system"]
    };

    public Task<HttpResponseMessage> PostEnrollAsync(AgentEnrollmentRequest request) =>
        Anonymous.PostAsJsonAsync("/api/agent/enroll", request);

    public async Task<EnrolledAgent> EnrollAsync(TenantSetup setup, Guid? installationId = null, string protocolVersion = "1.0")
    {
        var (_, token) = await CreateTokenAsync(setup);
        var request = EnrollmentRequest(token, installationId, protocolVersion);
        using var response = await PostEnrollAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = ReadJson(response);
        var credential = json.GetProperty("credential");
        return new EnrolledAgent(
            json.GetProperty("agentId").GetGuid(), credential.GetProperty("credentialId").GetGuid(),
            credential.GetProperty("secret").GetString()!, setup.Tenant.Id.Value, request.InstallationId);
    }

    public static async Task<HttpResponseMessage> AgentCallAsync(
        HttpClient client, HttpMethod method, string path, Guid credentialId, string secret)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Authorization", AgentAuthorizationScheme.FormatHeaderValue(credentialId, secret));
        return await client.SendAsync(request);
    }

    public Task<HttpResponseMessage> AgentCallAsync(HttpMethod method, string path, Guid credentialId, string secret) =>
        AgentCallAsync(Anonymous, method, path, credentialId, secret);

    public Task<HttpResponseMessage> AgentMeAsync(Guid credentialId, string secret) =>
        AgentCallAsync(HttpMethod.Get, "/api/agent/me", credentialId, secret);

    /// <summary>Starts a rotation with the supplied credential and returns the pending credential.</summary>
    public async Task<(Guid CredentialId, string Secret, int Generation)> BeginRotationAsync(Guid credentialId, string secret)
    {
        using var response = await AgentCallAsync(HttpMethod.Post, "/api/agent/credential/rotate", credentialId, secret);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = ReadJson(response);
        return (json.GetProperty("credentialId").GetGuid(), json.GetProperty("secret").GetString()!, json.GetProperty("generation").GetInt32());
    }

    public async Task<string> DumpAgentTablesAsync()
    {
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync();
        var text = new System.Text.StringBuilder();
        foreach (var table in new[] { "agent_enrollment_tokens", "agent_credentials", "agents", "tenant_agent_enrollment_policies" })
        {
            await using var command = new NpgsqlCommand($"SELECT coalesce(string_agg(row_to_json(t)::text, E'\\n'), '') FROM {table} t", connection);
            text.AppendLine((string)(await command.ExecuteScalarAsync())!);
        }
        return text.ToString();
    }

    public async Task<List<BackupPlatform.Agents.Credentials.AgentCredential>> CredentialsOfAsync(Guid agentId)
    {
        await using var context = Database.CreatePlatformContext();
        var id = new AgentId(agentId);
        return await context.AgentCredentials.AsNoTracking().Where(row => row.AgentId == id).OrderBy(row => row.Generation).ThenBy(row => row.IssuedAt).ToListAsync();
    }
}

internal sealed record TenantSetup(Tenant Tenant, Account Account, BrowserSession Admin);
