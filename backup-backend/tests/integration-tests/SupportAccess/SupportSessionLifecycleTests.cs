using System.Net;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static BackupPlatform.IntegrationTests.SupportScenario;

namespace BackupPlatform.IntegrationTests;

public sealed class SupportSessionLifecycleTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task PlatformAdminOpensAReasonedHourLongSessionAndTheOpeningIsAuditedWithTheRealActor()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var view = await scenario.OpenAsync([View, Download], reason: "  Ticket 4711: restore fails  ");
        var id = IdOf(view);
        Assert.Equal("Active", view.GetProperty("status").GetString());
        Assert.Equal("Ticket 4711: restore fails", view.GetProperty("reason").GetString());
        Assert.Equal(scenario.TenantId, view.GetProperty("tenantId").GetGuid());
        Assert.Equal(scenario.AdminAccount.UserId.Value, view.GetProperty("platformAdminId").GetGuid());
        Assert.True(view.GetProperty("isOwner").GetBoolean());
        Assert.Equal(TimeSpan.FromHours(1), view.GetProperty("expiresAt").GetDateTimeOffset() - view.GetProperty("startedAt").GetDateTimeOffset());
        Assert.Equal([View, Download], view.GetProperty("permissions").EnumerateArray().Select(item => item.GetString()!).ToArray());

        var opened = Assert.Single(await scenario.AuditAsync(id));
        Assert.Equal(SupportAuditActionsOpened, opened.Action);
        Assert.Equal(AuditActorKind.PlatformAdmin, opened.ActorKind);
        Assert.Equal(scenario.AdminAccount.UserId, opened.ActorUserId);
        Assert.Equal(scenario.Tenant.Id, opened.TenantId);
        Assert.Equal(id.ToString("D"), opened.TargetId);
        // Being a PlatformAdmin granted no membership.
        await using var context = fixture.CreatePlatformContext();
        Assert.False(await context.TenantMemberships.AnyAsync(row => row.UserId == scenario.AdminAccount.UserId));
    }

    private const string SupportAuditActionsOpened = "support.session.opened";

    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    [InlineData(TenantRole.Operator)]
    public async Task TenantUsersCannotOpenAnySupportSessionNorSeeThePlatformList(TenantRole role)
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var account = await scenario.World.SeedUserAsync();
        await scenario.World.GrantAsync(scenario.Tenant, account, role);
        var tenantUser = await scenario.World.SignInAsync(account);
        using var open = await scenario.OpenRawAsync([View], who: tenantUser);
        Assert.Equal(HttpStatusCode.Forbidden, open.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await scenario.GetAsync("/api/platform/support-sessions", tenantUser)).Status);
        await using var context = fixture.CreatePlatformContext();
        Assert.False(await context.SupportAccessSessions.AnyAsync(row => row.PlatformAdminId == account.UserId));
    }

    [Theory]
    [InlineData("", "reason", "required")]
    [InlineData("   \t  ", "reason", "required")]
    public async Task ABlankReasonIsRefused(string reason, string field, string code)
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        using var response = await scenario.OpenRawAsync([View], reason: reason);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = AgentWorld.ReadJson(response);
        Assert.Equal(field, error.GetProperty("field").GetString());
        Assert.Equal(code, error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task DurationPermissionsAndReasonLengthAreValidatedAndFourHoursIsTheCeiling()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        async Task<(HttpStatusCode, string, string)> Try(string[] permissions, int? minutes, string? reason = null)
        {
            using var response = await scenario.OpenRawAsync(permissions, minutes, reason ?? "ticket");
            var body = AgentWorld.ReadJson(response);
            return response.StatusCode == HttpStatusCode.BadRequest ? (response.StatusCode, body.GetProperty("field").GetString()!, body.GetProperty("code").GetString()!) : (response.StatusCode, "", "");
        }
        Assert.Equal((HttpStatusCode.BadRequest, "durationMinutes", "too_long"), await Try([View], 241));
        Assert.Equal((HttpStatusCode.BadRequest, "durationMinutes", "invalid_value"), await Try([View], 0));
        Assert.Equal((HttpStatusCode.BadRequest, "permissions", "required"), await Try([], 30));
        Assert.Equal((HttpStatusCode.BadRequest, "permissions", "invalid_value"), await Try([View, "DeleteBackup"], 30));
        Assert.Equal((HttpStatusCode.BadRequest, "permissions", "view_required"), await Try([Download], 30));
        Assert.Equal((HttpStatusCode.BadRequest, "reason", "too_long"), await Try([View], 30, new string('x', 501)));
        var four = await scenario.OpenAsync([View], 240);
        Assert.Equal(TimeSpan.FromHours(4), four.GetProperty("expiresAt").GetDateTimeOffset() - four.GetProperty("startedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task DuplicateSubmissionsNeverOpenASecondSessionAndAnUnknownTenantIsNotFound()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        using var first = await scenario.OpenRawAsync([View], key: "form-1");
        using var again = await scenario.OpenRawAsync([View], key: "form-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(IdOf(AgentWorld.ReadJson(first)), IdOf(AgentWorld.ReadJson(again)));
        using var different = await scenario.OpenRawAsync([View], key: "form-1", reason: "another reason");
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
        Assert.Equal("idempotency_conflict", AgentWorld.ReadJson(different).GetProperty("error").GetString());
        using var second = await scenario.OpenRawAsync([View], key: "form-2");
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var conflict = AgentWorld.ReadJson(second);
        Assert.Equal("active_session_exists", conflict.GetProperty("error").GetString());
        Assert.Equal(IdOf(AgentWorld.ReadJson(first)), conflict.GetProperty("sessionId").GetGuid());
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(1, await context.SupportAccessSessions.CountAsync(row => row.PlatformAdminId == scenario.AdminAccount.UserId));
        Assert.Equal(1, await context.AuditEvents.CountAsync(row => row.Action == "support.session.opened" && row.ActorUserId == scenario.AdminAccount.UserId));
        using var unknown = await scenario.OpenRawAsync([View], tenantId: Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task ExpiryIsEnforcedFromTheClockOnEveryRequestWithoutAnyWorkerAndNeverExtended()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var session = IdOf(await scenario.OpenAsync([View, Download], minutes: 5));
        var artifacts = Path(session, "/artifacts");
        Assert.Equal(HttpStatusCode.OK, (await scenario.GetAsync(artifacts)).Status);
        scenario.Clock.Advance(TimeSpan.FromMinutes(5));
        var (status, body) = await scenario.GetAsync(artifacts);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("session_expired", body.GetProperty("reason").GetString());
        var (downloadStatus, downloadBody) = await scenario.PostAsync(Path(session, $"/artifacts/{scenario.ArtifactId}/downloads"), new { });
        Assert.Equal(HttpStatusCode.Forbidden, downloadStatus);
        Assert.Equal("session_expired", downloadBody.GetProperty("reason").GetString());
        // Nothing recorded the expiry yet: the table still says Active, the API says Expired.
        await using (var context = fixture.CreatePlatformContext())
            Assert.Equal("Active", (await context.SupportAccessSessions.AsNoTracking().SingleAsync(row => row.Id == session)).Status.ToString());
        Assert.Equal("Expired", await scenario.StatusOfAsync(session));
        var (_, listed) = await scenario.GetAsync("/api/platform/support-sessions?status=Expired");
        Assert.Contains(listed.GetProperty("items").EnumerateArray(), item => IdOf(item) == session);
        // The bookkeeping worker records it, with its audit, once.
        Assert.Equal(1, await scenario.ExpireDueAsync());
        Assert.Equal(0, await scenario.ExpireDueAsync());
        Assert.Equal(1, (await scenario.AuditActionsAsync(session)).Count(action => action == "support.session.expired"));
        // Closing an expired session does not resurrect or rewrite it; a new window needs a new session.
        var (closeStatus, closed) = await scenario.PostAsync(Path(session, "/close"));
        Assert.Equal(HttpStatusCode.OK, closeStatus);
        Assert.Equal("Expired", closed.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Created, (await scenario.PostAsync("/api/platform/support-sessions",
            new { tenantId = scenario.TenantId, reason = "continued investigation", permissions = new[] { View } }, Guid.NewGuid().ToString("N"))).Status);
    }

    [Fact]
    public async Task ALapsedUnrecordedSessionDoesNotBlockTheAdminsNextSession()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var first = IdOf(await scenario.OpenAsync([View], minutes: 1));
        scenario.Clock.Advance(TimeSpan.FromMinutes(2));
        var second = IdOf(await scenario.OpenAsync([View], minutes: 1));
        Assert.NotEqual(first, second);
        Assert.Equal("Expired", await scenario.StatusOfAsync(first));
        Assert.Contains("support.session.expired", await scenario.AuditActionsAsync(first));
    }

    [Fact]
    public async Task OnlyTheOwnerClosesAndAnyAdminRevokesAndNeitherCanBeUndone()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var session = IdOf(await scenario.OpenAsync([View, Download]));
        var (notOwner, denied) = await scenario.PostAsync(Path(session, "/close"), who: scenario.Other);
        Assert.Equal(HttpStatusCode.Forbidden, notOwner);
        Assert.Equal("not_session_owner", denied.GetProperty("error").GetString());
        var (closeStatus, closed) = await scenario.PostAsync(Path(session, "/close"));
        Assert.Equal(HttpStatusCode.OK, closeStatus);
        Assert.Equal("Closed", closed.GetProperty("status").GetString());
        Assert.Equal(scenario.AdminAccount.UserId.Value, closed.GetProperty("closedBy").GetGuid());
        var (again, same) = await scenario.PostAsync(Path(session, "/close"));
        Assert.Equal(HttpStatusCode.OK, again);
        Assert.Equal("Closed", same.GetProperty("status").GetString());
        var (revokeEnded, ended) = await scenario.PostAsync(Path(session, "/revoke"), new { reason = "too late" }, who: scenario.Other);
        Assert.Equal(HttpStatusCode.Conflict, revokeEnded);
        Assert.Equal("session_not_active", ended.GetProperty("error").GetString());
        var (artifactsStatus, artifacts) = await scenario.GetAsync(Path(session, "/artifacts"));
        Assert.Equal(HttpStatusCode.Forbidden, artifactsStatus);
        Assert.Equal("session_closed", artifacts.GetProperty("reason").GetString());

        var live = IdOf(await scenario.OpenAsync([View]));
        var (blank, invalid) = await scenario.PostAsync(Path(live, "/revoke"), new { reason = "  " }, who: scenario.Other);
        Assert.Equal(HttpStatusCode.BadRequest, blank);
        Assert.Equal("reason", invalid.GetProperty("field").GetString());
        var (revokedStatus, revoked) = await scenario.PostAsync(Path(live, "/revoke"), new { reason = "Access no longer justified" }, who: scenario.Other);
        Assert.Equal(HttpStatusCode.OK, revokedStatus);
        Assert.Equal("Revoked", revoked.GetProperty("status").GetString());
        Assert.Equal(scenario.OtherAccount.UserId.Value, revoked.GetProperty("closedBy").GetGuid());
        var (afterRevoke, afterBody) = await scenario.GetAsync(Path(live, "/backup-jobs"));
        Assert.Equal(HttpStatusCode.Forbidden, afterRevoke);
        Assert.Equal("session_revoked", afterBody.GetProperty("reason").GetString());
        Assert.Contains("support.session.closed", await scenario.AuditActionsAsync(session));
        Assert.Contains("support.session.revoked", await scenario.AuditActionsAsync(live));
    }

    [Fact]
    public async Task TheDatabaseItselfRefusesToChangeAGrantOrReviveAnEndedSession()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var session = IdOf(await scenario.OpenAsync([View]));
        async Task<string?> Run(string sql)
        {
            var exception = await Record.ExceptionAsync(() => scenario.Backup.ExecuteAsync(sql, ("id", session)));
            return (exception as PostgresException)?.MessageText;
        }
        Assert.Contains("immutable", await Run("UPDATE support_access_sessions SET \"Permissions\" = 7 WHERE \"Id\" = @id"));
        Assert.Contains("immutable", await Run("UPDATE support_access_sessions SET \"ExpiresAt\" = \"ExpiresAt\" + interval '1 hour' WHERE \"Id\" = @id"));
        Assert.Contains("immutable", await Run("UPDATE support_access_sessions SET \"PlatformAdminId\" = (SELECT \"UserId\" FROM platform_admins WHERE \"UserId\" <> \"PlatformAdminId\" LIMIT 1) WHERE \"Id\" = @id"));
        Assert.Contains("never deleted", await Run("DELETE FROM support_access_sessions WHERE \"Id\" = @id"));
        Assert.Equal(HttpStatusCode.OK, (await scenario.PostAsync(Path(session, "/close"))).Status);
        Assert.Contains("final", await Run("UPDATE support_access_sessions SET \"Status\" = 'Active', \"ClosedAt\" = NULL, \"ClosedBy\" = NULL WHERE \"Id\" = @id"));
        // A window longer than four hours cannot exist at all.
        var window = await Record.ExceptionAsync(() => scenario.Backup.ExecuteAsync("""
            INSERT INTO support_access_sessions ("Id","TenantId","PlatformAdminId","Reason","Permissions","StartedAt","ExpiresAt","Status","CreatedAt","IdempotencyKey","RequestFingerprint")
            SELECT gen_random_uuid(), "TenantId", "PlatformAdminId", 'x', 1, now(), now() + interval '5 hours', 'Active', now(), 'k-window', 'f' FROM support_access_sessions WHERE "Id" = @id
            """, ("id", session)));
        Assert.Contains("ck_support_access_sessions_window", Assert.IsType<PostgresException>(window).ConstraintName);
    }
}
