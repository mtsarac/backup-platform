using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Agents;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupPlatform.IntegrationTests;

/// <summary>Controllable clock that starts at the real time so cookie and database defaults stay coherent.</summary>
internal sealed class MutableClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
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

    private AgentWorld(PostgreSqlFixture database, MutableClock clock, AuthApiFactory factory)
    {
        Database = database;
        Clock = clock;
        Factory = factory;
        Anonymous = factory.CreateReplayClient();
    }

    public PostgreSqlFixture Database { get; }
    public MutableClock Clock { get; }
    public AuthApiFactory Factory { get; }
    public HttpClient Anonymous { get; }

    public static Task<AgentWorld> CreateAsync(PostgreSqlFixture database, AgentSecurityOptions? options = null)
    {
        var clock = new MutableClock();
        return Task.FromResult(new AgentWorld(database, clock, new AuthApiFactory(database.ConnectionString, clock, options)));
    }

    public async ValueTask DisposeAsync()
    {
        Anonymous.Dispose();
        await Factory.DisposeAsync();
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

    public async Task<BrowserSession> SignInAsync(Account account)
    {
        var client = Factory.CreateBrowser();
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

    public async Task<EnrolledAgent> EnrollAsync(TenantSetup setup, Guid? installationId = null)
    {
        var (_, token) = await CreateTokenAsync(setup);
        var request = EnrollmentRequest(token, installationId);
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
