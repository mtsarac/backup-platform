using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.ManagedService.Administration;
using BackupPlatform.ManagedService.Plans;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.ManagedService.ManagedServiceTestSupport;

namespace BackupPlatform.IntegrationTests.ManagedService;

public sealed class PlanAdministrationIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    public async Task TenantOwnersAndAdminsCannotReachAnyManagedServiceRoute(TenantRole role)
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var platform = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var planId = await CreatePlanAsync(platform);
        var setup = await world.CreateTenantAsync(role);
        var tenant = setup.Tenant.Id.Value;
        var plansBefore = (await CallAsync(platform, HttpMethod.Get, "/api/platform/plans?pageSize=100")).Json.GetProperty("totalCount").GetInt32();
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, "/api/platform/plans", null),
            (HttpMethod.Post, "/api/platform/plans", new { name = "Nope", storageLimitBytes = Gb }),
            (HttpMethod.Put, $"/api/platform/plans/{planId}", new { name = "Nope", storageLimitBytes = Gb }),
            (HttpMethod.Put, $"/api/platform/plans/{planId}/enabled", new { enabled = false }),
            (HttpMethod.Get, SubscriptionPath(tenant), null),
            (HttpMethod.Get, SubscriptionPath(tenant, "s"), null),
            (HttpMethod.Put, SubscriptionPath(tenant), new { planId }),
            (HttpMethod.Post, SubscriptionPath(tenant, "/suspend"), null),
            (HttpMethod.Post, SubscriptionPath(tenant, "/resume"), null),
            (HttpMethod.Put, SubscriptionPath(tenant, "/storage-limit-override"), new { kind = "unlimited" })
        })
        {
            var reply = await CallAsync(setup.Admin, method, path, body);
            Assert.True(reply.Status == HttpStatusCode.Forbidden, $"{method} {path} returned {reply.Status}.");
        }
        // Rejected calls changed nothing and recorded nothing for the caller.
        Assert.Equal(plansBefore, (await CallAsync(platform, HttpMethod.Get, "/api/platform/plans?pageSize=100")).Json.GetProperty("totalCount").GetInt32());
        Assert.Empty(await RowsOfAsync(fixture, setup.Tenant.Id));
        await using var context = fixture.CreatePlatformContext();
        Assert.False(await context.AuditEvents.AnyAsync(row => row.ActorUserId == setup.Account.UserId));
    }

    [Fact]
    public async Task AnonymousCallersAreUnauthorized()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        foreach (var path in new[] { "/api/platform/plans", SubscriptionPath(Guid.NewGuid()), SubscriptionPath(Guid.NewGuid(), "s") })
        {
            using var response = await world.Anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task PlatformAdminNeedsNoTenantMembership()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var tenant = await world.AddTenantAsync();
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Get, "/api/platform/plans")).Status);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Get, SubscriptionPath(tenant.Id.Value))).Status);
    }

    [Fact]
    public async Task TheCommercialPlansAndTheLegacyPlanAreSeededAsDataWithDecimalSizes()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var list = await CallAsync(admin, HttpMethod.Get, "/api/platform/plans?pageSize=100");
        Assert.Equal(HttpStatusCode.OK, list.Status);
        var items = list.Json.GetProperty("items").EnumerateArray().ToList();

        var seeded = items.Where(item => !item.GetProperty("isSystem").GetBoolean() && item.GetProperty("storageLimitBytes").ValueKind == JsonValueKind.Number)
            .Select(item => (Name: item.GetProperty("name").GetString()!, Bytes: item.GetProperty("storageLimitBytes").GetInt64()))
            .Where(plan => new[] { "1 GB", "5 GB", "10 GB", "20 GB" }.Contains(plan.Name)).OrderBy(plan => plan.Bytes).ToList();
        Assert.Equal([("1 GB", 1_000_000_000L), ("5 GB", 5_000_000_000L), ("10 GB", 10_000_000_000L), ("20 GB", 20_000_000_000L)], seeded);

        var legacy = Assert.Single(items, item => item.GetProperty("isSystem").GetBoolean());
        Assert.Equal("Legacy Unlimited", legacy.GetProperty("name").GetString());
        Assert.Equal("legacy_unlimited", legacy.GetProperty("systemKey").GetString());
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("storageLimitBytes").ValueKind);
        Assert.True(legacy.GetProperty("enabled").GetBoolean());
        Assert.Equal(1, legacy.GetProperty("policyConfigurationVersion").GetInt32());

        // System plans sort after the sellable ones; sellable plans by size.
        var firstSystem = items.FindIndex(item => item.GetProperty("isSystem").GetBoolean());
        Assert.All(items.Skip(firstSystem), item => Assert.True(item.GetProperty("isSystem").GetBoolean()));
    }

    [Fact]
    public async Task PlansAreCreatedEditedAndToggledWithValidationAndAudit()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync(platformAdmin: true);
        var admin = await world.SignInAsync(account);
        var name = UniqueName("Custom");

        // A custom 50 GB plan and an unlimited plan are plain new rows.
        var created = await CallAsync(admin, HttpMethod.Post, "/api/platform/plans", new { name = $"  {name.ToUpperInvariant()}  ", storageLimitBytes = 50 * Gb });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var planId = created.Json.GetProperty("planId").GetGuid();
        Assert.Equal(50_000_000_000L, created.Json.GetProperty("storageLimitBytes").GetInt64());
        Assert.True(created.Json.GetProperty("enabled").GetBoolean());
        Assert.False(created.Json.GetProperty("isSystem").GetBoolean());
        Assert.Equal(0, created.Json.GetProperty("tenantCount").GetInt32());
        var unlimited = await CallAsync(admin, HttpMethod.Post, "/api/platform/plans", new { name = UniqueName("Unlimited"), unlimited = true, enabled = false });
        Assert.Equal(HttpStatusCode.Created, unlimited.Status);
        Assert.Equal(JsonValueKind.Null, unlimited.Json.GetProperty("storageLimitBytes").ValueKind);
        Assert.False(unlimited.Json.GetProperty("enabled").GetBoolean());

        foreach (var (body, field, code) in new (object, string, string)[]
        {
            (new { name = "x" }, "storageLimit", "required"),
            (new { name = "x", storageLimitBytes = Gb, unlimited = true }, "storageLimit", "ambiguous"),
            (new { name = "x", storageLimitBytes = 0L }, "storageLimit", "out_of_range"),
            (new { name = "x", storageLimitBytes = -Gb }, "storageLimit", "out_of_range"),
            (new { name = "x", storageLimitBytes = long.MaxValue }, "storageLimit", "out_of_range"),
            (new { name = "  ", storageLimitBytes = Gb }, "name", "required"),
            (new { name = new string('n', 101), storageLimitBytes = Gb }, "name", "too_long"),
            (new { name = "x", storageLimitBytes = Gb, quotaPolicyReference = "Not A Key" }, "quotaPolicyReference", "invalid_value")
        })
        {
            var rejected = await CallAsync(admin, HttpMethod.Post, "/api/platform/plans", body);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.Status);
            Assert.Equal((field, code), (rejected.Json.GetProperty("field").GetString(), rejected.Json.GetProperty("code").GetString()));
        }
        // Case and whitespace do not make a different plan.
        var duplicate = await CallAsync(admin, HttpMethod.Post, "/api/platform/plans", new { name = $" {name.ToLowerInvariant()} ", storageLimitBytes = Gb });
        Assert.Equal((HttpStatusCode.Conflict, "plan_name_taken"), (duplicate.Status, duplicate.Error));

        // A plan may only name a quota policy that exists.
        var unknownPolicy = await CallAsync(admin, HttpMethod.Put, $"/api/platform/plans/{planId}", new { name, storageLimitBytes = 50 * Gb, quotaPolicyReference = "std-1" });
        Assert.Equal((HttpStatusCode.NotFound, "quota_policy_not_found"), (unknownPolicy.Status, unknownPolicy.Error));
        var policy = await CallAsync(admin, HttpMethod.Post, "/api/platform/quota-policies", new
        {
            key = "std-1", name = UniqueName("Policy"), accountingMode = "LogicalArtifactBytes", enforcementMode = "BlockNewBackups",
            includedStorageScopes = new[] { new { kind = "all" } }, warningThresholdPercent = 80, criticalThresholdPercent = 90
        });
        Assert.Equal(HttpStatusCode.Created, policy.Status);

        world.Clock.Advance(TimeSpan.FromSeconds(1));
        var edited = await CallAsync(admin, HttpMethod.Put, $"/api/platform/plans/{planId}", new { name = name + " v2", storageLimitBytes = 60 * Gb, quotaPolicyReference = "std-1" });
        Assert.Equal(HttpStatusCode.OK, edited.Status);
        Assert.Equal((60_000_000_000L, "std-1"), (edited.Json.GetProperty("storageLimitBytes").GetInt64(), edited.Json.GetProperty("quotaPolicyReference").GetString()));
        // Identical values: success, nothing written, nothing audited.
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Put, $"/api/platform/plans/{planId}", new { name = name + " v2", storageLimitBytes = 60 * Gb, quotaPolicyReference = "std-1" })).Status);
        var clash = await CallAsync(admin, HttpMethod.Put, $"/api/platform/plans/{planId}", new { name = "5 GB", storageLimitBytes = Gb });
        Assert.Equal((HttpStatusCode.Conflict, "plan_name_taken"), (clash.Status, clash.Error));
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(admin, HttpMethod.Put, $"/api/platform/plans/{Guid.NewGuid()}", new { name = "x", storageLimitBytes = Gb })).Status);
        Assert.Equal("storageLimit", (await CallAsync(admin, HttpMethod.Put, $"/api/platform/plans/{planId}", new { name = "x" })).Json.GetProperty("field").GetString());

        world.Clock.Advance(TimeSpan.FromSeconds(1));
        var disabled = await CallAsync(admin, HttpMethod.Put, $"/api/platform/plans/{planId}/enabled", new { enabled = false });
        Assert.False(disabled.Json.GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Put, $"/api/platform/plans/{planId}/enabled", new { enabled = false })).Status);
        world.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await CallAsync(admin, HttpMethod.Put, $"/api/platform/plans/{planId}/enabled", new { enabled = true })).Json.GetProperty("enabled").GetBoolean());
        Assert.Equal("required", (await CallAsync(admin, HttpMethod.Put, $"/api/platform/plans/{planId}/enabled", new { })).Json.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(admin, HttpMethod.Put, $"/api/platform/plans/{Guid.NewGuid()}/enabled", new { enabled = true })).Status);

        var enabledFilter = await CallAsync(admin, HttpMethod.Get, $"/api/platform/plans?enabled=false&search={Uri.EscapeDataString(name[..12])}&pageSize=100");
        Assert.DoesNotContain(enabledFilter.Json.GetProperty("items").EnumerateArray(), item => item.GetProperty("planId").GetGuid() == planId);

        var audit = await AuditOfTargetAsync(fixture, planId);
        Assert.Equal(
            [ManagedServiceAuditActions.PlanCreated, ManagedServiceAuditActions.PlanChanged, ManagedServiceAuditActions.PlanDisabled, ManagedServiceAuditActions.PlanEnabled],
            audit.Select(row => row.Action).ToArray());
        Assert.All(audit, row =>
        {
            Assert.Equal(AuditActorKind.PlatformAdmin, row.ActorKind);
            Assert.Equal(account.UserId, row.ActorUserId);
            Assert.Null(row.TenantId);
            Assert.Equal("plan", row.TargetType);
        });
        using var changed = JsonDocument.Parse(audit[1].DetailsJson);
        Assert.Equal(50_000_000_000L, changed.RootElement.GetProperty("previousStorageLimitBytes").GetInt64());
        Assert.Equal(60_000_000_000L, changed.RootElement.GetProperty("storageLimitBytes").GetInt64());
        Assert.Equal("std-1", changed.RootElement.GetProperty("quotaPolicyReference").GetString());
        Assert.Equal(name.ToUpperInvariant(), changed.RootElement.GetProperty("previousName").GetString());
    }

    [Fact]
    public async Task SystemPlansAreReadOnly()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var legacy = (await CallAsync(admin, HttpMethod.Get, "/api/platform/plans?pageSize=100")).Json.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("isSystem").GetBoolean()).GetProperty("planId").GetGuid();
        var rename = await CallAsync(admin, HttpMethod.Put, $"/api/platform/plans/{legacy}", new { name = "Renamed", unlimited = true });
        Assert.Equal((HttpStatusCode.Conflict, "system_plan_immutable"), (rename.Status, rename.Error));
        var disable = await CallAsync(admin, HttpMethod.Put, $"/api/platform/plans/{legacy}/enabled", new { enabled = false });
        Assert.Equal((HttpStatusCode.Conflict, "system_plan_immutable"), (disable.Status, disable.Error));
        Assert.Empty(await AuditOfTargetAsync(fixture, legacy));
    }

    [Fact]
    public async Task PlanRowsCannotBeBrokenAtTheDatabaseLevelEither()
    {
        await using var context = fixture.CreatePlatformContext();
        foreach (var statement in new[]
        {
            "INSERT INTO plans (\"Id\",\"Name\",\"NormalizedName\",\"Enabled\",\"StorageLimitBytes\",\"PolicyConfigurationVersion\",\"IsSystem\",\"CreatedAt\",\"UpdatedAt\") VALUES (gen_random_uuid(),'zero','zero-limit',TRUE,0,1,FALSE,now(),now())",
            "INSERT INTO plans (\"Id\",\"Name\",\"NormalizedName\",\"Enabled\",\"StorageLimitBytes\",\"PolicyConfigurationVersion\",\"IsSystem\",\"CreatedAt\",\"UpdatedAt\") VALUES (gen_random_uuid(),'big','too-big',TRUE,1000000000000001,1,FALSE,now(),now())",
            "INSERT INTO plans (\"Id\",\"Name\",\"NormalizedName\",\"Enabled\",\"PolicyConfigurationVersion\",\"IsSystem\",\"SystemKey\",\"CreatedAt\",\"UpdatedAt\") VALUES (gen_random_uuid(),'k','key-on-normal',TRUE,1,FALSE,'legacy',now(),now())",
            "INSERT INTO plans (\"Id\",\"Name\",\"NormalizedName\",\"Enabled\",\"PolicyConfigurationVersion\",\"IsSystem\",\"SystemKey\",\"CreatedAt\",\"UpdatedAt\") VALUES (gen_random_uuid(),'dup','duplicate-key',TRUE,1,TRUE,'legacy_unlimited',now(),now())"
        })
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => context.Database.ExecuteSqlRawAsync(statement));
            Assert.IsType<Npgsql.PostgresException>(failure is Npgsql.PostgresException ? failure : failure.InnerException);
        }
    }
}
