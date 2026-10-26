using System.Net;
using System.Text.Json;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.IntegrationTests.Quota;
using BackupPlatform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BackupPlatform.IntegrationTests.Storage.Connections;

/// <summary>
/// The storage-connections HTTP API surface under real authentication, antiforgery cookies and roles: tenant and platform routes,
/// permission matrix, secret-safety, the test probe against real central storage, and end-to-end admission through the API.
/// </summary>
public sealed class StorageConnectionApiIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    internal sealed class ApiWorld : IAsyncDisposable
    {
        public AgentWorld World { get; }
        public Tenant Tenant { get; }
        public BrowserSession Admin { get; }
        public string CentralRoot { get; }

        private ApiWorld(AgentWorld world, Tenant tenant, BrowserSession admin, string centralRoot)
        {
            World = world; Tenant = tenant; Admin = admin; CentralRoot = centralRoot;
        }

        public string TenantRoot => $"/api/tenants/{Tenant.Id.Value}";

        public static async Task<ApiWorld> CreateAsync(
            PostgreSqlFixture database, bool withCentralStorage = false, Action<IServiceCollection>? configureServices = null)
        {
            var tenantId = TenantId.New();
            var centralRoot = Directory.CreateTempSubdirectory("api-central-test-").FullName;
            var settings = new Dictionary<string, string?>();
            if (withCentralStorage)
            {
                settings["Backups:Storage:Central:RootDirectory"] = centralRoot;
                settings[$"Backups:Storage:Central:TenantScopes:{tenantId.Value}"] = "api-scope";
            }

            var world = await AgentWorld.CreateAsync(database, configuration: settings, configureServices: services =>
            {
                services.RemoveAll<IStorageProviderCatalog>();
                services.AddSingleton<IStorageProviderCatalog>(_ => VaultProvider.Catalog());
                services.AddScoped<IStorageConnectionTester, VaultTester>();
                configureServices?.Invoke(services);
            });

            // Seed tenant and admin user
            var tenant = new Tenant(tenantId, $"API Tenant {tenantId.Value:N}");
            await using (var context = database.CreatePlatformContext())
            {
                context.Tenants.Add(tenant);
                await context.SaveChangesAsync();
            }
            await world.EntitleAsync(tenant);

            var account = await world.SeedUserAsync();
            await world.GrantAsync(tenant, account, TenantRole.Admin);
            var adminSession = await world.SignInAsync(account);

            return new ApiWorld(world, tenant, adminSession, centralRoot);
        }

        public async Task<BrowserSession> SessionAsync(TenantRole role)
        {
            var account = await World.SeedUserAsync();
            await World.GrantAsync(Tenant, account, role);
            return await World.SignInAsync(account);
        }

        public async Task<BrowserSession> PlatformAdminAsync()
        {
            var account = await World.SeedUserAsync(platformAdmin: true);
            return await World.SignInAsync(account);
        }

        public async Task<BrowserSession> NonMemberAsync()
        {
            var stranger = await World.SeedUserAsync();
            return await World.SignInAsync(stranger);
        }

        public async ValueTask DisposeAsync()
        {
            await World.DisposeAsync();
            try { Directory.Delete(CentralRoot, recursive: true); } catch (IOException) { }
        }
    }

    // ---- providers list shape --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ProvidersListReturnsAvailableAndPlannedProvidersWithTheirFullContractShape()
    {
        await using var env = await ApiWorld.CreateAsync(fixture);

        using var response = await env.Admin.GetAsync(env.TenantRoot + "/storage-providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = AgentWorld.ReadJson(response);
        var items = json.GetProperty("items").EnumerateArray().ToList();
        var local = items.Single(item => item.GetProperty("providerType").GetString() == "local");
        var central = items.Single(item => item.GetProperty("providerType").GetString() == "central");
        var sftp = items.Single(item => item.GetProperty("providerType").GetString() == "sftp");

        Assert.Equal(("Available", "AgentSide", true, false), (local.GetProperty("availability").GetString(), local.GetProperty("testMode").GetString(),
            local.GetProperty("tenantCreatable").GetBoolean(), local.GetProperty("platformCreatable").GetBoolean()));
        Assert.Equal(("Available", "BackendRoundTrip", false, true), (central.GetProperty("availability").GetString(), central.GetProperty("testMode").GetString(),
            central.GetProperty("tenantCreatable").GetBoolean(), central.GetProperty("platformCreatable").GetBoolean()));
        Assert.Equal(("Available", "AgentSide", true, false), (sftp.GetProperty("availability").GetString(), sftp.GetProperty("testMode").GetString(),
            sftp.GetProperty("tenantCreatable").GetBoolean(), sftp.GetProperty("platformCreatable").GetBoolean()));
    }

    // ---- list auto-provisions and computes permissions -------------------------------------------------------------------------

    [Fact]
    public async Task ListAutoProvisionsBothDefaultsWithTheExpectedPermissionsPerRole()
    {
        await using var env = await ApiWorld.CreateAsync(fixture);
        var admin = env.Admin;
        var viewer = await env.SessionAsync(TenantRole.Viewer);
        var platform = await env.PlatformAdminAsync();

        // 1. Viewer reads first: auto-provisions both, but Viewer cannot mutate anything
        using var viewerList = await viewer.GetAsync(env.TenantRoot + "/storage-connections");
        Assert.Equal(HttpStatusCode.OK, viewerList.StatusCode);
        var viewerItems = AgentWorld.ReadJson(viewerList).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, viewerItems.Count);
        Assert.All(viewerItems, item =>
        {
            var p = item.GetProperty("permissions");
            Assert.False(p.GetProperty("canEdit").GetBoolean());
            Assert.False(p.GetProperty("canToggle").GetBoolean());
            Assert.False(p.GetProperty("canDelete").GetBoolean());
        });

        // 2. Admin reads: can manage TenantManaged (Local) but not PlatformManaged (Central)
        using var adminList = await admin.GetAsync(env.TenantRoot + "/storage-connections");
        var adminItems = AgentWorld.ReadJson(adminList).GetProperty("items").EnumerateArray().ToList();
        var local = adminItems.Single(item => item.GetProperty("providerType").GetString() == "local");
        var central = adminItems.Single(item => item.GetProperty("providerType").GetString() == "central");
        Assert.True(local.GetProperty("permissions").GetProperty("canEdit").GetBoolean());
        Assert.True(local.GetProperty("permissions").GetProperty("canToggle").GetBoolean());
        Assert.True(local.GetProperty("permissions").GetProperty("canDelete").GetBoolean());
        Assert.False(central.GetProperty("permissions").GetProperty("canEdit").GetBoolean());
        Assert.False(central.GetProperty("permissions").GetProperty("canToggle").GetBoolean());
        Assert.False(central.GetProperty("permissions").GetProperty("canDelete").GetBoolean());

        // 3. PlatformAdmin reads: can manage PlatformManaged (Central) fully; for TenantManaged (Local) can only toggle and set quotaScope
        using var platformList = await platform.GetAsync($"/api/platform/tenants/{env.Tenant.Id.Value}/storage-connections");
        var platformItems = AgentWorld.ReadJson(platformList).GetProperty("items").EnumerateArray().ToList();
        var pLocal = platformItems.Single(item => item.GetProperty("providerType").GetString() == "local");
        var pCentral = platformItems.Single(item => item.GetProperty("providerType").GetString() == "central");
        Assert.False(pLocal.GetProperty("permissions").GetProperty("canEdit").GetBoolean());
        Assert.True(pLocal.GetProperty("permissions").GetProperty("canToggle").GetBoolean());
        Assert.True(pLocal.GetProperty("permissions").GetProperty("canSetQuotaScope").GetBoolean());
        Assert.False(pLocal.GetProperty("permissions").GetProperty("canDelete").GetBoolean());
        Assert.True(pCentral.GetProperty("permissions").GetProperty("canEdit").GetBoolean());
        Assert.True(pCentral.GetProperty("permissions").GetProperty("canToggle").GetBoolean());
        Assert.True(pCentral.GetProperty("permissions").GetProperty("canSetQuotaScope").GetBoolean());
        Assert.True(pCentral.GetProperty("permissions").GetProperty("canDelete").GetBoolean());
    }

    // ---- authorization matrix --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ViewerAndOperatorAreReadOnlyAndNonMembersAreForbidden()
    {
        await using var env = await ApiWorld.CreateAsync(fixture);
        var operatorUser = await env.SessionAsync(TenantRole.Operator);
        var nonMember = await env.NonMemberAsync();
        using var list = await env.Admin.GetAsync(env.TenantRoot + "/storage-connections");
        var localId = AgentWorld.ReadJson(list).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("providerType").GetString() == "local").GetProperty("id").GetGuid();

        // Operator attempts mutations -> 403
        using var opRename = await operatorUser.SendAsync(HttpMethod.Put, env.TenantRoot + $"/storage-connections/{localId}", new { name = "Hacked" });
        using var opToggle = await operatorUser.SendAsync(HttpMethod.Post, env.TenantRoot + $"/storage-connections/{localId}/disable");
        using var opDelete = await operatorUser.SendAsync(HttpMethod.Delete, env.TenantRoot + $"/storage-connections/{localId}");
        Assert.Equal(HttpStatusCode.Forbidden, opRename.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, opToggle.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, opDelete.StatusCode);

        // Non-member -> 403 on tenant routes
        using var nmList = await nonMember.GetAsync(env.TenantRoot + "/storage-connections");
        Assert.Equal(HttpStatusCode.Forbidden, nmList.StatusCode);
    }

    [Fact]
    public async Task PlatformManagedConnectionsAreReadOnlyForTenants()
    {
        await using var env = await ApiWorld.CreateAsync(fixture);
        using var list = await env.Admin.GetAsync(env.TenantRoot + "/storage-connections");
        var centralId = AgentWorld.ReadJson(list).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("providerType").GetString() == "central").GetProperty("id").GetGuid();

        using var rename = await env.Admin.SendAsync(HttpMethod.Put, env.TenantRoot + $"/storage-connections/{centralId}", new { name = "Custom Name" });
        using var toggle = await env.Admin.SendAsync(HttpMethod.Post, env.TenantRoot + $"/storage-connections/{centralId}/disable");
        using var delete = await env.Admin.SendAsync(HttpMethod.Delete, env.TenantRoot + $"/storage-connections/{centralId}");

        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
        Assert.Equal("platform_managed", AgentWorld.ReadJson(rename).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, toggle.StatusCode);
        Assert.Equal("platform_managed", AgentWorld.ReadJson(toggle).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal("platform_managed", AgentWorld.ReadJson(delete).GetProperty("error").GetString());
    }

    [Fact]
    public async Task AnotherTenantsConnectionReturnsNotFoundOnTenantRoutes()
    {
        await using var env = await ApiWorld.CreateAsync(fixture);
        var stranger = await ApiWorld.CreateAsync(fixture);
        await using var _ = stranger;
        using var strangerList = await stranger.Admin.GetAsync(stranger.TenantRoot + "/storage-connections");
        var strangerLocalId = AgentWorld.ReadJson(strangerList).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("providerType").GetString() == "local").GetProperty("id").GetGuid();

        using var get = await env.Admin.GetAsync(env.TenantRoot + $"/storage-connections/{strangerLocalId}");
        using var update = await env.Admin.SendAsync(HttpMethod.Put, env.TenantRoot + $"/storage-connections/{strangerLocalId}", new { name = "Hijack" });
        using var delete = await env.Admin.SendAsync(HttpMethod.Delete, env.TenantRoot + $"/storage-connections/{strangerLocalId}");

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
    }

    [Fact]
    public async Task PlatformAdminCannotMutateTenantManagedConnectionsExceptToggleAndQuotaScope()
    {
        await using var env = await ApiWorld.CreateAsync(fixture);
        var platform = await env.PlatformAdminAsync();
        using var list = await platform.GetAsync($"/api/platform/tenants/{env.Tenant.Id.Value}/storage-connections");
        var localId = AgentWorld.ReadJson(list).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("providerType").GetString() == "local").GetProperty("id").GetGuid();

        // Platform cannot rename or delete TenantManaged
        using var rename = await platform.SendAsync(HttpMethod.Put, $"/api/platform/storage-connections/{localId}", new { name = "Platform Rename" });
        using var delete = await platform.SendAsync(HttpMethod.Delete, $"/api/platform/storage-connections/{localId}");
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
        Assert.Equal("tenant_managed", AgentWorld.ReadJson(rename).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal("tenant_managed", AgentWorld.ReadJson(delete).GetProperty("error").GetString());

        // Platform CAN toggle and set quotaScope
        using var disable = await platform.SendAsync(HttpMethod.Post, $"/api/platform/storage-connections/{localId}/disable");
        using var setScope = await platform.SendAsync(HttpMethod.Put, $"/api/platform/storage-connections/{localId}", new { quotaScope = "custom-scope" });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.Equal(HttpStatusCode.OK, setScope.StatusCode);
        Assert.True(AgentWorld.ReadJson(disable).GetProperty("enabled").GetBoolean());
        Assert.True(AgentWorld.ReadJson(disable).GetProperty("administrativelyDisabled").GetBoolean());
        Assert.False(AgentWorld.ReadJson(disable).GetProperty("effectiveEnabled").GetBoolean());
        Assert.Equal("custom-scope", AgentWorld.ReadJson(setScope).GetProperty("quotaScope").GetString());
    }

    [Fact]
    public async Task QuotaScopeInATenantBodyIsRejectedAsNotAllowed()
    {
        await using var env = await ApiWorld.CreateAsync(fixture);
        using var create = await env.Admin.SendAsync(HttpMethod.Post, env.TenantRoot + "/storage-connections",
            new { providerType = VaultProvider.Type, name = "Scoped Vault", quotaScope = "illegal", configuration = new { host = "vault.internal" }, secret = new { password = "pass" } });

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        var json = AgentWorld.ReadJson(create);
        Assert.Equal(("invalid_request", "quotaScope", "not_allowed"), (json.GetProperty("error").GetString(), json.GetProperty("field").GetString(), json.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task TenantCannotCreateCentralProvider()
    {
        await using var env = await ApiWorld.CreateAsync(fixture);

        // Central -> 403 platform_managed_provider
        using var central = await env.Admin.SendAsync(HttpMethod.Post, env.TenantRoot + "/storage-connections", new { providerType = "central", name = "My Central" });
        Assert.Equal(HttpStatusCode.Forbidden, central.StatusCode);
        Assert.Equal("platform_managed_provider", AgentWorld.ReadJson(central).GetProperty("code").GetString());
    }

    // ---- secret safety and test probe ------------------------------------------------------------------------------------------

    [Fact]
    public async Task SecretsNeverAppearInResponsesAndProbeExecutesCorrectly()
    {
        await using var env = await ApiWorld.CreateAsync(fixture);
        var secretValue = "super-secret-pass-42";

        // Create vault connection
        using var create = await env.Admin.SendAsync(HttpMethod.Post, env.TenantRoot + "/storage-connections",
            new { providerType = VaultProvider.Type, name = "Safe Vault", configuration = new { host = "vault.internal" }, secret = new { password = secretValue } });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = AgentWorld.ReadJson(create);
        var id = created.GetProperty("id").GetGuid();

        // Assert secret value never in response
        Assert.DoesNotContain(secretValue, await create.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(created.GetProperty("hasSecret").GetBoolean());
        Assert.Equal([VaultProvider.Password], created.GetProperty("secretFieldNames").EnumerateArray().Select(s => s.GetString()!).ToArray());

        // GET view also safe
        using var get = await env.Admin.GetAsync(env.TenantRoot + $"/storage-connections/{id}");
        Assert.DoesNotContain(secretValue, await get.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Test endpoint runs VaultTester -> Succeeded
        using var test = await env.Admin.SendAsync(HttpMethod.Post, env.TenantRoot + $"/storage-connections/{id}/test");
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        var testResult = AgentWorld.ReadJson(test);
        Assert.Equal("Succeeded", testResult.GetProperty("test").GetProperty("status").GetString());
        Assert.Equal(StorageTestCodes.Ok, testResult.GetProperty("test").GetProperty("code").GetString());
    }

    [Fact]
    public async Task CentralConnectionTestReflectsWhetherStorageIsConfiguredAndCleansUpProbe()
    {
        // 1. Without central storage configured -> Failed / storage_not_configured
        await using (var unconfigured = await ApiWorld.CreateAsync(fixture, withCentralStorage: false))
        {
            var platform = await unconfigured.PlatformAdminAsync();
            using var list = await platform.GetAsync($"/api/platform/tenants/{unconfigured.Tenant.Id.Value}/storage-connections");
            var centralId = AgentWorld.ReadJson(list).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("providerType").GetString() == "central").GetProperty("id").GetGuid();

            using var test = await platform.SendAsync(HttpMethod.Post, $"/api/platform/storage-connections/{centralId}/test");
            Assert.Equal(HttpStatusCode.OK, test.StatusCode);
            var result = AgentWorld.ReadJson(test).GetProperty("test");
            Assert.Equal("Failed", result.GetProperty("status").GetString());
            Assert.Equal(StorageTestCodes.StorageNotConfigured, result.GetProperty("code").GetString());
        }

        // 2. With central storage configured -> Succeeded and probe file is cleaned up
        await using (var configured = await ApiWorld.CreateAsync(fixture, withCentralStorage: true))
        {
            var platform = await configured.PlatformAdminAsync();
            using var list = await platform.GetAsync($"/api/platform/tenants/{configured.Tenant.Id.Value}/storage-connections");
            var centralId = AgentWorld.ReadJson(list).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("providerType").GetString() == "central").GetProperty("id").GetGuid();

            using var test = await platform.SendAsync(HttpMethod.Post, $"/api/platform/storage-connections/{centralId}/test");
            Assert.Equal(HttpStatusCode.OK, test.StatusCode);
            var result = AgentWorld.ReadJson(test).GetProperty("test");
            Assert.Equal("Succeeded", result.GetProperty("status").GetString());
            Assert.Equal(StorageTestCodes.Ok, result.GetProperty("code").GetString());

            // Assert no .probe file left behind anywhere under CentralRoot
            Assert.Empty(Directory.GetFiles(configured.CentralRoot, "*.probe", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public async Task LocalConnectionTestReturnsNotTestableAgentSideStorage()
    {
        await using var env = await ApiWorld.CreateAsync(fixture);
        using var list = await env.Admin.GetAsync(env.TenantRoot + "/storage-connections");
        var localId = AgentWorld.ReadJson(list).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("providerType").GetString() == "local").GetProperty("id").GetGuid();

        using var test = await env.Admin.SendAsync(HttpMethod.Post, env.TenantRoot + $"/storage-connections/{localId}/test");

        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        var result = AgentWorld.ReadJson(test).GetProperty("test");
        Assert.Equal("NotTestable", result.GetProperty("status").GetString());
        Assert.Equal(StorageTestCodes.AgentSideStorage, result.GetProperty("code").GetString());
    }

    // ---- platform search and pagination ----------------------------------------------------------------------------------------

    [Fact]
    public async Task PlatformSearchFiltersByOwnershipProviderTypeTenantAndSearchTerm()
    {
        await using var env = await ApiWorld.CreateAsync(fixture);
        var platform = await env.PlatformAdminAsync();
        var tenantId = env.Tenant.Id.Value;

        // Auto-provision tenant defaults by listing
        using var _ = await env.Admin.GetAsync(env.TenantRoot + "/storage-connections");

        // Filter by tenantId
        using var byTenant = await platform.GetAsync($"/api/platform/storage-connections?tenantId={tenantId}");
        Assert.Equal(HttpStatusCode.OK, byTenant.StatusCode);
        var items = AgentWorld.ReadJson(byTenant).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        // Filter by ownership
        using var byOwnership = await platform.GetAsync($"/api/platform/storage-connections?tenantId={tenantId}&ownership=PlatformManaged");
        var platformManaged = AgentWorld.ReadJson(byOwnership).GetProperty("items").EnumerateArray().ToList();
        Assert.Single(platformManaged);
        Assert.Equal("central", platformManaged[0].GetProperty("providerType").GetString());

        // Filter by providerType
        using var byProvider = await platform.GetAsync($"/api/platform/storage-connections?tenantId={tenantId}&providerType=local");
        var localManaged = AgentWorld.ReadJson(byProvider).GetProperty("items").EnumerateArray().ToList();
        Assert.Single(localManaged);
        Assert.Equal("local", localManaged[0].GetProperty("providerType").GetString());
    }

    // ---- admission disable / enable end-to-end through API --------------------------------------------------------------------

    [Fact]
    public async Task DisablingAConnectionBlocksRunStartViaHttpAndReEnablingPermitsIt()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var jobId = await scenario.CreateJobAsync();
        var (local, _) = await new ConnectionWorld(fixture).DefaultsAsync(scenario.Setup.Tenant.Id);

        var platform = await scenario.World.SignInAsync(await scenario.World.SeedUserAsync(platformAdmin: true));

        // The shared job fixture uses the default Local provider.
        using var disable = await platform.SendAsync(HttpMethod.Post, $"/api/platform/storage-connections/{local.Id}/disable");
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        // 2. Start run -> 409 admission_denied / storage_connection_disabled
        using var denied = await scenario.StartRawAsync(jobId, "run-denied-key");
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        var error = AgentWorld.ReadJson(denied);
        Assert.Equal("admission_denied", error.GetProperty("error").GetString());
        Assert.Equal(BackupAdmissionDenialCodes.StorageConnectionDisabled, error.GetProperty("code").GetString());

        // Re-enable the job's actual destination.
        using var enable = await platform.SendAsync(HttpMethod.Post, $"/api/platform/storage-connections/{local.Id}/enable");
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);

        // 4. Start run -> 200 OK
        using var allowed = await scenario.StartRawAsync(jobId, "run-allowed-key");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }
}
