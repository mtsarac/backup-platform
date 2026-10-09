using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ApiWorld = BackupPlatform.IntegrationTests.Storage.Connections.StorageConnectionApiIntegrationTests.ApiWorld;

namespace BackupPlatform.IntegrationTests.Storage.Connections;

public sealed class StorageConnectionPlatformLockIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private readonly ConnectionWorld _world = new(fixture);

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    public async Task OwnerPreferenceCannotClearAPlatformLockOrDeleteTheConnectionToBypassIt(TenantRole role)
    {
        await using var env = await ApiWorld.CreateAsync(fixture);
        var tenant = role == TenantRole.Admin ? env.Admin : await env.SessionAsync(role);
        var platform = await env.PlatformAdminAsync();
        var localId = await LocalIdAsync(env);
        var path = env.TenantRoot + $"/storage-connections/{localId}";
        var platformPath = $"/api/platform/storage-connections/{localId}";

        AssertState(await ToggleAsync(platform, platformPath, "disable"), true, true, false);
        AssertState(await ToggleAsync(platform, platformPath, "disable"), true, true, false);
        var locked = await GetAsync(tenant, path);
        Assert.True(locked.GetProperty("permissions").GetProperty("canToggle").GetBoolean());
        Assert.False(locked.GetProperty("permissions").GetProperty("canDelete").GetBoolean());
        using (var enable = await tenant.SendAsync(HttpMethod.Post, path + "/enable")) AssertLocked(enable);
        using (var delete = await tenant.SendAsync(HttpMethod.Delete, path)) AssertLocked(delete);
        using (var update = await tenant.SendAsync(HttpMethod.Put, path,
            new { name = "Owner renamed Local", enabled = true, administrativelyDisabled = false, effectiveEnabled = true }))
        {
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            AssertState(AgentWorld.ReadJson(update), true, true, false);
        }
        AssertState(await ToggleAsync(tenant, path, "disable"), false, true, false);
        using (var enable = await tenant.SendAsync(HttpMethod.Post, path + "/enable")) AssertLocked(enable);
        AssertState(await GetAsync(tenant, path), false, true, false);

        AssertState(await ToggleAsync(platform, platformPath, "enable"), false, false, false);
        AssertState(await ToggleAsync(platform, platformPath, "enable"), false, false, false);
        var unlocked = await GetAsync(tenant, path);
        Assert.True(unlocked.GetProperty("permissions").GetProperty("canDelete").GetBoolean());
        AssertState(await ToggleAsync(tenant, path, "enable"), true, false, true);
        var row = await _world.RowAsync(localId);
        Assert.Equal((true, false, true), (row.Enabled, row.AdministrativelyDisabled, row.EffectiveEnabled));
        await AssertToggleAuditsAsync(env, localId);
    }

    [Theory]
    [InlineData(TenantRole.Viewer)]
    [InlineData(TenantRole.Operator)]
    public async Task ReadOnlyMembersCannotChangeEitherControlOrDeleteALockedConnection(TenantRole role)
    {
        await using var env = await ApiWorld.CreateAsync(fixture);
        var reader = await env.SessionAsync(role);
        var platform = await env.PlatformAdminAsync();
        var localId = await LocalIdAsync(env);
        var path = env.TenantRoot + $"/storage-connections/{localId}";
        var platformPath = $"/api/platform/storage-connections/{localId}";
        await ToggleAsync(platform, platformPath, "disable");
        var view = await GetAsync(reader, path);
        AssertState(view, true, true, false);
        Assert.False(view.GetProperty("permissions").GetProperty("canToggle").GetBoolean());
        Assert.False(view.GetProperty("permissions").GetProperty("canDelete").GetBoolean());
        foreach (var action in new[] { "enable", "disable" })
        {
            using var tenantToggle = await reader.SendAsync(HttpMethod.Post, path + "/" + action);
            using var platformToggle = await reader.SendAsync(HttpMethod.Post, platformPath + "/" + action);
            Assert.Equal(HttpStatusCode.Forbidden, tenantToggle.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, platformToggle.StatusCode);
        }
        using var delete = await reader.SendAsync(HttpMethod.Delete, path);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        AssertState(await GetAsync(env.Admin, path), true, true, false);
        Assert.Single(await ToggleAuditsAsync(env, localId));
    }

    [Fact]
    public async Task AnotherTenantCannotReadChangeOrDeleteALockedConnection()
    {
        await using var env = await ApiWorld.CreateAsync(fixture);
        await using var other = await ApiWorld.CreateAsync(fixture);
        var platform = await env.PlatformAdminAsync();
        var localId = await LocalIdAsync(env);
        await ToggleAsync(platform, $"/api/platform/storage-connections/{localId}", "disable");
        var ownPath = other.TenantRoot + $"/storage-connections/{localId}";
        using var read = await other.Admin.GetAsync(ownPath);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        foreach (var action in new[] { "enable", "disable" })
        {
            using var ownRoute = await other.Admin.SendAsync(HttpMethod.Post, ownPath + "/" + action);
            using var victimRoute = await other.Admin.SendAsync(HttpMethod.Post, env.TenantRoot + $"/storage-connections/{localId}/{action}");
            using var platformRoute = await other.Admin.SendAsync(HttpMethod.Post, $"/api/platform/storage-connections/{localId}/{action}");
            Assert.Equal(HttpStatusCode.NotFound, ownRoute.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, victimRoute.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, platformRoute.StatusCode);
        }
        using var delete = await other.Admin.SendAsync(HttpMethod.Delete, ownPath);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        AssertState(await GetAsync(env.Admin, env.TenantRoot + $"/storage-connections/{localId}"), true, true, false);
        Assert.Single(await ToggleAuditsAsync(env, localId));
    }

    [Theory]
    [InlineData("enable")]
    [InlineData("delete")]
    public async Task PlatformLockWinsAgainstATenantMutationThatPrefetchedTheUnlockedConnection(string action)
    {
        using var probe = new ConnectionLockProbe(_world.Store());
        await using var env = await ApiWorld.CreateAsync(fixture, configureServices: services =>
        {
            services.RemoveAll<IStorageConnectionStore>();
            services.AddSingleton<IStorageConnectionStore>(probe);
        });
        var platform = await env.PlatformAdminAsync();
        var localId = await LocalIdAsync(env);
        var path = env.TenantRoot + $"/storage-connections/{localId}";
        probe.Arm(localId);
        var platformRequest = platform.SendAsync(HttpMethod.Post, $"/api/platform/storage-connections/{localId}/disable");
        Task<HttpResponseMessage>? tenantRequest = null;
        try
        {
            await probe.PlatformLocked.Task.WaitAsync(TimeSpan.FromSeconds(15));
            tenantRequest = action == "delete"
                ? env.Admin.SendAsync(HttpMethod.Delete, path)
                : env.Admin.SendAsync(HttpMethod.Post, path + "/enable");
            await probe.TenantMutating.Task.WaitAsync(TimeSpan.FromSeconds(15));
            probe.Release();
            var disabled = await platformRequest;
            var rejected = await tenantRequest;
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
            AssertLocked(rejected);
            AssertState(await GetAsync(env.Admin, path), true, true, false);
            Assert.Single(await ToggleAuditsAsync(env, localId));
        }
        finally
        {
            probe.Release();
            using var platformResponse = await platformRequest;
            if (tenantRequest is not null)
            {
                using var tenantResponse = await tenantRequest;
            }
        }
    }

    [Theory]
    [InlineData("local")]
    [InlineData("central")]
    public async Task PlatformTogglesPreserveOwnerPreferenceForBothOwnershipsAndSearchUsesEffectiveState(string providerType)
    {
        await using var env = await ApiWorld.CreateAsync(fixture);
        var platform = await env.PlatformAdminAsync();
        var (local, central) = await _world.DefaultsAsync(env.Tenant.Id);
        var connection = providerType == "local" ? local : central;
        var path = $"/api/platform/storage-connections/{connection.Id}";
        AssertState(await ToggleAsync(platform, path, "disable"), true, true, false);
        using (var enabled = await platform.GetAsync($"/api/platform/storage-connections?tenantId={env.Tenant.Id.Value}&enabled=true"))
        using (var disabled = await platform.GetAsync($"/api/platform/storage-connections?tenantId={env.Tenant.Id.Value}&enabled=false"))
        {
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
            Assert.DoesNotContain(AgentWorld.ReadJson(enabled).GetProperty("items").EnumerateArray(), item => item.GetProperty("id").GetGuid() == connection.Id);
            Assert.Equal(connection.Id, Assert.Single(AgentWorld.ReadJson(disabled).GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        }
        await _world.SetEnabledAsync(connection.Id, env.Tenant.Id, false);
        AssertState(await ToggleAsync(platform, path, "enable"), false, false, false);
    }

    private static async Task<Guid> LocalIdAsync(ApiWorld env)
    {
        var list = await GetAsync(env.Admin, env.TenantRoot + "/storage-connections");
        return list.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("providerType").GetString() == "local").GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> GetAsync(BrowserSession session, string path)
    {
        using var response = await session.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    private static async Task<JsonElement> ToggleAsync(BrowserSession session, string path, string action)
    {
        using var response = await session.SendAsync(HttpMethod.Post, path + "/" + action);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return AgentWorld.ReadJson(response);
    }

    private static void AssertLocked(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("administratively_disabled", AgentWorld.ReadJson(response).GetProperty("error").GetString());
    }

    internal static void AssertState(JsonElement view, bool enabled, bool administrativelyDisabled, bool effectiveEnabled) =>
        Assert.Equal((enabled, administrativelyDisabled, effectiveEnabled),
            (view.GetProperty("enabled").GetBoolean(), view.GetProperty("administrativelyDisabled").GetBoolean(), view.GetProperty("effectiveEnabled").GetBoolean()));

    private async Task<List<AuditEvent>> ToggleAuditsAsync(ApiWorld env, Guid localId) =>
        (await _world.AuditOfTenantAsync(env.Tenant.Id)).Where(audit => audit.TargetId == localId.ToString("D")
            && audit.Action is StorageConnectionAuditActions.Enabled or StorageConnectionAuditActions.Disabled).ToList();

    private async Task AssertToggleAuditsAsync(ApiWorld env, Guid localId)
    {
        var audits = await ToggleAuditsAsync(env, localId);
        Assert.Equal(4, audits.Count);
        AssertAudit(audits, StorageConnectionAuditActions.Disabled, AuditActorKind.PlatformAdmin, "platform", (true, true, false));
        AssertAudit(audits, StorageConnectionAuditActions.Disabled, AuditActorKind.TenantAdministrator, "owner", (false, true, false));
        AssertAudit(audits, StorageConnectionAuditActions.Enabled, AuditActorKind.PlatformAdmin, "platform", (false, false, false));
        AssertAudit(audits, StorageConnectionAuditActions.Enabled, AuditActorKind.TenantAdministrator, "owner", (true, false, true));
    }

    private static void AssertAudit(List<AuditEvent> audits, string action, AuditActorKind kind, string control, (bool Enabled, bool Locked, bool Effective) state)
    {
        var audit = Assert.Single(audits, audit => audit.Action == action && audit.ActorKind == kind);
        using var details = JsonDocument.Parse(audit.DetailsJson);
        Assert.Equal(control, details.RootElement.GetProperty("control").GetString());
        AssertState(details.RootElement, state.Enabled, state.Locked, state.Effective);
    }
}
