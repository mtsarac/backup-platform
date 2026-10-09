using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static BackupPlatform.IntegrationTests.SupportScenario;

namespace BackupPlatform.IntegrationTests;

public sealed class SupportDataAccessTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static string DownloadsPath(Guid session, Guid artifact) => Path(session, $"/artifacts/{artifact}/downloads");

    [Fact]
    public async Task AdminWithoutASessionReachesNothingOfTheTenantAndAnotherAdminsSessionIsNotUsable()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var tenant = $"/api/tenants/{scenario.TenantId}";
        // PlatformAdmin is not a membership: the ordinary tenant routes stay closed for the admin.
        foreach (var path in new[] { "/backup-artifacts", $"/backup-artifacts/{scenario.ArtifactId}", $"/backup-artifacts/{scenario.ArtifactId}/download-availability", "/backup-jobs", "/restores", "/support-access/sessions" })
            Assert.Equal(HttpStatusCode.Forbidden, (await scenario.GetAsync(tenant + path)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await scenario.PostAsync(tenant + $"/backup-artifacts/{scenario.ArtifactId}/downloads", new { })).Status);
        // There is no support route that works without a session of one's own.
        var (unknown, _) = await scenario.GetAsync(Path(Guid.NewGuid(), "/artifacts"));
        Assert.Equal(HttpStatusCode.NotFound, unknown);
        var session = IdOf(await scenario.OpenAsync([View, Download]));
        var (status, body) = await scenario.GetAsync(Path(session, "/artifacts"), scenario.Other);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("not_session_owner", body.GetProperty("reason").GetString());
        var denied = (await scenario.AuditAsync(session)).Single(entry => entry.Action == "support.access_denied");
        Assert.Equal(scenario.OtherAccount.UserId, denied.ActorUserId);
        Assert.Equal(scenario.Tenant.Id, denied.TenantId);
        Assert.Equal("not_session_owner", JsonDocument.Parse(denied.DetailsJson).RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task EveryOperationIsEnforcedAgainstTheGrantedPermissionsOnTheServer()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var onlyView = IdOf(await scenario.OpenAsync([View]));
        Assert.Equal(HttpStatusCode.OK, (await scenario.GetAsync(Path(onlyView, "/artifacts"))).Status);
        var artifact = Path(onlyView, $"/artifacts/{scenario.ArtifactId}");
        foreach (var (method, path, permission) in new[]
        {
            ("GET", artifact + "/download-availability", Download), ("POST", artifact + "/downloads", Download),
            ("GET", artifact + "/restore-targets", Restore), ("POST", artifact + "/restore-precheck", Restore), ("POST", artifact + "/restore", Restore)
        })
        {
            var (status, body) = method == "GET" ? await scenario.GetAsync(path) : await scenario.PostAsync(path, new { targetJobId = scenario.JobId, databaseName = "X" }, "k-" + Guid.NewGuid().ToString("N"));
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal("permission_missing", body.GetProperty("reason").GetString());
            Assert.Equal(permission, body.GetProperty("permission").GetString());
        }
        Assert.Equal(HttpStatusCode.OK, (await scenario.PostAsync(Path(onlyView, "/close"))).Status);
        var withDownload = IdOf(await scenario.OpenAsync([View, Download]));
        var (restoreStatus, restoreBody) = await scenario.PostAsync(Path(withDownload, $"/artifacts/{scenario.ArtifactId}/restore-precheck"), new { targetJobId = scenario.JobId, databaseName = "X" }, "k1");
        Assert.Equal(HttpStatusCode.Forbidden, restoreStatus);
        Assert.Equal("permission_missing", restoreBody.GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.OK, (await scenario.PostAsync(DownloadsPath(withDownload, scenario.ArtifactId), new { })).Status);
        Assert.True((await scenario.AuditActionsAsync(onlyView)).Count(action => action == "support.access_denied") >= 5);
    }

    [Fact]
    public async Task MetadataShowsTheTenantsBackupsWithoutStorageLocationsHostsOrCredentialsAndIsAudited()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var session = IdOf(await scenario.OpenAsync([View]));
        var (artifactsStatus, artifacts) = await scenario.GetAsync(Path(session, "/artifacts"));
        Assert.Equal(HttpStatusCode.OK, artifactsStatus);
        var listed = Assert.Single(artifacts.GetProperty("items").EnumerateArray());
        Assert.Equal(scenario.ArtifactId, listed.GetProperty("id").GetGuid());
        var (_, detail) = await scenario.GetAsync(Path(session, $"/artifacts/{scenario.ArtifactId}"));
        Assert.Equal(scenario.Source.Checksum, detail.GetProperty("artifactChecksum").GetString());
        var replica = Assert.Single(detail.GetProperty("replicas").EnumerateArray());
        Assert.Equal("Verified", replica.GetProperty("verificationState").GetString());
        Assert.Equal("Available", replica.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Undefined, detail.GetProperty("retentionExpiresAt").ValueKind);
        var (_, jobs) = await scenario.GetAsync(Path(session, "/backup-jobs"));
        var (_, runs) = await scenario.GetAsync(Path(session, "/backup-runs"));
        Assert.Equal(scenario.JobId, Assert.Single(jobs.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(scenario.Source.Accepted.RunId, Assert.Single(runs.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        var (runStatus, _) = await scenario.GetAsync(Path(session, $"/backup-runs/{scenario.Source.Accepted.RunId}"));
        Assert.Equal(HttpStatusCode.OK, runStatus);
        // Where the bytes live, and how the customer's SQL Server is reached, is not support's to read.
        var everything = detail.GetRawText() + artifacts.GetRawText() + jobs.GetRawText() + runs.GetRawText();
        foreach (var forbidden in new[] { "storageTarget", "\"key\"", "\"host\"", "credential", scenario.Source.DirectoryPath, scenario.Source.Accepted.Key, "sql-server" })
            Assert.DoesNotContain(forbidden, everything, StringComparison.OrdinalIgnoreCase);
        var audit = await scenario.AuditAsync(session);
        Assert.Contains(audit, entry => entry.Action == "support.artifact.listed");
        var viewed = Assert.Single(audit, entry => entry.Action == "support.artifact.viewed");
        Assert.Equal(scenario.ArtifactId.ToString("D"), viewed.TargetId);
        Assert.Equal(scenario.AdminAccount.UserId, viewed.ActorUserId);
        Assert.Equal(AuditActorKind.PlatformAdmin, viewed.ActorKind);
        Assert.Contains(audit, entry => entry.Action == "support.backup.viewed");
        Assert.DoesNotContain(scenario.Source.DirectoryPath, string.Concat(audit.Select(entry => entry.DetailsJson)));
    }

    [Fact]
    public async Task ASessionOfAnotherTenantNeverReachesThisTenantsArtifactsDownloadsOrJobs()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var otherTenant = await scenario.World.AddTenantAsync();
        var session = IdOf(await scenario.OpenAsync([View, Download, Restore], tenantId: otherTenant.Id.Value));
        var (_, artifacts) = await scenario.GetAsync(Path(session, "/artifacts"));
        Assert.Equal(0, artifacts.GetProperty("totalCount").GetInt32());
        var (_, jobs) = await scenario.GetAsync(Path(session, "/backup-jobs"));
        Assert.Empty(jobs.EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await scenario.GetAsync(Path(session, $"/artifacts/{scenario.ArtifactId}"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await scenario.GetAsync(Path(session, $"/artifacts/{scenario.ArtifactId}/download-availability"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await scenario.PostAsync(DownloadsPath(session, scenario.ArtifactId), new { })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await scenario.GetAsync(Path(session, $"/backup-runs/{scenario.Source.Accepted.RunId}"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await scenario.PostAsync(Path(session, $"/artifacts/{scenario.ArtifactId}/restore-precheck"),
            new { targetJobId = scenario.JobId, databaseName = "X" }, "k-x")).Status);
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(0, await context.DownloadSessions.CountAsync(row => row.SupportSessionId == session));
    }

    [Fact]
    public async Task SupportDownloadStreamsTheRealBytesThroughTheM4EngineAndIsAuditedEndToEnd()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var session = IdOf(await scenario.OpenAsync([View, Download]));
        var (availabilityStatus, availability) = await scenario.GetAsync(Path(session, $"/artifacts/{scenario.ArtifactId}/download-availability"));
        Assert.Equal(HttpStatusCode.OK, availabilityStatus);
        Assert.True(availability.GetProperty("available").GetBoolean());
        Assert.DoesNotContain("storageTarget", availability.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var (createStatus, created) = await scenario.PostAsync(DownloadsPath(session, scenario.ArtifactId), new { replicaId = scenario.Source.ReplicaId });
        Assert.Equal(HttpStatusCode.OK, createStatus);
        var downloadId = created.GetProperty("sessionId").GetGuid();
        var url = created.GetProperty("downloadUrl").GetString()!;
        Assert.Equal(DownloadsPath(session, scenario.ArtifactId) + $"/{downloadId}/content", url);
        Assert.DoesNotContain(scenario.Source.DirectoryPath, url);
        Assert.DoesNotContain(scenario.Source.Accepted.Key, url);
        var (_, status) = await scenario.GetAsync(DownloadsPath(session, scenario.ArtifactId) + $"/{downloadId}");
        Assert.Equal("Ready", status.GetProperty("state").GetString());

        using var content = await scenario.Admin.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("attachment", content.Content.Headers.ContentDisposition!.DispositionType);
        var bytes = await content.Content.ReadAsByteArrayAsync();
        Assert.Equal(scenario.Source.Bytes, bytes);
        Assert.Equal(scenario.Source.Checksum, Convert.ToHexStringLower(SHA256.HashData(bytes)));

        await using var context = fixture.CreatePlatformContext();
        var persisted = await context.DownloadSessions.AsNoTracking().SingleAsync(row => row.Id == downloadId);
        Assert.Equal(DownloadState.Completed, persisted.State);
        Assert.Equal(session, persisted.SupportSessionId);
        Assert.Equal(scenario.AdminAccount.UserId.Value, persisted.UserId);
        var audit = (await scenario.AuditAsync(session)).Where(entry => entry.Action.StartsWith("support.artifact.download_")).ToList();
        // Events of one test second share a timestamp, so the order is not asserted here, only that each step is recorded exactly once.
        Assert.Equal(["support.artifact.download_completed", "support.artifact.download_requested", "support.artifact.download_started"], audit.Select(entry => entry.Action).Order().ToArray());
        Assert.All(audit, entry =>
        {
            Assert.Equal(scenario.AdminAccount.UserId, entry.ActorUserId);
            Assert.Equal(AuditActorKind.PlatformAdmin, entry.ActorKind);
            Assert.Equal(scenario.Tenant.Id, entry.TenantId);
            Assert.Equal(scenario.ArtifactId.ToString("D"), entry.TargetId);
            Assert.DoesNotContain(scenario.Source.DirectoryPath, entry.DetailsJson);
            Assert.DoesNotContain(scenario.Source.Accepted.Key, entry.DetailsJson);
        });
        using var done = JsonDocument.Parse(audit.Single(entry => entry.Action.EndsWith("completed")).DetailsJson);
        Assert.Equal("Completed", done.RootElement.GetProperty("result").GetString());
        Assert.Equal(scenario.Source.Bytes.Length, done.RootElement.GetProperty("transferredBytes").GetInt64());
        var (_, listed) = await scenario.GetAsync(Path(session, "/downloads"));
        Assert.Equal(downloadId, Assert.Single(listed.GetProperty("items").EnumerateArray()).GetProperty("downloadId").GetGuid());
    }

    [Fact]
    public async Task ADownloadClaimBelongsToItsAdminItsSessionAndItsChannel()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var mine = IdOf(await scenario.OpenAsync([View, Download]));
        var theirs = IdOf(await scenario.OpenAsync([View, Download], who: scenario.Other));
        var (_, created) = await scenario.PostAsync(DownloadsPath(mine, scenario.ArtifactId), new { });
        var downloadId = created.GetProperty("sessionId").GetGuid();
        // The other admin cannot use it through my session, nor through their own.
        var (viaMine, viaMineBody) = await scenario.GetAsync(DownloadsPath(mine, scenario.ArtifactId) + $"/{downloadId}/content", scenario.Other);
        Assert.Equal(HttpStatusCode.Forbidden, viaMine);
        Assert.Equal("not_session_owner", viaMineBody.GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await scenario.GetAsync(DownloadsPath(theirs, scenario.ArtifactId) + $"/{downloadId}/content", scenario.Other)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await scenario.GetAsync(DownloadsPath(theirs, scenario.ArtifactId) + $"/{downloadId}", scenario.Other)).Status);
        // Even the same admin, when also a tenant Admin, cannot carry it onto the tenant's own route.
        await scenario.World.GrantAsync(scenario.Tenant, scenario.AdminAccount, TenantRole.Admin);
        using var tenantRoute = await scenario.Admin.GetAsync($"/api/tenants/{scenario.TenantId}/backup-artifacts/{scenario.ArtifactId}/downloads/{downloadId}/content");
        Assert.Equal(HttpStatusCode.NotFound, tenantRoute.StatusCode);
        using var own = await scenario.Admin.Client.GetAsync(DownloadsPath(mine, scenario.ArtifactId) + $"/{downloadId}/content");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(scenario.Source.Bytes, await own.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ClosingASessionRefusesNewDownloadsCancelsPreparedOnesAndStreamingNeverStartsAfterwards()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var session = IdOf(await scenario.OpenAsync([View, Download]));
        var (_, created) = await scenario.PostAsync(DownloadsPath(session, scenario.ArtifactId), new { });
        var downloadId = created.GetProperty("sessionId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await scenario.PostAsync(Path(session, "/close"))).Status);
        var (newStatus, newBody) = await scenario.PostAsync(DownloadsPath(session, scenario.ArtifactId), new { });
        Assert.Equal(HttpStatusCode.Forbidden, newStatus);
        Assert.Equal("session_closed", newBody.GetProperty("reason").GetString());
        var (contentStatus, contentBody) = await scenario.GetAsync(DownloadsPath(session, scenario.ArtifactId) + $"/{downloadId}/content");
        Assert.Equal(HttpStatusCode.Forbidden, contentStatus);
        Assert.Equal("session_closed", contentBody.GetProperty("reason").GetString());
        await using var context = fixture.CreatePlatformContext();
        var persisted = await context.DownloadSessions.AsNoTracking().SingleAsync(row => row.Id == downloadId);
        Assert.Equal(DownloadState.Cancelled, persisted.State);
        Assert.Equal("support_session_closed", persisted.FailureCode);
        Assert.NotNull(persisted.ReleasedAt);
        Assert.Contains("support.artifact.download_failed", await scenario.AuditActionsAsync(session));
    }

    [Fact]
    public async Task ConcurrentDownloadRequestsAndACloseAreSerializedSoNothingSurvivesTheClose()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        for (var round = 0; round < 3; round++)
        {
            var session = IdOf(await scenario.OpenAsync([View, Download]));
            var path = DownloadsPath(session, scenario.ArtifactId);
            var requests = Enumerable.Range(0, 6).Select(_ => scenario.PostAsync(path, new { })).ToList();
            var close = scenario.PostAsync(Path(session, "/close"));
            var results = await Task.WhenAll(requests);
            Assert.Equal(HttpStatusCode.OK, (await close).Status);
            // Each request is either admitted before the close or refused after it; nothing else (no 5xx, no half state).
            Assert.All(results, result =>
            {
                if (result.Status == HttpStatusCode.OK) return;
                Assert.Equal(HttpStatusCode.Forbidden, result.Status);
                Assert.Equal("session_closed", result.Body.GetProperty("reason").GetString());
            });
            await using var context = fixture.CreatePlatformContext();
            var rows = await context.DownloadSessions.AsNoTracking().Where(row => row.SupportSessionId == session).ToListAsync();
            Assert.Equal(results.Count(result => result.Status == HttpStatusCode.OK), rows.Count);
            Assert.All(rows, row => Assert.Equal(DownloadState.Cancelled, row.State));
        }
    }

    [Fact]
    public async Task AnAuditThatCannotBeWrittenFailsClosedForMetadataAndForDownloads()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var session = IdOf(await scenario.OpenAsync([View, Download]));
        await scenario.Backup.ExecuteAsync("""
            CREATE FUNCTION test_reject_support_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."Action" IN ('support.artifact.viewed', 'support.artifact.download_requested', 'support.artifact.download_started') THEN
                    RAISE EXCEPTION 'audit store unavailable';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER test_reject_support_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION test_reject_support_audit();
            """);
        try
        {
            var (viewStatus, viewBody) = await scenario.GetAsync(Path(session, $"/artifacts/{scenario.ArtifactId}"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, viewStatus);
            Assert.Equal("support_unavailable", viewBody.GetProperty("error").GetString());
            Assert.DoesNotContain("artifactChecksum", viewBody.GetRawText());
            var (prepareStatus, _) = await scenario.PostAsync(DownloadsPath(session, scenario.ArtifactId), new { });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, prepareStatus);
            await using var context = fixture.CreatePlatformContext();
            Assert.Equal(0, await context.DownloadSessions.CountAsync(row => row.SupportSessionId == session));
            // Opening a session is audited too: no audit, no session.
            await scenario.Backup.ExecuteAsync("CREATE OR REPLACE FUNCTION test_reject_support_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.\"Action\" = 'support.session.opened' THEN RAISE EXCEPTION 'audit store unavailable'; END IF; RETURN NEW; END $$");
            Assert.Equal(HttpStatusCode.OK, (await scenario.PostAsync(Path(session, "/close"))).Status);
            using var open = await scenario.OpenRawAsync([View]);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, open.StatusCode);
            Assert.Equal(1, await context.SupportAccessSessions.CountAsync(row => row.PlatformAdminId == scenario.AdminAccount.UserId));
        }
        finally
        {
            await scenario.Backup.ExecuteAsync("DROP TRIGGER test_reject_support_audit ON audit_events; DROP FUNCTION test_reject_support_audit()");
        }
    }

    [Fact]
    public async Task TenantAdministratorsSeeExactlyTheirOwnSupportHistoryAndNobodyElseDoes()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var session = IdOf(await scenario.OpenAsync([View, Download], reason: "Investigating failed nightly backup"));
        await scenario.GetAsync(Path(session, $"/artifacts/{scenario.ArtifactId}"));
        var (_, created) = await scenario.PostAsync(DownloadsPath(session, scenario.ArtifactId), new { });
        using (var content = await scenario.Admin.Client.GetAsync(created.GetProperty("downloadUrl").GetString()!))
            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        var root = $"/api/tenants/{scenario.TenantId}/support-access";
        using var listed = await scenario.TenantAdminGetAsync(root + "/sessions");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var item = Assert.Single(AgentWorld.ReadJson(listed).GetProperty("items").EnumerateArray());
        Assert.Equal(session, IdOf(item));
        Assert.Equal("Investigating failed nightly backup", item.GetProperty("reason").GetString());
        Assert.Equal([View, Download], item.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToArray());
        var summary = item.GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("artifactsViewed").GetInt32());
        Assert.Equal(1, summary.GetProperty("downloadsStarted").GetInt32());
        Assert.Equal(1, summary.GetProperty("downloadsCompleted").GetInt32());
        Assert.Equal(0, summary.GetProperty("restoresRequested").GetInt32());
        using var activity = await scenario.TenantAdminGetAsync(root + $"/sessions/{session}/activity");
        var actions = AgentWorld.ReadJson(activity).GetProperty("items").EnumerateArray().Select(entry => entry.GetProperty("action").GetString()!).ToList();
        Assert.Contains("support.artifact.download_completed", actions);
        Assert.Contains("support.session.opened", actions);

        foreach (var role in new[] { TenantRole.Operator, TenantRole.Viewer })
        {
            var lower = await scenario.Backup.Backup.SessionAsync(role);
            Assert.Equal(HttpStatusCode.Forbidden, (await lower.GetAsync(root + "/sessions")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await lower.GetAsync(root + $"/sessions/{session}/activity")).StatusCode);
        }
        // Another tenant's administrator has no membership here and sees nothing of its own either.
        var other = await scenario.World.CreateTenantAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await other.Admin.GetAsync(root + "/sessions")).StatusCode);
        using var ownList = await other.Admin.GetAsync($"/api/tenants/{other.Tenant.Id.Value}/support-access/sessions");
        Assert.Equal(0, AgentWorld.ReadJson(ownList).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.GetAsync($"/api/tenants/{other.Tenant.Id.Value}/support-access/sessions/{session}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.GetAsync($"/api/tenants/{other.Tenant.Id.Value}/support-access/sessions/{session}/activity")).StatusCode);
        // The history is read-only for the customer: no verb changes it or ends the session.
        Assert.True((await SendAsync(scenario.Source.Admin.Client, HttpMethod.Post, root + $"/sessions/{session}/close")).StatusCode
            is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.Equal("Active", await scenario.StatusOfAsync(session));
    }
}
