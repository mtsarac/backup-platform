using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class PlatformAdministrationIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed record Reply(JsonElement Json, HttpStatusCode Status);

    private static async Task<Reply> CallAsync(BrowserSession session, HttpMethod method, string path, object? body = null)
    {
        using var response = await session.SendAsync(method, path, body);
        var text = await response.Content.ReadAsStringAsync();
        return new Reply(string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone(), response.StatusCode);
    }

    private async Task<List<AuditEvent>> AuditOfAsync(TenantId tenantId)
    {
        await using var context = fixture.CreatePlatformContext();
        return await context.AuditEvents.AsNoTracking().Where(row => row.TenantId == tenantId).OrderBy(row => row.At).ThenBy(row => row.Id).ToListAsync();
    }

    [Fact]
    public async Task TenantOwnersAndAdminsCannotReachAnyPlatformRoute()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        var id = setup.Tenant.Id.Value;
        var user = setup.Account.UserId.Value;
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Get, "/api/platform/summary", null),
            (HttpMethod.Get, "/api/platform/tenants", null),
            (HttpMethod.Post, "/api/platform/tenants", new { name = "Nope" }),
            (HttpMethod.Get, $"/api/platform/tenants/{id}", null),
            (HttpMethod.Put, $"/api/platform/tenants/{id}", new { name = "Nope" }),
            (HttpMethod.Get, $"/api/platform/tenants/{id}/memberships", null),
            (HttpMethod.Post, $"/api/platform/tenants/{id}/memberships", new { userId = user }),
            (HttpMethod.Delete, $"/api/platform/tenants/{id}/memberships/{user}", null),
            (HttpMethod.Get, "/api/platform/users", null),
            (HttpMethod.Post, "/api/platform/users", new { username = "nope.user", email = "nope@example.com", displayName = "Nope", password = "Local-only-0123456789!", role = "UserAdmin", tenantIds = new[] { id } }),
            (HttpMethod.Put, $"/api/platform/users/{user}", new { username = "nope.user", email = "nope@example.com", displayName = "Nope", role = "UserAdmin" }),
            (HttpMethod.Get, "/api/platform/agents", null),
            (HttpMethod.Get, $"/api/platform/tenants/{id}/agents/{Guid.NewGuid()}", null),
            (HttpMethod.Get, "/api/platform/audit", null)
        })
        {
            var reply = await CallAsync(setup.Admin, method, path, body);
            Assert.True(reply.Status == HttpStatusCode.Forbidden, $"{method} {path} returned {reply.Status}.");
        }
        // The rejected calls changed nothing, in particular not the caller's own membership.
        await using var context = fixture.CreatePlatformContext();
        Assert.NotNull(await context.TenantMemberships.SingleOrDefaultAsync(row => row.TenantId == setup.Tenant.Id && row.UserId == setup.Account.UserId));
        Assert.False(await context.AuditEvents.AnyAsync(row => row.ActorUserId == setup.Account.UserId && row.Action != AuditActions.UserLoginSucceeded));
    }

    [Fact]
    public async Task AnonymousCallersAreUnauthorizedOnPlatformRoutes()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        foreach (var path in new[] { "/api/platform/summary", "/api/platform/tenants", "/api/platform/users", "/api/platform/agents", "/api/platform/audit" })
        {
            using var response = await world.Anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task PlatformAdminReadsAcrossTenantsWithoutAnyMembershipAndGainsNoTenantRole()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var first = await world.AddTenantAsync();
        var second = await world.AddTenantAsync();
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));

        var summary = await CallAsync(admin, HttpMethod.Get, "/api/platform/summary");
        Assert.Equal(HttpStatusCode.OK, summary.Status);
        Assert.True(summary.Json.GetProperty("tenants").GetInt64() >= 2);
        Assert.True(summary.Json.GetProperty("users").GetInt64() >= 1);
        Assert.Equal(JsonValueKind.Number, summary.Json.GetProperty("agents").GetProperty("total").ValueKind);
        Assert.Equal(JsonValueKind.Number, summary.Json.GetProperty("enabledBackupJobs").ValueKind);
        Assert.Equal(JsonValueKind.Number, summary.Json.GetProperty("failedRunsLast24Hours").ValueKind);

        var list = await CallAsync(admin, HttpMethod.Get, "/api/platform/tenants?pageSize=100");
        var ids = list.Json.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("tenantId").GetGuid()).ToHashSet();
        Assert.Contains(first.Id.Value, ids);
        Assert.Contains(second.Id.Value, ids);

        // A PlatformAdmin has access to tenant-scoped management endpoints without any membership row.
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Get, $"/api/tenants/{first.Id.Value}/context")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(admin, HttpMethod.Get, $"/api/platform/tenants/{Guid.NewGuid()}")).Status);
    }

    [Fact]
    public async Task TenantCreationAndRenameAreValidatedAndAuditedWithThePlatformAdminAsActor()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var account = await world.SeedUserAsync(platformAdmin: true);
        var admin = await world.SignInAsync(account);

        var blank = await CallAsync(admin, HttpMethod.Post, "/api/platform/tenants", new { name = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.Status);
        Assert.Equal("name", blank.Json.GetProperty("field").GetString());
        var tooLong = await CallAsync(admin, HttpMethod.Post, "/api/platform/tenants", new { name = new string('x', 201) });
        Assert.Equal("too_long", tooLong.Json.GetProperty("code").GetString());

        var name = $"Acme {Guid.NewGuid():N}";
        var created = await CallAsync(admin, HttpMethod.Post, "/api/platform/tenants", new { name = $"  {name}  " });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var tenantId = created.Json.GetProperty("tenantId").GetGuid();
        Assert.Equal(name, created.Json.GetProperty("name").GetString());

        world.Clock.Advance(TimeSpan.FromSeconds(1));
        var renamed = await CallAsync(admin, HttpMethod.Put, $"/api/platform/tenants/{tenantId}", new { name = name + " Renamed" });
        Assert.Equal(HttpStatusCode.OK, renamed.Status);
        Assert.Equal(name + " Renamed", renamed.Json.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(admin, HttpMethod.Put, $"/api/platform/tenants/{tenantId}", new { name = name + " Renamed" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(admin, HttpMethod.Put, $"/api/platform/tenants/{Guid.NewGuid()}", new { name = "x" })).Status);

        // The repeated rename changed nothing, so it recorded nothing.
        var audit = await AuditOfAsync(new TenantId(tenantId));
        Assert.Equal([AuditActions.TenantCreated, AuditActions.TenantRenamed], audit.Select(row => row.Action).ToArray());
        Assert.All(audit, row =>
        {
            Assert.Equal(AuditActorKind.PlatformAdmin, row.ActorKind);
            Assert.Equal(account.UserId, row.ActorUserId);
        });
        Assert.Contains(name, audit[1].DetailsJson);
        Assert.Contains("Renamed", audit[1].DetailsJson);

        var feed = await CallAsync(admin, HttpMethod.Get, $"/api/platform/audit?tenantId={tenantId}");
        Assert.Equal(2, feed.Json.GetProperty("totalCount").GetInt32());
        var newest = feed.Json.GetProperty("items")[0];
        Assert.Equal("tenant.renamed", newest.GetProperty("action").GetString());
        Assert.Equal("PlatformAdmin", newest.GetProperty("actorKind").GetString());
        Assert.Equal(account.UserId.Value, newest.GetProperty("actorUserId").GetGuid());
        Assert.Equal(name + " Renamed", newest.GetProperty("tenantName").GetString());
    }

    [Fact]
    public async Task AccountManagementEnforcesIdentityRolesMembershipsAndAudits()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var actor = await world.SeedUserAsync(platformAdmin: true);
        var admin = await world.SignInAsync(actor);
        var first = await world.AddTenantAsync();
        var second = await world.AddTenantAsync();
        var login = $"new.user-{Guid.NewGuid():N}";
        var email = $"{Guid.NewGuid():N}@example.com";
        var password = $"Initial-{Guid.NewGuid():N}!";
        var create = new { username = login, email, displayName = "New User", password, role = "UserAdmin", tenantIds = new[] { first.Id.Value } };

        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(admin, HttpMethod.Post, "/api/platform/users", new { create.username, create.email, create.displayName, create.password, create.role, tenantIds = Array.Empty<Guid>() })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(admin, HttpMethod.Post, "/api/platform/users", new { create.username, create.email, create.displayName, create.password, role = "PlatformAdmin", create.tenantIds })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(admin, HttpMethod.Post, "/api/platform/users", new { create.username, email = "bad", create.displayName, create.password, create.role, create.tenantIds })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(admin, HttpMethod.Post, "/api/platform/users", new { username = "bad username", create.email, create.displayName, create.password, create.role, create.tenantIds })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(admin, HttpMethod.Post, "/api/platform/users", new { create.username, create.email, create.displayName, password = "short", create.role, create.tenantIds })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(admin, HttpMethod.Post, "/api/platform/users", new { create.username, create.email, create.displayName, create.password, role = "UserAdmin,PlatformAdmin", create.tenantIds })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(admin, HttpMethod.Post, "/api/platform/users", new { create.username, create.email, create.displayName, create.password, create.role, tenantIds = new[] { first.Id.Value, first.Id.Value } })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(admin, HttpMethod.Post, "/api/platform/users", new { create.username, create.email, create.displayName, create.password, create.role, tenantIds = new[] { Guid.NewGuid() } })).Status);

        var created = await CallAsync(admin, HttpMethod.Post, "/api/platform/users", create);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var userId = created.Json.GetProperty("userId").GetGuid();
        Assert.Equal("UserAdmin", created.Json.GetProperty("role").GetString());
        Assert.DoesNotContain(password, created.Json.GetRawText());
        Assert.Equal(HttpStatusCode.Conflict, (await CallAsync(admin, HttpMethod.Post, "/api/platform/users", new { username = login.ToUpperInvariant(), email = $"other-{email}", create.displayName, create.password, create.role, create.tenantIds })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await CallAsync(admin, HttpMethod.Post, "/api/platform/users", new { username = "other-" + login, email = email.ToUpperInvariant(), create.displayName, create.password, create.role, create.tenantIds })).Status);

        await using (var forcedChange = fixture.CreatePlatformContext())
            Assert.Equal(1, await forcedChange.PasswordCredentials.Where(row => row.UserId == new UserId(userId)).ExecuteUpdateAsync(set => set.SetProperty(row => row.MustChangePassword, false)));
        var member = await world.SignInAsync(new Account(new UserId(userId), login.ToUpperInvariant(), password));
        var emailSession = await world.SignInAsync(new Account(new UserId(userId), email.ToUpperInvariant(), password));
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(member, HttpMethod.Get, $"/api/tenants/{first.Id.Value}/context")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(emailSession, HttpMethod.Get, "/api/platform/users")).Status);
        Assert.Equal(HttpStatusCode.Created, (await CallAsync(admin, HttpMethod.Post, $"/api/platform/tenants/{second.Id.Value}/memberships", new { userId })).Status);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(member, HttpMethod.Get, $"/api/tenants/{second.Id.Value}/context")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(admin, HttpMethod.Put, $"/api/platform/users/{userId}", new { create.username, create.email, create.displayName, role = "PlatformAdmin" })).Status);
        var updated = await CallAsync(admin, HttpMethod.Put, $"/api/platform/users/{userId}", new { create.username, create.email, displayName = "Updated", create.role });
        Assert.Equal(HttpStatusCode.OK, updated.Status);
        Assert.Equal("Updated", updated.Json.GetProperty("displayName").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await CallAsync(admin, HttpMethod.Put, $"/api/platform/users/{actor.UserId.Value}", new { username = login.ToUpperInvariant(), email = $"other-{email}", displayName = "Other", role = "PlatformAdmin" })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await CallAsync(admin, HttpMethod.Put, $"/api/platform/users/{actor.UserId.Value}", new { username = "other-" + login, email = email.ToUpperInvariant(), displayName = "Other", role = "PlatformAdmin" })).Status);
        await using var context = fixture.CreatePlatformContext();
        var audit = await context.AuditEvents.AsNoTracking().Where(row => row.TargetId == userId.ToString("D")).ToListAsync();
        Assert.Contains(audit, row => row.Action == AuditActions.UserCreated);
        Assert.Contains(audit, row => row.Action == AuditActions.UserUpdated);
        Assert.Equal(2, audit.Count(row => row.Action == AuditActions.MembershipAdded));
        Assert.All(audit, row => { Assert.Equal(AuditActorKind.PlatformAdmin, row.ActorKind); Assert.Equal(actor.UserId, row.ActorUserId); Assert.DoesNotContain(password, row.DetailsJson); });
    }

    [Fact]
    public async Task ConcurrentAccountCreationHasOneWinner()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var login = $"race-{Guid.NewGuid():N}";
        var email = $"{Guid.NewGuid():N}@example.com";
        var request = new { username = login, email, displayName = "Concurrent", password = "local-password-123", role = "PlatformAdmin", tenantIds = Array.Empty<Guid>() };
        var other = new { username = login.ToUpperInvariant(), email = $"other-{email}", request.displayName, request.password, request.role, request.tenantIds };
        var results = await Task.WhenAll(CallAsync(admin, HttpMethod.Post, "/api/platform/users", request), CallAsync(admin, HttpMethod.Post, "/api/platform/users", other));
        Assert.Equal(1, results.Count(result => result.Status == HttpStatusCode.Created));
        Assert.Equal(1, results.Count(result => result.Status == HttpStatusCode.Conflict));
        await using var context = fixture.CreatePlatformContext();
        var normalizedLogin = login.ToUpperInvariant();
        Assert.Equal(1, await context.PasswordCredentials.CountAsync(row => row.NormalizedLoginIdentifier == normalizedLogin));
    }

    [Fact]
    public async Task FailedSignInDoesNotRevealWhetherUsernameOrEmailExists()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var username = $"login-{Guid.NewGuid():N}";
        var email = $"{Guid.NewGuid():N}@example.com";
        var created = await CallAsync(admin, HttpMethod.Post, "/api/platform/users",
            new { username, email, displayName = "Account", password = "local-password-123", role = "PlatformAdmin", tenantIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.Created, created.Status);

        var browser = new BrowserSession(world.Factory.CreateBrowser());
        var replies = new List<Reply>();
        foreach (var identifier in new[] { username, email, $"missing-{Guid.NewGuid():N}", $"{Guid.NewGuid():N}@example.com" })
            replies.Add(await CallAsync(browser, HttpMethod.Post, "/api/auth/login", new { loginIdentifier = identifier, password = "wrong-password" }));
        Assert.All(replies, reply =>
        {
            Assert.Equal(HttpStatusCode.Unauthorized, reply.Status);
            Assert.Equal(replies[0].Json.GetRawText(), reply.Json.GetRawText());
        });
    }

    [Fact]
    public async Task MembershipLifecycleChangesRealAccessAndIsAudited()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var platformAccount = await world.SeedUserAsync(platformAdmin: true);
        var admin = await world.SignInAsync(platformAccount);
        var tenant = await world.AddTenantAsync();
        var member = await world.SeedUserAsync();
        var memberships = $"/api/platform/tenants/{tenant.Id.Value}/memberships";

        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(admin, HttpMethod.Post, memberships, new { userId = Guid.NewGuid() })).Status);
        Assert.Equal(HttpStatusCode.NotFound,
            (await CallAsync(admin, HttpMethod.Post, $"/api/platform/tenants/{Guid.NewGuid()}/memberships", new { userId = member.UserId.Value })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(admin, HttpMethod.Post, memberships, new { userId = Guid.Empty })).Status);

        // Creating a membership for a PlatformAdmin is rejected.
        var anotherPlatformAdmin = await world.SeedUserAsync(platformAdmin: true);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(admin, HttpMethod.Post, memberships, new { userId = anotherPlatformAdmin.UserId.Value })).Status);

        var added = await CallAsync(admin, HttpMethod.Post, memberships, new { userId = member.UserId.Value });
        Assert.Equal(HttpStatusCode.Created, added.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await CallAsync(admin, HttpMethod.Post, memberships, new { userId = member.UserId.Value })).Status);

        var memberSession = await world.SignInAsync(member);
        var tenantPolicy = $"/api/tenants/{tenant.Id.Value}/agent-enrollment";
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(memberSession, HttpMethod.Get, tenantPolicy)).Status);

        var listed = await CallAsync(admin, HttpMethod.Get, memberships);
        var row = Assert.Single(listed.Json.GetProperty("items").EnumerateArray());
        Assert.Equal(member.UserId.Value, row.GetProperty("userId").GetGuid());
        Assert.Equal(member.Identifier, row.GetProperty("loginIdentifier").GetString());

        world.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.NoContent, (await CallAsync(admin, HttpMethod.Delete, $"{memberships}/{member.UserId.Value}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await CallAsync(admin, HttpMethod.Delete, $"{memberships}/{member.UserId.Value}")).Status);
        // Losing the last membership disables the account and ends its sessions.
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(memberSession, HttpMethod.Get, tenantPolicy)).Status);

        var audit = await AuditOfAsync(tenant.Id);
        Assert.Equal(
            [AuditActions.MembershipAdded, AuditActions.MembershipRemoved],
            audit.Select(entry => entry.Action).ToArray());
        Assert.All(audit, entry =>
        {
            Assert.Equal(AuditActorKind.PlatformAdmin, entry.ActorKind);
            Assert.Equal(platformAccount.UserId, entry.ActorUserId);
            Assert.Equal(member.UserId.Value.ToString("D"), entry.TargetId);
        });
    }

    [Fact]
    public async Task EnrollmentPolicyChangesAreAuditedWithTheKindOfAuthorityUsed()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var platformAccount = await world.SeedUserAsync(platformAdmin: true);
        var platform = await world.SignInAsync(platformAccount);
        var setup = await world.CreateTenantAsync(enrollmentEnabled: false);
        var platformPath = $"/api/platform/tenants/{setup.Tenant.Id.Value}/agent-enrollment";
        var tenantPath = $"/api/tenants/{setup.Tenant.Id.Value}/agent-enrollment";

        Assert.Equal(HttpStatusCode.OK, (await CallAsync(platform, HttpMethod.Put, platformPath, new { enabled = true })).Status);
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(platform, HttpMethod.Put, platformPath, new { enabled = true })).Status);
        world.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(setup.Admin, HttpMethod.Put, tenantPath, new { enabled = false })).Status);

        var audit = await AuditOfAsync(setup.Tenant.Id);
        Assert.Equal(2, audit.Count);
        Assert.All(audit, entry => Assert.Equal(AuditActions.EnrollmentPolicyChanged, entry.Action));
        Assert.Equal(AuditActorKind.PlatformAdmin, audit[0].ActorKind);
        Assert.Equal(platformAccount.UserId, audit[0].ActorUserId);
        Assert.Equal(AuditActorKind.TenantAdministrator, audit[1].ActorKind);
        Assert.Equal(setup.Account.UserId, audit[1].ActorUserId);

        var detail = await CallAsync(platform, HttpMethod.Get, $"/api/platform/tenants/{setup.Tenant.Id.Value}");
        Assert.False(detail.Json.GetProperty("enrollment").GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.String, detail.Json.GetProperty("enrollment").GetProperty("changedAt").ValueKind);
    }

    [Fact]
    public async Task GlobalAgentListSpansTenantsFiltersAndPaginatesWhileTenantListsStayIsolated()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var first = await world.CreateTenantAsync();
        var second = await world.CreateTenantAsync();
        var firstAgent = await world.EnrollAsync(first);
        var secondAgent = await world.EnrollAsync(second);
        using (var heartbeat = await world.HeartbeatAsync(firstAgent))
        {
            Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        }
        using (var sync = await world.SyncCapabilitiesAsync(firstAgent, AgentWorld.CapabilitySnapshot(AgentWorld.CapabilityReport("backup.file-system"))))
        {
            Assert.True(sync.IsSuccessStatusCode);
        }
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));

        var all = await CallAsync(admin, HttpMethod.Get, "/api/platform/agents?pageSize=100");
        var items = all.Json.GetProperty("items").EnumerateArray().ToList();
        var seen = items.Select(item => item.GetProperty("agentId").GetGuid()).ToHashSet();
        Assert.Contains(firstAgent.AgentId, seen);
        Assert.Contains(secondAgent.AgentId, seen);
        var row = items.Single(item => item.GetProperty("agentId").GetGuid() == firstAgent.AgentId);
        Assert.Equal(first.Tenant.Id.Value, row.GetProperty("tenantId").GetGuid());
        Assert.Equal(first.Tenant.Name, row.GetProperty("tenantName").GetString());
        Assert.Equal("Active", row.GetProperty("status").GetString());
        Assert.Equal("online", row.GetProperty("connectivity").GetString());
        Assert.Equal("integration-host", row.GetProperty("hostName").GetString());
        Assert.Equal(1, row.GetProperty("capabilityCount").GetInt32());
        Assert.Equal(JsonValueKind.String, row.GetProperty("capabilitiesReportedAt").ValueKind);
        Assert.Equal(JsonValueKind.String, row.GetProperty("lastSeenAt").ValueKind);
        var unreported = items.Single(item => item.GetProperty("agentId").GetGuid() == secondAgent.AgentId);
        Assert.Equal("unknown", unreported.GetProperty("connectivity").GetString());
        Assert.Equal(0, unreported.GetProperty("capabilityCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, unreported.GetProperty("capabilitiesReportedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, unreported.GetProperty("lastSeenAt").ValueKind);

        var byTenant = await CallAsync(admin, HttpMethod.Get, $"/api/platform/agents?tenantId={second.Tenant.Id.Value}");
        Assert.Equal(secondAgent.AgentId, Assert.Single(byTenant.Json.GetProperty("items").EnumerateArray()).GetProperty("agentId").GetGuid());
        var online = await CallAsync(admin, HttpMethod.Get, $"/api/platform/agents?tenantId={first.Tenant.Id.Value}&connectivity=online&status=Active");
        Assert.Equal(1, online.Json.GetProperty("totalCount").GetInt32());
        var offline = await CallAsync(admin, HttpMethod.Get, $"/api/platform/agents?tenantId={first.Tenant.Id.Value}&connectivity=offline");
        Assert.Equal(0, offline.Json.GetProperty("totalCount").GetInt32());
        var search = await CallAsync(admin, HttpMethod.Get, $"/api/platform/agents?search={second.Tenant.Name.ToUpperInvariant()}");
        Assert.Equal(secondAgent.AgentId, Assert.Single(search.Json.GetProperty("items").EnumerateArray()).GetProperty("agentId").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(admin, HttpMethod.Get, "/api/platform/agents?connectivity=sideways")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(admin, HttpMethod.Get, "/api/platform/agents?status=7")).Status);

        var pageOne = await CallAsync(admin, HttpMethod.Get, "/api/platform/agents?pageSize=1&page=1");
        var pageTwo = await CallAsync(admin, HttpMethod.Get, "/api/platform/agents?pageSize=1&page=2");
        Assert.Single(pageOne.Json.GetProperty("items").EnumerateArray());
        Assert.True(pageOne.Json.GetProperty("totalCount").GetInt32() >= 2);
        Assert.Equal(pageOne.Json.GetProperty("totalCount").GetInt32(), pageTwo.Json.GetProperty("totalCount").GetInt32());
        Assert.NotEqual(
            pageOne.Json.GetProperty("items")[0].GetProperty("agentId").GetGuid(), pageTwo.Json.GetProperty("items")[0].GetProperty("agentId").GetGuid());

        var detail = await CallAsync(admin, HttpMethod.Get, $"/api/platform/tenants/{first.Tenant.Id.Value}/agents/{firstAgent.AgentId}");
        Assert.Equal(HttpStatusCode.OK, detail.Status);
        Assert.Equal("backup.file-system", detail.Json.GetProperty("capabilities")[0].GetProperty("identifier").GetString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await CallAsync(admin, HttpMethod.Get, $"/api/platform/tenants/{first.Tenant.Id.Value}/agents/{secondAgent.AgentId}")).Status);

        var tenantDetail = await CallAsync(admin, HttpMethod.Get, $"/api/platform/tenants/{first.Tenant.Id.Value}");
        Assert.Equal(1, tenantDetail.Json.GetProperty("agents").GetProperty("total").GetInt32());
        Assert.Equal(1, tenantDetail.Json.GetProperty("agents").GetProperty("online").GetInt32());

        // Tenant isolation is intact for the tenant's own administrator: only that tenant's agent is visible.
        var tenantList = await CallAsync(first.Admin, HttpMethod.Get, $"/api/tenants/{first.Tenant.Id.Value}/agents");
        Assert.Equal(firstAgent.AgentId, Assert.Single(tenantList.Json.EnumerateArray()).GetProperty("agentId").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(first.Admin, HttpMethod.Get, $"/api/tenants/{second.Tenant.Id.Value}/agents")).Status);
    }

    [Fact]
    public async Task ListsRejectInvalidPagingAndReportTotalsAcrossPages()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var marker = Guid.NewGuid().ToString("N");
        for (var index = 0; index < 3; index++)
        {
            Assert.Equal(HttpStatusCode.Created, (await CallAsync(admin, HttpMethod.Post, "/api/platform/tenants", new { name = $"Paged {marker} {index}" })).Status);
        }

        foreach (var path in new[] { "/api/platform/tenants", "/api/platform/users", "/api/platform/agents", "/api/platform/audit" })
        {
            foreach (var query in new[] { "?page=0", "?pageSize=0", "?pageSize=101", "?page=-1" })
            {
                var reply = await CallAsync(admin, HttpMethod.Get, path + query);
                Assert.True(reply.Status == HttpStatusCode.BadRequest, $"{path}{query} returned {reply.Status}.");
            }
        }

        var names = new List<string>();
        for (var page = 1; page <= 2; page++)
        {
            var body = await CallAsync(admin, HttpMethod.Get, $"/api/platform/tenants?search={marker}&pageSize=2&page={page}");
            Assert.Equal(3, body.Json.GetProperty("totalCount").GetInt32());
            Assert.Equal(page, body.Json.GetProperty("page").GetInt32());
            Assert.Equal(2, body.Json.GetProperty("pageSize").GetInt32());
            names.AddRange(body.Json.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!));
        }
        Assert.Equal([$"Paged {marker} 0", $"Paged {marker} 1", $"Paged {marker} 2"], names);
        var beyond = await CallAsync(admin, HttpMethod.Get, $"/api/platform/tenants?search={marker}&pageSize=2&page=3");
        Assert.Empty(beyond.Json.GetProperty("items").EnumerateArray());
        Assert.Equal(3, beyond.Json.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task OnlyPlatformOperationsMayWriteAuditEventsAndTheyAreAppendOnly()
    {
        var tenant = new Tenant(TenantId.New(), $"Audit {Guid.NewGuid():N}");
        await using (var setup = fixture.CreatePlatformContext())
        {
            setup.Tenants.Add(tenant);
            await setup.SaveChangesAsync();
        }
        var actor = UserId.New();
        await using (var scoped = fixture.CreateContext(tenant.Id))
        {
            scoped.AuditEvents.Add(AuditEvent.Record(DateTimeOffset.UtcNow, actor, AuditActorKind.PlatformAdmin, AuditActions.TenantCreated, tenant.Id, "tenant", "x"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => scoped.SaveChangesAsync());
        }
        await using var platform = fixture.CreatePlatformContext();
        var recorded = AuditEvent.Record(DateTimeOffset.UtcNow, actor, AuditActorKind.PlatformAdmin, AuditActions.TenantCreated, tenant.Id, "tenant", "x");
        platform.AuditEvents.Add(recorded);
        await platform.SaveChangesAsync();
        platform.AuditEvents.Remove(recorded);
        await Assert.ThrowsAsync<InvalidOperationException>(() => platform.SaveChangesAsync());
    }
}
