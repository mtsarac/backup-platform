using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.IntegrationTests.ManagedService;
using BackupPlatform.ManagedService.Administration;
using BackupPlatform.ManagedService.Quota;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.ManagedService.ManagedServiceTestSupport;

namespace BackupPlatform.IntegrationTests.Quota;

/// <summary>
/// The quota HTTP surface on a real host: PlatformAdmin policy and usage administration, the tenant's own read-only standing, and the
/// admission denial of a manual run start. Usage is created through the real run lifecycle, never by writing the projection.
/// </summary>
public sealed class QuotaApiIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string Policies = "/api/platform/quota-policies";
    private static readonly IReadOnlyList<ReplicaSpec> Central = [ReplicaSpec.Central];
    private readonly QuotaWorld _quota = new(fixture);

    private static string NewKey() => $"api-{Guid.NewGuid():N}";

    private static Dictionary<string, object?> ValidBody(string? key = null) => new()
    {
        ["key"] = key ?? NewKey(),
        ["name"] = UniqueName("Policy"),
        ["accountingMode"] = "LogicalArtifactBytes",
        ["includedStorageScopes"] = new[] { new { kind = "all" } },
        ["enforcementMode"] = "BlockNewBackups",
        ["warningThresholdPercent"] = 80,
        ["criticalThresholdPercent"] = 90,
        ["enabled"] = true
    };

    /// <summary>The full values of a policy as an update request (everything is required on PUT).</summary>
    private static Dictionary<string, object?> UpdateBody(string name, string accounting, object scopes, string enforcement = "BlockNewBackups", bool enabled = true) => new()
    {
        ["name"] = name,
        ["accountingMode"] = accounting,
        ["includedStorageScopes"] = scopes,
        ["enforcementMode"] = enforcement,
        ["warningThresholdPercent"] = 80,
        ["criticalThresholdPercent"] = 90,
        ["enabled"] = enabled
    };

    private static string Usage(Guid tenantId, string suffix = "") => $"/api/platform/tenants/{tenantId}/storage-usage{suffix}";

    private static string Quota(Guid tenantId) => $"/api/platform/tenants/{tenantId}/quota";

    private async Task<BrowserSession> PlatformAdminAsync(AgentWorld world, Account? account = null) =>
        await world.SignInAsync(account ?? await world.SeedUserAsync(platformAdmin: true));

    private async Task<(Guid PolicyId, string Key)> CreatePolicyAsync(BrowserSession admin, string enforcement = "BlockNewBackups", bool enabled = true, string accounting = "LogicalArtifactBytes")
    {
        var body = ValidBody();
        body["enforcementMode"] = enforcement;
        body["enabled"] = enabled;
        body["accountingMode"] = accounting;
        var reply = await CallAsync(admin, HttpMethod.Post, Policies, body);
        Assert.Equal(HttpStatusCode.Created, reply.Status);
        return (reply.Json.GetProperty("policyId").GetGuid(), (string)body["key"]!);
    }

    // ---- policies --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSeededDefaultPolicyIsListedFirstWithItsExactSeededValues()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await PlatformAdminAsync(world);
        await CreatePolicyAsync(admin);

        var list = await CallAsync(admin, HttpMethod.Get, Policies);

        Assert.Equal(HttpStatusCode.OK, list.Status);
        var items = list.Json.GetProperty("items").EnumerateArray().ToList();
        var seeded = items[0];
        Assert.Equal(QuotaPolicy.DefaultId.Value, seeded.GetProperty("policyId").GetGuid());
        Assert.Equal(("default", "Default (logical, all storage)", 1), (seeded.GetProperty("key").GetString(), seeded.GetProperty("name").GetString(), seeded.GetProperty("version").GetInt32()));
        Assert.Equal(("LogicalArtifactBytes", "BlockNewBackups", 80, 90), (seeded.GetProperty("accountingMode").GetString(), seeded.GetProperty("enforcementMode").GetString(),
            seeded.GetProperty("warningThresholdPercent").GetInt32(), seeded.GetProperty("criticalThresholdPercent").GetInt32()));
        Assert.True(seeded.GetProperty("enabled").GetBoolean());
        Assert.True(seeded.GetProperty("isDefault").GetBoolean());
        var scope = Assert.Single(seeded.GetProperty("includedStorageScopes").EnumerateArray());
        Assert.Equal("all", scope.GetProperty("kind").GetString());
        Assert.False(scope.TryGetProperty("values", out _));
        Assert.Single(items, item => item.GetProperty("isDefault").GetBoolean());
        Assert.True(items.Count >= 2);
    }

    [Fact]
    public async Task APolicyIsCreatedWithCanonicalScopesAndAuditedAndKeysAndNamesAreUnique()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync(platformAdmin: true);
        var admin = await PlatformAdminAsync(world, account);
        var body = ValidBody();
        body["includedStorageScopes"] = new object[]
        {
            new { kind = "providerType", values = new[] { "Central", "GoogleDrive", "central" } },
            new { kind = "ownershipType", values = new[] { "platformmanaged" } }
        };
        body["accountingMode"] = "physicalreplicabytes";
        body["enforcementMode"] = "WarnOnly";
        body["warningThresholdPercent"] = 70;
        body["criticalThresholdPercent"] = 95;

        var created = await CallAsync(admin, HttpMethod.Post, Policies, body);

        Assert.Equal(HttpStatusCode.Created, created.Status);
        var policyId = created.Json.GetProperty("policyId").GetGuid();
        Assert.Equal((body["key"], 1, "PhysicalReplicaBytes", "WarnOnly", 70, 95, true, false),
            (created.Json.GetProperty("key").GetString(), created.Json.GetProperty("version").GetInt32(), created.Json.GetProperty("accountingMode").GetString(),
                created.Json.GetProperty("enforcementMode").GetString(), created.Json.GetProperty("warningThresholdPercent").GetInt32(),
                created.Json.GetProperty("criticalThresholdPercent").GetInt32(), created.Json.GetProperty("enabled").GetBoolean(), created.Json.GetProperty("isDefault").GetBoolean()));
        // Values are lowercased, de-duplicated and sorted; the ownership name is the canonical one; the selectors are ordered by kind.
        var scopes = created.Json.GetProperty("includedStorageScopes").EnumerateArray().ToList();
        Assert.Equal(new string?[] { "providerType", "ownershipType" }, scopes.Select(scope => scope.GetProperty("kind").GetString()).ToArray());
        Assert.Equal(new string?[] { "central", "googledrive" }, scopes[0].GetProperty("values").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.Equal(new string?[] { "PlatformManaged" }, scopes[1].GetProperty("values").EnumerateArray().Select(value => value.GetString()).ToArray());

        var audit = Assert.Single(await AuditOfTargetAsync(fixture, policyId));
        Assert.Equal((ManagedServiceAuditActions.QuotaPolicyCreated, AuditActorKind.PlatformAdmin, account.UserId, "quota_policy"), (audit.Action, audit.ActorKind, audit.ActorUserId, audit.TargetType));
        Assert.Null(audit.TenantId);

        var sameKey = ValidBody((string)body["key"]!);
        Assert.Equal((HttpStatusCode.Conflict, "quota_policy_key_taken"), await Reply(admin, HttpMethod.Post, Policies, sameKey));
        var sameName = ValidBody();
        sameName["name"] = ((string)body["name"]!).ToUpperInvariant();
        Assert.Equal((HttpStatusCode.Conflict, "quota_policy_key_taken"), await Reply(admin, HttpMethod.Post, Policies, sameName));
        Assert.Single(await AuditOfTargetAsync(fixture, policyId));
    }

    private static async Task<(HttpStatusCode, string?)> Reply(BrowserSession session, HttpMethod method, string path, object? body)
    {
        var reply = await CallAsync(session, method, path, body);
        return (reply.Status, reply.Json.ValueKind == JsonValueKind.Object && reply.Json.TryGetProperty("error", out var error) ? error.GetString() : null);
    }

    private static readonly (string Property, object? Value, string Field, string Code)[] InvalidPolicyValues =
    [
        ("warningThresholdPercent", 0, "warningThresholdPercent", "out_of_range"),
        ("warningThresholdPercent", 100, "warningThresholdPercent", "out_of_range"),
        ("warningThresholdPercent", null, "warningThresholdPercent", "required"),
        ("criticalThresholdPercent", 80, "criticalThresholdPercent", "out_of_range"),
        ("criticalThresholdPercent", 70, "criticalThresholdPercent", "out_of_range"),
        ("criticalThresholdPercent", 101, "criticalThresholdPercent", "out_of_range"),
        ("criticalThresholdPercent", null, "criticalThresholdPercent", "required"),
        ("accountingMode", "Bogus", "accountingMode", "invalid_value"),
        ("accountingMode", "1", "accountingMode", "invalid_value"),
        ("accountingMode", "2", "accountingMode", "invalid_value"),
        ("accountingMode", null, "accountingMode", "required"),
        ("enforcementMode", "Block", "enforcementMode", "invalid_value"),
        ("enforcementMode", "3", "enforcementMode", "invalid_value"),
        ("enforcementMode", "", "enforcementMode", "required"),
        ("includedStorageScopes", new object[] { new { kind = "bogusKind", values = new[] { "gold" } } }, "includedStorageScopes", "unsupported_scope_kind"),
        ("includedStorageScopes", new object[] { new { kind = "StorageConnection", values = new[] { "c1" } } }, "includedStorageScopes", "unsupported_scope_kind"),
        ("includedStorageScopes", new object[] { new { kind = "1" } }, "includedStorageScopes", "unsupported_scope_kind"),
        ("includedStorageScopes", new object[] { new { kind = "all" }, new { kind = "bogusKind", values = new[] { "gold" } } }, "includedStorageScopes", "unsupported_scope_kind"),
        ("includedStorageScopes", new object[] { new { kind = "quotaScope", values = new[] { "Bad Scope!" } } }, "includedStorageScopes", "invalid_value"),
        ("includedStorageScopes", new object[] { new { kind = "storageConnection", values = new[] { "c1" } } }, "includedStorageScopes", "invalid_value"),
        ("includedStorageScopes", Array.Empty<object>(), "includedStorageScopes", "required"),
        ("includedStorageScopes", null, "includedStorageScopes", "required"),
        ("includedStorageScopes", new object[] { new { kind = "providerType" } }, "includedStorageScopes", "invalid_value"),
        ("includedStorageScopes", new object[] { new { kind = "providerType", values = new[] { "Bad Provider!" } } }, "includedStorageScopes", "invalid_value"),
        ("includedStorageScopes", new object[] { new { kind = "ownershipType", values = new[] { "Bogus" } } }, "includedStorageScopes", "invalid_value"),
        ("includedStorageScopes", new object[] { new { kind = "all", values = new[] { "x" } } }, "includedStorageScopes", "unexpected_values"),
        ("name", "   ", "name", "required"),
        ("name", new string('n', 101), "name", "too_long"),
        ("key", "Not A Key", "key", "invalid_value"),
        ("key", "", "key", "required"),
        ("key", null, "key", "required")
    ];

    [Fact]
    public async Task InvalidPolicyInputIsRejectedWithTheFieldAndCodeAndChangesNothing()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await PlatformAdminAsync(world);
        var (policyId, _) = await CreatePolicyAsync(admin);
        var before = (await CallAsync(admin, HttpMethod.Get, Policies)).Json.GetProperty("items").GetArrayLength();

        foreach (var (property, value, field, code) in InvalidPolicyValues)
        {
            var body = ValidBody();
            body[property] = value;
            var created = await CallAsync(admin, HttpMethod.Post, Policies, body);

            var rejection = Rejection(created);
            Assert.True(rejection == (HttpStatusCode.BadRequest, "invalid_request", field, code), $"POST {property}: got {rejection}.");

            // The same value is just as invalid on update (a key cannot be updated at all).
            if (property == "key")
            {
                continue;
            }
            var update = UpdateBody(UniqueName("Policy"), "LogicalArtifactBytes", new[] { new { kind = "all" } });
            update[property] = value;
            var rejected = await CallAsync(admin, HttpMethod.Put, $"{Policies}/{policyId}", update);
            var updateRejection = Rejection(rejected);
            Assert.True(updateRejection == (HttpStatusCode.BadRequest, "invalid_request", field, code), $"PUT {property}: got {updateRejection}.");
        }

        var after = await CallAsync(admin, HttpMethod.Get, Policies);
        Assert.Equal(before, after.Json.GetProperty("items").GetArrayLength());
        var untouched = after.Json.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("policyId").GetGuid() == policyId);
        Assert.Equal(1, untouched.GetProperty("version").GetInt32());
    }

    private static (HttpStatusCode Status, string? Error, string? Field, string? Code) Rejection(ApiReply reply) =>
        reply.Json.ValueKind == JsonValueKind.Object && reply.Json.TryGetProperty("field", out var field)
            ? (reply.Status, reply.Error, field.GetString(), reply.Json.GetProperty("code").GetString())
            : (reply.Status, null, null, null);

    [Fact]
    public async Task UpdatingAnUnknownPolicyIsNotFoundAndTheDefaultPolicyCannotBeDisabled()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await PlatformAdminAsync(world);

        var unknown = await CallAsync(admin, HttpMethod.Put, $"{Policies}/{Guid.NewGuid()}", UpdateBody(UniqueName("Policy"), "LogicalArtifactBytes", new[] { new { kind = "all" } }));
        Assert.Equal((HttpStatusCode.NotFound, "quota_policy_not_found"), (unknown.Status, unknown.Error));

        var disable = await CallAsync(admin, HttpMethod.Put, $"{Policies}/{QuotaPolicy.DefaultId.Value}",
            UpdateBody("Default (logical, all storage)", "LogicalArtifactBytes", new[] { new { kind = "all" } }, enabled: false));
        Assert.Equal((HttpStatusCode.Conflict, "default_policy_required"), (disable.Status, disable.Error));
        var seeded = (await CallAsync(admin, HttpMethod.Get, Policies)).Json.GetProperty("items")[0];
        Assert.Equal((true, 1), (seeded.GetProperty("enabled").GetBoolean(), seeded.GetProperty("version").GetInt32()));
        Assert.Empty(await AuditOfTargetAsync(fixture, QuotaPolicy.DefaultId.Value));
    }

    [Fact]
    public async Task ChangingAPolicyBumpsItsVersionRecalculatesTheTenantsThatUseItAndIsAuditedOnlyWhenSomethingChanged()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync(platformAdmin: true);
        var admin = await PlatformAdminAsync(world, account);
        var (policyId, key) = await CreatePolicyAsync(admin);
        var agent = await _quota.AgentAsync(20 * Gb, key);
        var tenant = agent.TenantId.Value;
        var job = await _quota.JobAsync(agent);
        // One 2 GB artifact stored three times: central (platform), google drive and sftp (tenant managed).
        await _quota.PublishAsync(_quota.RunStore(), agent, job, 2 * Gb, [ReplicaSpec.Central, ReplicaSpec.GoogleDriveLike, ReplicaSpec.SftpLike], Stamp());
        var name = UniqueName("Renamed");
        var all = new[] { new { kind = "all" } };

        var before = await CallAsync(admin, HttpMethod.Get, Usage(tenant));
        Assert.Equal((2 * Gb, key, 1, "LogicalArtifactBytes"), (before.Json.GetProperty("usedBytes").GetInt64(), before.Json.GetProperty("policyKey").GetString(),
            before.Json.GetProperty("policyVersion").GetInt32(), before.Json.GetProperty("accountingMode").GetString()));

        var steps = new (string Accounting, object Scopes, long ExpectedBytes, int Version)[]
        {
            ("PhysicalReplicaBytes", all, 6 * Gb, 2),
            ("PhysicalReplicaBytes", new[] { new { kind = "providerType", values = new[] { "central", "sftp" } } }, 4 * Gb, 3),
            ("PhysicalReplicaBytes", new[] { new { kind = "ownershipType", values = new[] { "platformmanaged" } } }, 2 * Gb, 4),
            ("PhysicalReplicaBytes", new[] { new { kind = "ownershipType", values = new[] { "TenantManaged" } } }, 4 * Gb, 5),
            ("LogicalArtifactBytes", new[] { new { kind = "ownershipType", values = new[] { "TenantManaged" } } }, 2 * Gb, 6)
        };
        foreach (var (accounting, scopes, expectedBytes, version) in steps)
        {
            world.Clock.Advance(TimeSpan.FromSeconds(1));
            var changed = await CallAsync(admin, HttpMethod.Put, $"{Policies}/{policyId}", UpdateBody(name, accounting, scopes));

            Assert.Equal(HttpStatusCode.OK, changed.Status);
            Assert.Equal((version, 1), (changed.Json.GetProperty("policy").GetProperty("version").GetInt32(), changed.Json.GetProperty("tenantsRecalculated").GetInt32()));
            var usage = await CallAsync(admin, HttpMethod.Get, Usage(tenant));
            Assert.Equal((expectedBytes, version, accounting), (usage.Json.GetProperty("usedBytes").GetInt64(), usage.Json.GetProperty("policyVersion").GetInt32(), usage.Json.GetProperty("accountingMode").GetString()));
            Assert.Equal(expectedBytes, (await CallAsync(admin, HttpMethod.Get, Quota(tenant))).Json.GetProperty("usedBytes").GetInt64());
        }

        // The same values again: success, no new version, nothing recalculated, nothing audited.
        var unchanged = await CallAsync(admin, HttpMethod.Put, $"{Policies}/{policyId}", UpdateBody(name, "LogicalArtifactBytes", new[] { new { kind = "ownershipType", values = new[] { "TenantManaged" } } }));
        Assert.Equal(HttpStatusCode.OK, unchanged.Status);
        Assert.Equal((6, 0), (unchanged.Json.GetProperty("policy").GetProperty("version").GetInt32(), unchanged.Json.GetProperty("tenantsRecalculated").GetInt32()));

        var audit = await AuditOfTargetAsync(fixture, policyId);
        Assert.Equal(1 + steps.Length, audit.Count);
        Assert.Single(audit, row => row.Action == ManagedServiceAuditActions.QuotaPolicyCreated);
        var firstChange = audit.Single(row => row.Action == ManagedServiceAuditActions.QuotaPolicyChanged && Details(row).GetProperty("version").GetInt32() == 2);
        Assert.Equal((account.UserId, AuditActorKind.PlatformAdmin), (firstChange.ActorUserId, firstChange.ActorKind));
        Assert.Equal(("LogicalArtifactBytes", "PhysicalReplicaBytes", 1), (Details(firstChange).GetProperty("previousAccountingMode").GetString(),
            Details(firstChange).GetProperty("accountingMode").GetString(), Details(firstChange).GetProperty("previousVersion").GetInt32()));
        Assert.Equal(steps.Length, audit.Count(row => row.Action == ManagedServiceAuditActions.QuotaPolicyChanged));
    }

    private static JsonElement Details(AuditEvent row) => JsonDocument.Parse(row.DetailsJson).RootElement;

    [Fact]
    public async Task DisablingAPolicySendsItsTenantsToTheDefaultPolicyAndEnablingItBringsThemBack()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await PlatformAdminAsync(world);
        var (policyId, key) = await CreatePolicyAsync(admin, enforcement: "WarnOnly");
        var agent = await _quota.AgentAsync(5 * Gb, key);
        var tenant = agent.TenantId.Value;
        var scopes = new[] { new { kind = "all" } };
        var quota = await CallAsync(admin, HttpMethod.Get, Quota(tenant));
        Assert.Equal((key, "WarnOnly"), (quota.Json.GetProperty("policy").GetProperty("key").GetString(), quota.Json.GetProperty("enforcementMode").GetString()));
        var name = (await CallAsync(admin, HttpMethod.Get, Policies)).Json.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("policyId").GetGuid() == policyId).GetProperty("name").GetString()!;

        world.Clock.Advance(TimeSpan.FromSeconds(1));
        var disabled = await CallAsync(admin, HttpMethod.Put, $"{Policies}/{policyId}", UpdateBody(name, "LogicalArtifactBytes", scopes, "WarnOnly", enabled: false));
        Assert.Equal(HttpStatusCode.OK, disabled.Status);
        Assert.Equal(1, disabled.Json.GetProperty("tenantsRecalculated").GetInt32());
        var fallback = await CallAsync(admin, HttpMethod.Get, Quota(tenant));
        Assert.Equal(("default", "BlockNewBackups"), (fallback.Json.GetProperty("policy").GetProperty("key").GetString(), fallback.Json.GetProperty("enforcementMode").GetString()));

        world.Clock.Advance(TimeSpan.FromSeconds(1));
        await CallAsync(admin, HttpMethod.Put, $"{Policies}/{policyId}", UpdateBody(name, "LogicalArtifactBytes", scopes, "WarnOnly", enabled: true));
        var back = await CallAsync(admin, HttpMethod.Get, Quota(tenant));
        Assert.Equal((key, "WarnOnly"), (back.Json.GetProperty("policy").GetProperty("key").GetString(), back.Json.GetProperty("enforcementMode").GetString()));
    }

    [Fact]
    public async Task APlanMayOnlyNameAnExistingEnabledPolicy()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await PlatformAdminAsync(world);
        var (_, disabledKey) = await CreatePolicyAsync(admin, enabled: false);

        var unknown = await CallAsync(admin, HttpMethod.Post, "/api/platform/plans", new { name = UniqueName(), storageLimitBytes = Gb, quotaPolicyReference = NewKey() });
        Assert.Equal((HttpStatusCode.NotFound, "quota_policy_not_found"), (unknown.Status, unknown.Error));
        var disabled = await CallAsync(admin, HttpMethod.Post, "/api/platform/plans", new { name = UniqueName(), storageLimitBytes = Gb, quotaPolicyReference = disabledKey });
        Assert.Equal((HttpStatusCode.Conflict, "quota_policy_disabled"), (disabled.Status, disabled.Error));

        var planId = await CreatePlanAsync(admin);
        var update = await CallAsync(admin, HttpMethod.Put, $"/api/platform/plans/{planId}", new { name = UniqueName(), storageLimitBytes = Gb, quotaPolicyReference = disabledKey });
        Assert.Equal((HttpStatusCode.Conflict, "quota_policy_disabled"), (update.Status, update.Error));
        var unknownUpdate = await CallAsync(admin, HttpMethod.Put, $"/api/platform/plans/{planId}", new { name = UniqueName(), storageLimitBytes = Gb, quotaPolicyReference = NewKey() });
        Assert.Equal((HttpStatusCode.NotFound, "quota_policy_not_found"), (unknownUpdate.Status, unknownUpdate.Error));

        var byDefault = await CallAsync(admin, HttpMethod.Post, "/api/platform/plans", new { name = UniqueName(), storageLimitBytes = Gb, quotaPolicyReference = "default" });
        Assert.Equal(HttpStatusCode.Created, byDefault.Status);
        Assert.Equal("default", byDefault.Json.GetProperty("quotaPolicyReference").GetString());
    }

    // ---- platform usage administration -----------------------------------------------------------------------------------------

    [Fact]
    public async Task APlatformAdminReadsReconcilesAndRebuildsATenantsUsage()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync(platformAdmin: true);
        var admin = await PlatformAdminAsync(world, account);
        var agent = await _quota.AgentAsync();
        var tenant = agent.TenantId.Value;
        await _quota.PublishAsync(_quota.RunStore(), agent, await _quota.JobAsync(agent), 1_500_000_000, Central, Stamp());

        var usage = await CallAsync(admin, HttpMethod.Get, Usage(tenant));
        Assert.Equal(HttpStatusCode.OK, usage.Status);
        Assert.Equal((tenant, 1_500_000_000L, 0L, "LogicalArtifactBytes", "default", 1, QuotaPolicy.DefaultId.Value),
            (usage.Json.GetProperty("tenantId").GetGuid(), usage.Json.GetProperty("usedBytes").GetInt64(), usage.Json.GetProperty("reservedBytes").GetInt64(),
                usage.Json.GetProperty("accountingMode").GetString(), usage.Json.GetProperty("policyKey").GetString(), usage.Json.GetProperty("policyVersion").GetInt32(),
                usage.Json.GetProperty("policyId").GetGuid()));
        Assert.NotEqual(JsonValueKind.Null, usage.Json.GetProperty("calculatedAt").ValueKind);

        var quota = await CallAsync(admin, HttpMethod.Get, Quota(tenant));
        Assert.Equal(HttpStatusCode.OK, quota.Status);
        Assert.Equal((5 * Gb, false, 1_500_000_000L, 0L, 3_500_000_000L, "Normal"),
            (quota.Json.GetProperty("limitBytes").GetInt64(), quota.Json.GetProperty("unlimited").GetBoolean(), quota.Json.GetProperty("usedBytes").GetInt64(),
                quota.Json.GetProperty("reservedBytes").GetInt64(), quota.Json.GetProperty("remainingBytes").GetInt64(), quota.Json.GetProperty("state").GetString()));
        Assert.Equal((80, 90, "LogicalArtifactBytes", "BlockNewBackups", "Active", true), (quota.Json.GetProperty("warningThreshold").GetInt32(), quota.Json.GetProperty("criticalThreshold").GetInt32(),
            quota.Json.GetProperty("accountingMode").GetString(), quota.Json.GetProperty("enforcementMode").GetString(), quota.Json.GetProperty("subscriptionStatus").GetString(),
            quota.Json.GetProperty("newBackupsAllowed").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, quota.Json.GetProperty("denialCode").ValueKind);
        Assert.Equal("default", quota.Json.GetProperty("policy").GetProperty("key").GetString());

        await _quota.CorruptUsageAsync(agent.TenantId, 7, 9);
        var reconciled = await CallAsync(admin, HttpMethod.Post, Usage(tenant, "/reconcile"));
        Assert.Equal(HttpStatusCode.OK, reconciled.Status);
        Assert.Equal((true, 7L, 9L, 1_500_000_000L, 0L, 0, 1), (reconciled.Json.GetProperty("drifted").GetBoolean(), reconciled.Json.GetProperty("previousUsedBytes").GetInt64(),
            reconciled.Json.GetProperty("previousReservedBytes").GetInt64(), reconciled.Json.GetProperty("usedBytes").GetInt64(), reconciled.Json.GetProperty("reservedBytes").GetInt64(),
            reconciled.Json.GetProperty("reservationsReleased").GetInt32(), reconciled.Json.GetProperty("policyVersion").GetInt32()));
        var corrected = Assert.Single(await AuditAsync(fixture, agent.TenantId, ManagedServiceAuditActions.StorageUsageCorrected));
        Assert.Equal((account.UserId, AuditActorKind.PlatformAdmin), (corrected.ActorUserId, corrected.ActorKind));

        var clean = await CallAsync(admin, HttpMethod.Post, Usage(tenant, "/reconcile"));
        Assert.False(clean.Json.GetProperty("drifted").GetBoolean());
        Assert.Single(await AuditAsync(fixture, agent.TenantId, ManagedServiceAuditActions.StorageUsageCorrected));
        Assert.Empty(await AuditAsync(fixture, agent.TenantId, ManagedServiceAuditActions.StorageUsageRebuilt));

        var revision = (await CallAsync(admin, HttpMethod.Get, Usage(tenant))).Json.GetProperty("revision").GetInt64();
        var rebuilt = await CallAsync(admin, HttpMethod.Post, Usage(tenant, "/rebuild"));
        Assert.Equal(HttpStatusCode.OK, rebuilt.Status);
        Assert.Equal((false, 1_500_000_000L), (rebuilt.Json.GetProperty("drifted").GetBoolean(), rebuilt.Json.GetProperty("usedBytes").GetInt64()));
        // A rebuild always rewrites and is always audited, drift or not.
        var audit = Assert.Single(await AuditAsync(fixture, agent.TenantId, ManagedServiceAuditActions.StorageUsageRebuilt));
        Assert.Equal((account.UserId, AuditActorKind.PlatformAdmin), (audit.ActorUserId, audit.ActorKind));
        Assert.True((await CallAsync(admin, HttpMethod.Get, Usage(tenant))).Json.GetProperty("revision").GetInt64() > revision);
    }

    [Fact]
    public async Task UsageRoutesAnswerTenantNotFoundForAnUnknownTenant()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await PlatformAdminAsync(world);
        var unknown = Guid.NewGuid();

        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, Usage(unknown)), (HttpMethod.Get, Quota(unknown)),
            (HttpMethod.Post, Usage(unknown, "/reconcile")), (HttpMethod.Post, Usage(unknown, "/rebuild"))
        })
        {
            var reply = await CallAsync(admin, method, path);
            Assert.True(reply.Status == HttpStatusCode.NotFound && reply.Error == "tenant_not_found", $"{method} {path} returned {reply.Status}.");
        }
    }

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    public async Task TenantOwnersAndAdminsCannotReachAnyPlatformQuotaRoute(TenantRole role)
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var platform = await PlatformAdminAsync(world);
        var setup = await world.CreateTenantAsync(role);
        var tenant = setup.Tenant.Id.Value;
        var policiesBefore = (await CallAsync(platform, HttpMethod.Get, Policies)).Json.GetProperty("items").GetArrayLength();

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, Policies, null),
            (HttpMethod.Post, Policies, ValidBody()),
            (HttpMethod.Put, $"{Policies}/{QuotaPolicy.DefaultId.Value}", UpdateBody("Hijacked", "PhysicalReplicaBytes", new[] { new { kind = "all" } }, "None")),
            (HttpMethod.Get, Usage(tenant), null),
            (HttpMethod.Get, Quota(tenant), null),
            (HttpMethod.Post, Usage(tenant, "/reconcile"), null),
            (HttpMethod.Post, Usage(tenant, "/rebuild"), null)
        })
        {
            var reply = await CallAsync(setup.Admin, method, path, body);
            Assert.True(reply.Status == HttpStatusCode.Forbidden, $"{method} {path} returned {reply.Status}.");
        }

        var seeded = (await CallAsync(platform, HttpMethod.Get, Policies)).Json.GetProperty("items");
        Assert.Equal(policiesBefore, seeded.GetArrayLength());
        Assert.Equal((1, "Default (logical, all storage)"), (seeded[0].GetProperty("version").GetInt32(), seeded[0].GetProperty("name").GetString()));
        await using var context = fixture.CreatePlatformContext();
        Assert.False(await context.AuditEvents.AnyAsync(row => row.ActorUserId == setup.Account.UserId));
        Assert.Null(await _quota.StoredUsageAsync(setup.Tenant.Id));
    }

    [Fact]
    public async Task AnonymousCallersAreUnauthorizedEverywhereInTheQuotaSurface()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        foreach (var path in new[] { Policies, Usage(Guid.NewGuid()), Quota(Guid.NewGuid()), $"/api/tenants/{Guid.NewGuid()}/storage-quota" })
        {
            using var response = await world.Anonymous.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"GET {path} returned {response.StatusCode}.");
        }
    }

    // ---- the tenant's own standing -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    [InlineData(TenantRole.Operator)]
    [InlineData(TenantRole.Viewer)]
    public async Task AnyMemberOfATenantReadsItsStorageQuota(TenantRole role)
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync(role);
        await _quota.AssignAsync(setup.Tenant.Id, await _quota.PlanAsync(5 * Gb));

        using var response = await setup.Admin.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/storage-quota");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = AgentWorld.ReadJson(response);
        Assert.Equal((5 * Gb, false, 0L, 0L, 5 * Gb, "Normal"), (json.GetProperty("limitBytes").GetInt64(), json.GetProperty("unlimited").GetBoolean(), json.GetProperty("usedBytes").GetInt64(),
            json.GetProperty("reservedBytes").GetInt64(), json.GetProperty("remainingBytes").GetInt64(), json.GetProperty("state").GetString()));
        Assert.Equal((80, 90, "LogicalArtifactBytes", "Active", true), (json.GetProperty("warningThreshold").GetInt32(), json.GetProperty("criticalThreshold").GetInt32(),
            json.GetProperty("accountingMode").GetString(), json.GetProperty("subscriptionStatus").GetString(), json.GetProperty("newBackupsAllowed").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("denialCode").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, json.GetProperty("calculatedAt").ValueKind);
    }

    [Fact]
    public async Task TheTenantQuotaNamesItsPlanAndTheScopesItCounts()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var plan = await _quota.PlanAsync(5 * Gb);
        await _quota.AssignAsync(setup.Tenant.Id, plan);

        var json = AgentWorld.ReadJson(await setup.Admin.GetAsync($"/api/tenants/{setup.Tenant.Id.Value}/storage-quota"));

        Assert.Equal(("BlockNewBackups", false), (json.GetProperty("enforcementMode").GetString(), json.GetProperty("plan").GetProperty("isSystem").GetBoolean()));
        Assert.StartsWith("Quota plan ", json.GetProperty("plan").GetProperty("name").GetString());
        var scope = Assert.Single(json.GetProperty("includedStorageScopes").EnumerateArray());
        Assert.Equal("all", scope.GetProperty("kind").GetString());
        // A tenant without a subscription has no plan to name.
        var bare = await world.CreateTenantAsync();
        var none = AgentWorld.ReadJson(await bare.Admin.GetAsync($"/api/tenants/{bare.Tenant.Id.Value}/storage-quota"));
        Assert.Equal(JsonValueKind.Null, none.GetProperty("plan").ValueKind);
    }

    [Fact]
    public async Task AdminListsEveryTenantsStandingInOneFilteredPagedRead()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var platform = await PlatformAdminAsync(world);
        var tag = Guid.NewGuid().ToString("N");
        var store = _quota.RunStore();

        async Task<(Guid Id, string Name)> TenantAsync(string label, long? limit, long usedBytes, bool subscribe = true)
        {
            var tenant = await world.AddTenantAsync();
            await using (var context = fixture.CreatePlatformContext())
            {
                await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE tenants SET \"Name\" = {$"{label} {tag}"} WHERE \"Id\" = {tenant.Id.Value}");
            }
            if (subscribe)
            {
                await _quota.AssignAsync(tenant.Id, await _quota.PlanAsync(limit));
            }
            if (usedBytes > 0)
            {
                var agent = new StoreAgent(tenant.Id, await _quota.Harness.AddAgentToAsync(tenant.Id));
                await _quota.PublishAsync(store, agent, await _quota.JobAsync(agent), usedBytes, Central, Stamp());
            }
            return (tenant.Id.Value, $"{label} {tag}");
        }

        var normal = await TenantAsync("a-normal", 5 * Gb, 1 * Gb);
        var over = await TenantAsync("b-over", 5 * Gb, 5 * Gb);
        var unlimited = await TenantAsync("c-unlimited", null, 2 * Gb);
        var bare = await TenantAsync("d-none", null, 0, subscribe: false);
        string Url(string query) => $"/api/platform/storage-usage?search={tag}&{query}";

        var all = await CallAsync(platform, HttpMethod.Get, Url("pageSize=100"));
        Assert.Equal(HttpStatusCode.OK, all.Status);
        var items = all.Json.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(new[] { normal.Id, over.Id, unlimited.Id, bare.Id }, items.Select(item => item.GetProperty("tenantId").GetGuid()).ToArray());
        Assert.Equal(4, all.Json.GetProperty("totalCount").GetInt64());
        Assert.Equal(("Normal", 1 * Gb, 5 * Gb, 4 * Gb, "Active"), (items[0].GetProperty("state").GetString(), items[0].GetProperty("usedBytes").GetInt64(),
            items[0].GetProperty("limitBytes").GetInt64(), items[0].GetProperty("remainingBytes").GetInt64(), items[0].GetProperty("subscriptionStatus").GetString()));
        Assert.Equal(("OverQuota", false, "quota_exceeded"), (items[1].GetProperty("state").GetString(), items[1].GetProperty("newBackupsAllowed").GetBoolean(), items[1].GetProperty("denialCode").GetString()));
        Assert.Equal(("Unlimited", true), (items[2].GetProperty("state").GetString(), items[2].GetProperty("unlimited").GetBoolean()));
        // No subscription and no stored projection: figures and state are absent, never a computed zero.
        Assert.Equal(("NoSubscription", false, JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null),
            (items[3].GetProperty("subscriptionStatus").GetString(), items[3].GetProperty("hasUsage").GetBoolean(), items[3].GetProperty("usedBytes").ValueKind,
                items[3].GetProperty("state").ValueKind, items[3].GetProperty("plan").ValueKind, items[3].GetProperty("calculatedAt").ValueKind));

        var overOnly = await CallAsync(platform, HttpMethod.Get, Url("quotaState=overquota"));
        Assert.Equal(new[] { over.Id }, overOnly.Json.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("tenantId").GetGuid()).ToArray());
        Assert.Equal(1, overOnly.Json.GetProperty("totalCount").GetInt64());
        var none = await CallAsync(platform, HttpMethod.Get, Url("subscriptionStatus=NoSubscription"));
        Assert.Equal(new[] { bare.Id }, none.Json.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("tenantId").GetGuid()).ToArray());

        var second = await CallAsync(platform, HttpMethod.Get, Url("page=2&pageSize=3"));
        Assert.Equal((4L, 2, 3), (second.Json.GetProperty("totalCount").GetInt64(), second.Json.GetProperty("page").GetInt32(), second.Json.GetProperty("pageSize").GetInt32()));
        Assert.Equal(new[] { bare.Id }, second.Json.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("tenantId").GetGuid()).ToArray());

        Assert.Equal((HttpStatusCode.BadRequest, "invalid_request"), await Reply(platform, HttpMethod.Get, Url("quotaState=1"), null));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_request"), await Reply(platform, HttpMethod.Get, Url("subscriptionStatus=bogus"), null));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_request"), await Reply(platform, HttpMethod.Get, Url("pageSize=101"), null));

        // The list never computes or writes a projection: the tenant that had none still has none.
        Assert.Null(await _quota.StoredUsageAsync(new TenantId(bare.Id)));
    }

    [Fact]
    public async Task TheStorageUsageListIsPlatformAdminOnly()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync(TenantRole.Owner);

        var forbidden = await CallAsync(setup.Admin, HttpMethod.Get, "/api/platform/storage-usage");
        using var anonymous = await world.Anonymous.GetAsync("/api/platform/storage-usage");

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task TheTenantQuotaFollowsUsageThroughEveryStateAndTheLimitChangesOfThePlatform()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var platform = await PlatformAdminAsync(world);
        var setup = await world.CreateTenantAsync();
        var tenantId = setup.Tenant.Id;
        await _quota.AssignAsync(tenantId, await _quota.PlanAsync(5 * Gb));
        var agent = new StoreAgent(tenantId, await _quota.Harness.AddAgentToAsync(tenantId));
        var store = _quota.RunStore();
        var path = $"/api/tenants/{tenantId.Value}/storage-quota";

        // Each publication is a new job, so no estimate is reserved: usage is exactly the running total.
        var walk = new (long Added, long Used, string State)[]
        {
            (3_999_999_999, 3_999_999_999, "Normal"),
            (1, 4_000_000_000, "Warning"),
            (500_000_000, 4_500_000_000, "Critical"),
            (500_000_000, 5_000_000_000, "OverQuota")
        };
        foreach (var (added, used, state) in walk)
        {
            await _quota.PublishAsync(store, agent, await _quota.JobAsync(agent), added, Central, Stamp());
            var json = AgentWorld.ReadJson(await setup.Admin.GetAsync(path));
            Assert.Equal((used, state, 5 * Gb - used), (json.GetProperty("usedBytes").GetInt64(), json.GetProperty("state").GetString(), json.GetProperty("remainingBytes").GetInt64()));
            Assert.Equal(state != "OverQuota", json.GetProperty("newBackupsAllowed").GetBoolean());
        }
        var over = AgentWorld.ReadJson(await setup.Admin.GetAsync(path));
        Assert.Equal("quota_exceeded", over.GetProperty("denialCode").GetString());

        var subscription = SubscriptionPath(tenantId.Value);
        var unlimited = await CallAsync(platform, HttpMethod.Put, subscription + "/storage-limit-override", new { kind = "unlimited" });
        Assert.Equal(HttpStatusCode.OK, unlimited.Status);
        var unlimitedView = AgentWorld.ReadJson(await setup.Admin.GetAsync(path));
        Assert.Equal((true, JsonValueKind.Null, JsonValueKind.Null, "Unlimited", true), (unlimitedView.GetProperty("unlimited").GetBoolean(), unlimitedView.GetProperty("limitBytes").ValueKind,
            unlimitedView.GetProperty("remainingBytes").ValueKind, unlimitedView.GetProperty("state").GetString(), unlimitedView.GetProperty("newBackupsAllowed").GetBoolean()));

        await CallAsync(platform, HttpMethod.Put, subscription + "/storage-limit-override", new { kind = "limited", bytes = 10 * Gb });
        var limited = AgentWorld.ReadJson(await setup.Admin.GetAsync(path));
        Assert.Equal((10 * Gb, "Normal", 5 * Gb, true), (limited.GetProperty("limitBytes").GetInt64(), limited.GetProperty("state").GetString(),
            limited.GetProperty("remainingBytes").GetInt64(), limited.GetProperty("newBackupsAllowed").GetBoolean()));

        await CallAsync(platform, HttpMethod.Put, subscription + "/storage-limit-override", new { kind = "none" });
        var bigPlan = await CreatePlanAsync(platform, bytes: 20 * Gb);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(platform, HttpMethod.Put, subscription, new { planId = bigPlan })).Status);
        var upgraded = AgentWorld.ReadJson(await setup.Admin.GetAsync(path));
        Assert.Equal((20 * Gb, "Normal"), (upgraded.GetProperty("limitBytes").GetInt64(), upgraded.GetProperty("state").GetString()));

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(platform, HttpMethod.Post, subscription + "/suspend")).Status);
        var suspended = AgentWorld.ReadJson(await setup.Admin.GetAsync(path));
        Assert.Equal(("Suspended", false, "subscription_required"), (suspended.GetProperty("subscriptionStatus").GetString(), suspended.GetProperty("newBackupsAllowed").GetBoolean(), suspended.GetProperty("denialCode").GetString()));
    }

    [Fact]
    public async Task AMemberOfAnotherTenantCannotReadAndSeesOnlyItsOwnStanding()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var owner = await world.CreateTenantAsync();
        var stranger = await world.CreateTenantAsync();
        await _quota.AssignAsync(owner.Tenant.Id, await _quota.PlanAsync(5 * Gb));
        var agent = new StoreAgent(owner.Tenant.Id, await _quota.Harness.AddAgentToAsync(owner.Tenant.Id));
        await _quota.PublishAsync(_quota.RunStore(), agent, await _quota.JobAsync(agent), 2 * Gb, Central, Stamp());

        using var forbidden = await stranger.Admin.GetAsync($"/api/tenants/{owner.Tenant.Id.Value}/storage-quota");
        var leaked = await forbidden.Content.ReadAsStringAsync();
        using var own = await stranger.Admin.GetAsync($"/api/tenants/{stranger.Tenant.Id.Value}/storage-quota");

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.DoesNotContain("2000000000", leaked);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        var json = AgentWorld.ReadJson(own);
        Assert.Equal((0L, "NoSubscription", false, "subscription_required"), (json.GetProperty("usedBytes").GetInt64(), json.GetProperty("subscriptionStatus").GetString(),
            json.GetProperty("newBackupsAllowed").GetBoolean(), json.GetProperty("denialCode").GetString()));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("limitBytes").ValueKind);
    }

    // ---- manual run start ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AManualRunStartIsRefusedWith409ForNoSubscriptionAndForQuotaAndAdmittedOnceCapacityIsBack()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture, entitled: false);
        var tenantId = scenario.Setup.Tenant.Id;
        var platform = await PlatformAdminAsync(scenario.World);
        // Queue: with Skip, an active run of the job would answer overlap_skipped before admission is even asked.
        var jobId = await scenario.CreateJobAsync(overlapPolicy: "Queue");
        var harness = new BackupStoreHarness(fixture);

        // No subscription at all.
        using (var noSubscription = await scenario.StartRawAsync(jobId, "key-1"))
        {
            AssertDenied(noSubscription, "subscription_required");
        }
        Assert.Equal(0, await harness.CountRunsAsync(jobId));

        // A 5 GB plan, with 5.5 GB already stored by another job of the same agent.
        var planId = await CreatePlanAsync(platform, bytes: 5 * Gb);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(platform, HttpMethod.Put, SubscriptionPath(tenantId.Value), new { planId })).Status);
        var agent = new StoreAgent(tenantId, new AgentId(scenario.Agent.AgentId));
        await _quota.PublishAsync(_quota.RunStore(), agent, await _quota.JobAsync(agent), 5_500_000_000, Central, Stamp());
        using (var overQuota = await scenario.StartRawAsync(jobId, "key-2"))
        {
            AssertDenied(overQuota, "quota_exceeded");
        }
        Assert.Equal(0, await harness.CountRunsAsync(jobId));
        var quota = AgentWorld.ReadJson(await scenario.Admin.GetAsync(scenario.Root + "/storage-quota"));
        Assert.Equal(("OverQuota", false), (quota.GetProperty("state").GetString(), quota.GetProperty("newBackupsAllowed").GetBoolean()));

        // Capacity back (a 10 GB override): the very same key now starts a run, i.e. the refusal consumed nothing.
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(platform, HttpMethod.Put, SubscriptionPath(tenantId.Value) + "/storage-limit-override", new { kind = "limited", bytes = 10 * Gb })).Status);
        var runId = await scenario.StartRunAsync(jobId, "key-2");
        Assert.Equal(1, await harness.CountRunsAsync(jobId));
        Assert.Equal("Pending", BackupApiScenario.StatusOf(await scenario.RunAsync(runId)));

        // Over the plan limit again: a replay still returns the original run (it is answered before admission), a new key is refused.
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(platform, HttpMethod.Put, SubscriptionPath(tenantId.Value) + "/storage-limit-override", new { kind = "none" })).Status);
        Assert.Equal(runId, await scenario.StartRunAsync(jobId, "key-2"));
        using (var refused = await scenario.StartRawAsync(jobId, "key-3"))
        {
            AssertDenied(refused, "quota_exceeded");
        }
        Assert.Equal(1, await harness.CountRunsAsync(jobId));
    }

    private static void AssertDenied(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = AgentWorld.ReadJson(response);
        Assert.Equal(("admission_denied", code), (json.GetProperty("error").GetString(), json.GetProperty("code").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("message").GetString()));
    }

    /// <summary>
    /// The host's stale-lease sweep runs on the real clock: leases taken at "now" (never at a long gone instant) stay live while a test publishes artifacts beside it.
    /// </summary>
    private static DateTimeOffset Stamp() => DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private static Task<List<AuditEvent>> AuditAsync(PostgreSqlFixture database, TenantId tenantId, string action) =>
        AdmissionTestKit.AuditAsync(database, tenantId, action);
}
