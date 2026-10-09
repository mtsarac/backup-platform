using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Audit;
using BackupPlatform.ManagedService.Administration;
using BackupPlatform.ManagedService.Subscriptions;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.ManagedService.ManagedServiceTestSupport;

namespace BackupPlatform.IntegrationTests.ManagedService;

public sealed class SubscriptionAdministrationIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static JsonElement Sub(ApiReply reply) => reply.Json.GetProperty("subscription");

    [Fact]
    public async Task ANewTenantExistsWithoutASubscriptionAndOneCanBeAssignedAfterwards()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync(platformAdmin: true);
        var admin = await world.SignInAsync(account);

        var created = await CallAsync(admin, HttpMethod.Post, "/api/platform/tenants", new { name = UniqueName("Fresh") });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var tenantId = created.Json.GetProperty("tenantId").GetGuid();
        var none = await CallAsync(admin, HttpMethod.Get, SubscriptionPath(tenantId));
        Assert.Equal(HttpStatusCode.OK, none.Status);
        Assert.Equal(tenantId, none.Json.GetProperty("tenantId").GetGuid());
        Assert.Equal(JsonValueKind.Null, none.Json.GetProperty("subscription").ValueKind);
        Assert.Equal(0, (await CallAsync(admin, HttpMethod.Get, SubscriptionPath(tenantId, "s"))).Json.GetProperty("totalCount").GetInt32());

        var planId = await CreatePlanAsync(admin);
        var assigned = await CallAsync(admin, HttpMethod.Put, SubscriptionPath(tenantId), new { planId });
        Assert.Equal(HttpStatusCode.OK, assigned.Status);
        Assert.Equal("assigned", assigned.Json.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, assigned.Json.GetProperty("previousSubscriptionId").ValueKind);
        var subscription = Sub(assigned);
        Assert.Equal(("Active", planId, account.UserId.Value), (subscription.GetProperty("status").GetString(), subscription.GetProperty("planId").GetGuid(), subscription.GetProperty("assignedByUserId").GetGuid()));
        Assert.Equal("none", subscription.GetProperty("storageLimitOverride").GetProperty("kind").GetString());
        var effective = subscription.GetProperty("effectiveStorageLimit");
        Assert.Equal((false, 5_000_000_000L, "plan"), (effective.GetProperty("unlimited").GetBoolean(), effective.GetProperty("bytes").GetInt64(), effective.GetProperty("source").GetString()));
        Assert.Equal(1, await OpenRowCountAsync(fixture, new(tenantId)));
    }

    [Fact]
    public async Task ChangingPlansKeepsHistoryAndLeavesExactlyOneActiveSubscription()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync(platformAdmin: true);
        var admin = await world.SignInAsync(account);
        var tenant = await world.AddTenantAsync();
        var five = await CreatePlanAsync(admin, UniqueName("Five"), 5 * Gb);
        var ten = await CreatePlanAsync(admin, UniqueName("Ten"), 10 * Gb);
        var disabled = await CreatePlanAsync(admin, UniqueName("Off"), 20 * Gb, enabled: false);
        var path = SubscriptionPath(tenant.Id.Value);

        var first = await CallAsync(admin, HttpMethod.Put, path, new { planId = five });
        var firstId = Sub(first).GetProperty("subscriptionId").GetGuid();
        world.Clock.Advance(TimeSpan.FromSeconds(5));
        var changed = await CallAsync(admin, HttpMethod.Put, path, new { planId = ten });
        Assert.Equal(HttpStatusCode.OK, changed.Status);
        Assert.Equal("changed", changed.Json.GetProperty("outcome").GetString());
        Assert.Equal(firstId, changed.Json.GetProperty("previousSubscriptionId").GetGuid());
        Assert.Equal(10_000_000_000L, Sub(changed).GetProperty("effectiveStorageLimit").GetProperty("bytes").GetInt64());

        var history = (await CallAsync(admin, HttpMethod.Get, SubscriptionPath(tenant.Id.Value, "s"))).Json;
        Assert.Equal(2, history.GetProperty("totalCount").GetInt32());
        var rows = history.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(("Active", ten), (rows[0].GetProperty("status").GetString(), rows[0].GetProperty("planId").GetGuid()));
        Assert.Equal(("Superseded", five, firstId), (rows[1].GetProperty("status").GetString(), rows[1].GetProperty("planId").GetGuid(), rows[1].GetProperty("subscriptionId").GetGuid()));
        Assert.Equal(5_000_000_000L, rows[1].GetProperty("planStorageLimitBytes").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, rows[1].GetProperty("endsAt").ValueKind);
        var current = (await CallAsync(admin, HttpMethod.Get, path)).Json.GetProperty("subscription");
        Assert.Equal(ten, current.GetProperty("planId").GetGuid());

        // The superseded row was closed, never rewritten to another plan, and never reopened.
        var db = await RowsOfAsync(fixture, tenant.Id);
        Assert.Single(db, row => row.Status == SubscriptionStatus.Active);
        Assert.Equal(five, db.Single(row => row.Id.Value == firstId).PlanId.Value);
        Assert.Equal(1, db.Count(row => row.IsOpen));

        var same = await CallAsync(admin, HttpMethod.Put, path, new { planId = ten });
        Assert.Equal((HttpStatusCode.Conflict, "plan_already_assigned"), (same.Status, same.Error));
        var off = await CallAsync(admin, HttpMethod.Put, path, new { planId = disabled });
        Assert.Equal((HttpStatusCode.Conflict, "plan_disabled"), (off.Status, off.Error));
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(admin, HttpMethod.Put, path, new { planId = Guid.NewGuid() })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(admin, HttpMethod.Put, SubscriptionPath(Guid.NewGuid()), new { planId = five })).Status);
        Assert.Equal("planId", (await CallAsync(admin, HttpMethod.Put, path, new { })).Json.GetProperty("field").GetString());
        Assert.Equal(2, (await RowsOfAsync(fixture, tenant.Id)).Count);
    }

    [Fact]
    public async Task DisablingAPlanNeverMovesOrBreaksItsSubscribers()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var tenant = await world.AddTenantAsync();
        var plan = await CreatePlanAsync(admin);
        await CallAsync(admin, HttpMethod.Put, SubscriptionPath(tenant.Id.Value), new { planId = plan });

        var disabled = await CallAsync(admin, HttpMethod.Put, $"/api/platform/plans/{plan}/enabled", new { enabled = false });
        Assert.Equal(1, disabled.Json.GetProperty("tenantCount").GetInt32());
        var current = (await CallAsync(admin, HttpMethod.Get, SubscriptionPath(tenant.Id.Value))).Json.GetProperty("subscription");
        Assert.Equal(("Active", plan), (current.GetProperty("status").GetString(), current.GetProperty("planId").GetGuid()));
        Assert.Equal(1, await OpenRowCountAsync(fixture, tenant.Id));
        // Existing subscribers keep suspend/resume/override; only new assignments to the disabled plan are refused.
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Post, SubscriptionPath(tenant.Id.Value, "/suspend"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Post, SubscriptionPath(tenant.Id.Value, "/resume"))).Status);
    }

    [Fact]
    public async Task TheStorageLimitOverrideWinsOverThePlanAndIsNotInheritedByAPlanChange()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var tenant = await world.AddTenantAsync();
        var five = await CreatePlanAsync(admin, bytes: 5 * Gb);
        var unlimitedPlan = await CreatePlanAsync(admin, bytes: null);
        var path = SubscriptionPath(tenant.Id.Value);

        var assigned = await CallAsync(admin, HttpMethod.Put, path, new { planId = five, storageLimitOverride = new { kind = "limited", bytes = 20 * Gb } });
        var overridden = Sub(assigned);
        Assert.Equal(("limited", 20_000_000_000L), (overridden.GetProperty("storageLimitOverride").GetProperty("kind").GetString(), overridden.GetProperty("storageLimitOverride").GetProperty("bytes").GetInt64()));
        Assert.Equal((false, 20_000_000_000L, "override"), (overridden.GetProperty("effectiveStorageLimit").GetProperty("unlimited").GetBoolean(),
            overridden.GetProperty("effectiveStorageLimit").GetProperty("bytes").GetInt64(), overridden.GetProperty("effectiveStorageLimit").GetProperty("source").GetString()));
        Assert.Equal(5_000_000_000L, overridden.GetProperty("planStorageLimitBytes").GetInt64());

        // Typed unlimited override, distinct from "no override".
        var unlimitedOverride = await CallAsync(admin, HttpMethod.Put, SubscriptionPath(tenant.Id.Value, "/storage-limit-override"), new { kind = "unlimited" });
        Assert.Equal(HttpStatusCode.OK, unlimitedOverride.Status);
        Assert.Equal((true, JsonValueKind.Null, "override"), (unlimitedOverride.Json.GetProperty("effectiveStorageLimit").GetProperty("unlimited").GetBoolean(),
            unlimitedOverride.Json.GetProperty("effectiveStorageLimit").GetProperty("bytes").ValueKind, unlimitedOverride.Json.GetProperty("effectiveStorageLimit").GetProperty("source").GetString()));
        var cleared = await CallAsync(admin, HttpMethod.Put, SubscriptionPath(tenant.Id.Value, "/storage-limit-override"), new { kind = "none" });
        Assert.Equal((false, 5_000_000_000L, "plan"), (cleared.Json.GetProperty("effectiveStorageLimit").GetProperty("unlimited").GetBoolean(),
            cleared.Json.GetProperty("effectiveStorageLimit").GetProperty("bytes").GetInt64(), cleared.Json.GetProperty("effectiveStorageLimit").GetProperty("source").GetString()));
        // Limited override on an unlimited plan narrows it.
        await CallAsync(admin, HttpMethod.Put, SubscriptionPath(tenant.Id.Value, "/storage-limit-override"), new { kind = "limited", bytes = 3 * Gb });
        var change = await CallAsync(admin, HttpMethod.Put, path, new { planId = unlimitedPlan });
        Assert.Equal("changed", change.Json.GetProperty("outcome").GetString());
        Assert.Equal("none", Sub(change).GetProperty("storageLimitOverride").GetProperty("kind").GetString());
        Assert.True(Sub(change).GetProperty("effectiveStorageLimit").GetProperty("unlimited").GetBoolean());

        foreach (var (body, code) in new (object, string)[]
        {
            (new { kind = "limited" }, "required"),
            (new { kind = "limited", bytes = 0L }, "out_of_range"),
            (new { kind = "limited", bytes = -Gb }, "out_of_range"),
            (new { kind = "limited", bytes = 2_000_000_000_000_000L }, "out_of_range"),
            (new { kind = "unlimited", bytes = Gb }, "unexpected"),
            (new { kind = "forever" }, "invalid_kind"),
            (new { }, "invalid_kind")
        })
        {
            var rejected = await CallAsync(admin, HttpMethod.Put, SubscriptionPath(tenant.Id.Value, "/storage-limit-override"), body);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.Status);
            Assert.Equal(("storageLimitOverride", code), (rejected.Json.GetProperty("field").GetString(), rejected.Json.GetProperty("code").GetString()));
        }
        var assignWithBadOverride = await CallAsync(admin, HttpMethod.Put, path, new { planId = five, storageLimitOverride = new { kind = "limited", bytes = -1 } });
        Assert.Equal("storageLimitOverride", assignWithBadOverride.Json.GetProperty("field").GetString());
    }

    [Fact]
    public async Task SuspendResumeAndOverrideFollowTheStateMachineAndAreAudited()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync(platformAdmin: true);
        var admin = await world.SignInAsync(account);
        var tenant = await world.AddTenantAsync();
        var five = await CreatePlanAsync(admin, bytes: 5 * Gb);
        var ten = await CreatePlanAsync(admin, bytes: 10 * Gb);
        var path = SubscriptionPath(tenant.Id.Value);

        var missing = await CallAsync(admin, HttpMethod.Post, SubscriptionPath(tenant.Id.Value, "/suspend"));
        Assert.Equal((HttpStatusCode.NotFound, "subscription_not_found"), (missing.Status, missing.Error));
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(admin, HttpMethod.Post, SubscriptionPath(Guid.NewGuid(), "/suspend"))).Status);
        Assert.Equal("subscription_not_found", (await CallAsync(admin, HttpMethod.Put, SubscriptionPath(tenant.Id.Value, "/storage-limit-override"), new { kind = "none" })).Error);

        await CallAsync(admin, HttpMethod.Put, path, new { planId = five });
        var resumeActive = await CallAsync(admin, HttpMethod.Post, SubscriptionPath(tenant.Id.Value, "/resume"));
        Assert.Equal((HttpStatusCode.Conflict, "subscription_not_suspended"), (resumeActive.Status, resumeActive.Error));

        world.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("Suspended", (await CallAsync(admin, HttpMethod.Post, SubscriptionPath(tenant.Id.Value, "/suspend"))).Json.GetProperty("status").GetString());
        var again = await CallAsync(admin, HttpMethod.Post, SubscriptionPath(tenant.Id.Value, "/suspend"));
        Assert.Equal((HttpStatusCode.Conflict, "subscription_not_active"), (again.Status, again.Error));
        Assert.Equal(1, await OpenRowCountAsync(fixture, tenant.Id));

        // A suspended subscription still accepts an override edit; the same value again changes nothing and records nothing.
        world.Clock.Advance(TimeSpan.FromSeconds(2));
        var edit = await CallAsync(admin, HttpMethod.Put, SubscriptionPath(tenant.Id.Value, "/storage-limit-override"), new { kind = "limited", bytes = 7 * Gb });
        Assert.Equal(("Suspended", 7_000_000_000L), (edit.Json.GetProperty("status").GetString(), edit.Json.GetProperty("effectiveStorageLimit").GetProperty("bytes").GetInt64()));
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Put, SubscriptionPath(tenant.Id.Value, "/storage-limit-override"), new { kind = "limited", bytes = 7 * Gb })).Status);

        world.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("Active", (await CallAsync(admin, HttpMethod.Post, SubscriptionPath(tenant.Id.Value, "/resume"))).Json.GetProperty("status").GetString());

        // Changing plan while suspended starts a fresh Active row and closes the suspended one.
        world.Clock.Advance(TimeSpan.FromSeconds(2));
        await CallAsync(admin, HttpMethod.Post, SubscriptionPath(tenant.Id.Value, "/suspend"));
        world.Clock.Advance(TimeSpan.FromSeconds(2));
        var change = await CallAsync(admin, HttpMethod.Put, path, new { planId = ten });
        Assert.Equal(("changed", "Active"), (change.Json.GetProperty("outcome").GetString(), Sub(change).GetProperty("status").GetString()));
        var rows = await RowsOfAsync(fixture, tenant.Id);
        Assert.Equal([SubscriptionStatus.Active, SubscriptionStatus.Superseded], rows.Select(row => row.Status).Order().ToArray());

        var audit = await AuditOfTenantAsync(fixture, tenant.Id);
        Assert.Equal(
            [
                ManagedServiceAuditActions.SubscriptionAssigned, ManagedServiceAuditActions.SubscriptionSuspended,
                ManagedServiceAuditActions.StorageLimitOverrideChanged, ManagedServiceAuditActions.SubscriptionResumed,
                ManagedServiceAuditActions.SubscriptionSuspended, ManagedServiceAuditActions.SubscriptionChanged
            ],
            audit.Select(row => row.Action).ToArray());
        Assert.All(audit, row =>
        {
            Assert.Equal(AuditActorKind.PlatformAdmin, row.ActorKind);
            Assert.Equal(account.UserId, row.ActorUserId);
            Assert.Equal("subscription", row.TargetType);
        });
        using var overrideDetails = JsonDocument.Parse(audit[2].DetailsJson);
        Assert.Equal(("None", "Limited", 7_000_000_000L), (overrideDetails.RootElement.GetProperty("previousKind").GetString(),
            overrideDetails.RootElement.GetProperty("kind").GetString(), overrideDetails.RootElement.GetProperty("bytes").GetInt64()));
        using var changeDetails = JsonDocument.Parse(audit[5].DetailsJson);
        Assert.Equal(("Suspended", 10_000_000_000L), (changeDetails.RootElement.GetProperty("previousStatus").GetString(), changeDetails.RootElement.GetProperty("planStorageLimitBytes").GetInt64()));
        Assert.Equal(rows.Single(row => row.Status == SubscriptionStatus.Superseded).Id.Value.ToString("D"), changeDetails.RootElement.GetProperty("previousSubscriptionId").GetString());
        // The platform audit feed shows the same events for the tenant.
        var feed = await CallAsync(admin, HttpMethod.Get, $"/api/platform/audit?tenantId={tenant.Id.Value}&pageSize=100");
        Assert.Equal(6, feed.Json.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task AnExpiredSubscriptionIsReportedExpiredRefusesChangesAndIsReplacedByANewAssignment()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var tenant = await world.AddTenantAsync();
        var plan = await CreatePlanAsync(admin);
        var path = SubscriptionPath(tenant.Id.Value);

        Assert.Equal("invalid_value", (await CallAsync(admin, HttpMethod.Put, path, new { planId = plan, endsAt = world.Clock.GetUtcNow() })).Json.GetProperty("code").GetString());
        var assigned = await CallAsync(admin, HttpMethod.Put, path, new { planId = plan, endsAt = world.Clock.GetUtcNow().AddSeconds(30) });
        Assert.Equal(HttpStatusCode.OK, assigned.Status);
        Assert.Equal("Active", Sub(assigned).GetProperty("status").GetString());
        var expiredId = Sub(assigned).GetProperty("subscriptionId").GetGuid();

        world.Clock.Advance(TimeSpan.FromSeconds(31));
        var expired = (await CallAsync(admin, HttpMethod.Get, path)).Json.GetProperty("subscription");
        Assert.Equal("Expired", expired.GetProperty("status").GetString());
        foreach (var (method, suffix, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, "/suspend", null),
            (HttpMethod.Post, "/resume", null),
            (HttpMethod.Put, "/storage-limit-override", new { kind = "unlimited" })
        })
        {
            var refused = await CallAsync(admin, method, SubscriptionPath(tenant.Id.Value, suffix), body);
            Assert.Equal((HttpStatusCode.Conflict, "subscription_expired"), (refused.Status, refused.Error));
        }

        // Assigning again (even the same plan) starts a new Active row; the lapsed row is closed as Expired, not Superseded.
        var renewed = await CallAsync(admin, HttpMethod.Put, path, new { planId = plan });
        Assert.Equal(HttpStatusCode.OK, renewed.Status);
        Assert.Equal("assigned", renewed.Json.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, renewed.Json.GetProperty("previousSubscriptionId").ValueKind);
        Assert.Equal("Active", Sub(renewed).GetProperty("status").GetString());
        var rows = await RowsOfAsync(fixture, tenant.Id);
        Assert.Equal(SubscriptionStatus.Expired, rows.Single(row => row.Id.Value == expiredId).Status);
        Assert.Equal(1, rows.Count(row => row.IsOpen));
        var audit = await AuditOfTenantAsync(fixture, tenant.Id);
        Assert.Equal([ManagedServiceAuditActions.SubscriptionAssigned, ManagedServiceAuditActions.SubscriptionAssigned], audit.Select(row => row.Action).ToArray());
        Assert.Contains(expiredId.ToString("D"), audit[1].DetailsJson);
    }
}
