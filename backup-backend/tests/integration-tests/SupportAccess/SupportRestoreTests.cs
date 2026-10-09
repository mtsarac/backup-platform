using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Restores;
using Microsoft.EntityFrameworkCore;
using static BackupPlatform.IntegrationTests.SupportScenario;

namespace BackupPlatform.IntegrationTests;

public sealed class SupportRestoreTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string PrecheckTask = "database-restore-precheck";

    private static string ArtifactPath(Guid session, Guid artifact, string tail) => Path(session, $"/artifacts/{artifact}/{tail}");

    private object Body(SupportScenario scenario, string database = "SupportRestore", Guid? precheckId = null, string? mode = null) =>
        new { targetJobId = scenario.JobId, databaseName = database, precheckId, mode };

    /// <summary>Opens a restore session, runs a real precheck to success through the agent and returns the session and the precheck.</summary>
    private async Task<(Guid Session, Guid Precheck)> PrecheckedAsync(SupportScenario scenario, int? minutes = null)
    {
        var session = IdOf(await scenario.OpenAsync([View, Restore], minutes));
        var (status, view) = await scenario.PostAsync(ArtifactPath(session, scenario.ArtifactId, "restore-precheck"), Body(scenario), "pre-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("Precheck", view.GetProperty("purpose").GetString());
        await scenario.RunAgentAsync(PrecheckTask, targetState: "NotModified");
        return (session, view.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task RestoreTargetsAreTheCustomersRealServersAndSaySoWithoutCredentialReferences()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var session = IdOf(await scenario.OpenAsync([View, Restore]));
        var (status, targets) = await scenario.GetAsync(ArtifactPath(session, scenario.ArtifactId, "restore-targets"));
        Assert.Equal(HttpStatusCode.OK, status);
        var environment = targets.GetProperty("environment");
        Assert.Equal("CustomerEnvironment", environment.GetProperty("kind").GetString());
        Assert.False(environment.GetProperty("isolated").GetBoolean());
        Assert.Contains("not an isolated", environment.GetProperty("warning").GetString());
        var target = Assert.Single(targets.GetProperty("targets").EnumerateArray());
        Assert.Equal(scenario.JobId, target.GetProperty("jobId").GetGuid());
        Assert.Equal("sql-server", target.GetProperty("host").GetString());
        Assert.DoesNotContain("credential", targets.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.True(Assert.Single(targets.GetProperty("agents").EnumerateArray()).GetProperty("canRestore").GetBoolean());
        Assert.Contains("support.artifact.viewed", await scenario.AuditActionsAsync(session));
    }

    [Fact]
    public async Task PrecheckThenNewDatabaseRestoreRunOnTheM5EngineAndAreAuditedUnderTheSession()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var (session, precheck) = await PrecheckedAsync(scenario);
        var (startStatus, started) = await scenario.PostAsync(ArtifactPath(session, scenario.ArtifactId, "restore"), Body(scenario, precheckId: precheck), "go-1");
        Assert.Equal(HttpStatusCode.Accepted, startStatus);
        Assert.Equal("Execution", started.GetProperty("purpose").GetString());
        Assert.Equal("NewDatabase", started.GetProperty("mode").GetString());
        Assert.Equal(precheck, started.GetProperty("precheckId").GetGuid());
        Assert.Equal(JsonValueKind.Null, started.GetProperty("target").GetProperty("databaseCredentialReference").ValueKind);
        var restoreId = started.GetProperty("id").GetGuid();
        // The same key is the same restore, not a second one.
        var (again, repeat) = await scenario.PostAsync(ArtifactPath(session, scenario.ArtifactId, "restore"), Body(scenario, precheckId: precheck), "go-1");
        Assert.Equal(HttpStatusCode.OK, again);
        Assert.Equal(restoreId, repeat.GetProperty("id").GetGuid());

        await scenario.RunAgentAsync("database-restore");
        var (_, done) = await scenario.GetAsync(Path(session, $"/restores/{restoreId}"));
        Assert.Equal("Succeeded", done.GetProperty("status").GetString());
        var (_, history) = await scenario.GetAsync(Path(session, "/restores"));
        Assert.Equal(restoreId, Assert.Single(history.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        var audit = await scenario.AuditAsync(session);
        Assert.Equal(["support.restore.completed", "support.restore.precheck_requested", "support.restore.requested"],
            audit.Where(entry => entry.Action.StartsWith("support.restore.")).Select(entry => entry.Action).Order().ToArray());
        Assert.All(audit.Where(entry => entry.Action.StartsWith("support.restore.")), entry =>
        {
            Assert.Equal(AuditActorKind.PlatformAdmin, entry.ActorKind);
            Assert.Equal(scenario.AdminAccount.UserId, entry.ActorUserId);
            Assert.Equal(scenario.Tenant.Id, entry.TenantId);
            Assert.DoesNotContain("sql-server", entry.DetailsJson);
        });
        await using var context = fixture.CreatePlatformContext();
        var persisted = await context.RestoreOperations.AsNoTracking().SingleAsync(row => row.Id == restoreId);
        Assert.Equal(session, persisted.SupportSessionId);
        Assert.Equal(scenario.AdminAccount.UserId.Value, persisted.RequestedByUserId);
        Assert.Equal(RestoreMode.NewDatabase, persisted.Mode);
        Assert.Equal(RestoreOverwritePolicy.Forbidden, persisted.OverwritePolicy);
    }

    [Theory]
    [InlineData("ReplaceExistingDatabase")]
    [InlineData("replaceexistingdatabase")]
    [InlineData("anything-else")]
    public async Task ASupportSessionCanNeverReplaceAnExistingDatabase(string mode)
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var (session, precheck) = await PrecheckedAsync(scenario);
        foreach (var tail in new[] { "restore-precheck", "restore" })
        {
            var (status, body) = await scenario.PostAsync(ArtifactPath(session, scenario.ArtifactId, tail), Body(scenario, precheckId: precheck, mode: mode), "rep-" + tail);
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal("replace_forbidden", body.GetProperty("reason").GetString());
        }
        var denied = (await scenario.AuditAsync(session)).Where(entry => entry.Action == "support.access_denied").ToList();
        Assert.Equal(2, denied.Count);
        Assert.All(denied, entry => Assert.Equal("replace_forbidden", JsonDocument.Parse(entry.DetailsJson).RootElement.GetProperty("reason").GetString()));
        // The database refuses it too, whatever the application does.
        var raw = await Record.ExceptionAsync(() => scenario.Backup.ExecuteAsync("""
            UPDATE restore_operations SET "Mode" = 'ReplaceExistingDatabase', "OverwritePolicy" = 'ReplaceExisting' WHERE "SupportSessionId" IS NOT NULL
            """));
        Assert.Contains("ck_restore_operations_support", Assert.IsType<Npgsql.PostgresException>(raw).ConstraintName);
    }

    [Fact]
    public async Task StartingNeedsASuccessfulMatchingPrecheckOfTheSameSession()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var session = IdOf(await scenario.OpenAsync([View, Restore]));
        var (missing, body) = await scenario.PostAsync(ArtifactPath(session, scenario.ArtifactId, "restore"), Body(scenario), "no-precheck");
        Assert.Equal(HttpStatusCode.BadRequest, missing);
        Assert.Equal("precheckId", body.GetProperty("field").GetString());
        var (status, precheck) = await scenario.PostAsync(ArtifactPath(session, scenario.ArtifactId, "restore-precheck"), Body(scenario), "pre-1");
        Assert.Equal(HttpStatusCode.Accepted, status);
        // Not yet run: a pending precheck is not a successful one.
        var (early, earlyBody) = await scenario.PostAsync(ArtifactPath(session, scenario.ArtifactId, "restore"), Body(scenario, precheckId: precheck.GetProperty("id").GetGuid()), "early");
        Assert.Equal(HttpStatusCode.Conflict, early);
        Assert.Equal("precheck_rejected", earlyBody.GetProperty("error").GetString());
        // A different database name is a different request.
        await scenario.RunAgentAsync(PrecheckTask, targetState: "NotModified");
        var (mismatch, _) = await scenario.PostAsync(ArtifactPath(session, scenario.ArtifactId, "restore"), Body(scenario, "OtherName", precheck.GetProperty("id").GetGuid()), "mismatch");
        Assert.Equal(HttpStatusCode.Conflict, mismatch);
    }

    [Fact]
    public async Task ClosingASessionLeavesAStartedRestoreToFinishSafelyAndItsTerminalAuditIsStillWritten()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var (session, precheck) = await PrecheckedAsync(scenario);
        var (_, started) = await scenario.PostAsync(ArtifactPath(session, scenario.ArtifactId, "restore"), Body(scenario, precheckId: precheck), "go");
        var restoreId = started.GetProperty("id").GetGuid();
        var task = await scenario.RunAgentAsync("database-restore", complete: false);
        Assert.Equal(HttpStatusCode.OK, (await scenario.PostAsync(Path(session, "/close"))).Status);
        Assert.Equal("Closed", await scenario.StatusOfAsync(session));
        // The SQL work is not cut off, nor asked to stop, only because a person closed the session.
        var running = await scenario.Backup.RestoreAsync(restoreId);
        Assert.Equal("Running", RestoreApiScenario.StatusOf(running));
        Assert.Equal(JsonValueKind.Null, running.GetProperty("cancellationRequestedAt").ValueKind);
        // New work through the closed session is refused, including cancelling.
        var (cancelStatus, cancelBody) = await scenario.PostAsync(Path(session, $"/restores/{restoreId}/cancel"));
        Assert.Equal(HttpStatusCode.Forbidden, cancelStatus);
        Assert.Equal("session_closed", cancelBody.GetProperty("reason").GetString());
        var (restoreStatus, _) = await scenario.PostAsync(ArtifactPath(session, scenario.ArtifactId, "restore"), Body(scenario, "Second", precheck), "go-2");
        Assert.Equal(HttpStatusCode.Forbidden, restoreStatus);
        // The agent finishes, and the end of the restore is audited under the closed session.
        using (var completed = await scenario.Backup.CompleteRawAsync(task, RestoreApiScenario.Completion(task, result: RestoreApiScenario.SampleResult())))
            Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        Assert.Equal("Succeeded", RestoreApiScenario.StatusOf(await scenario.Backup.RestoreAsync(restoreId)));
        var terminal = (await scenario.AuditAsync(session)).Single(entry => entry.Action == "support.restore.completed");
        Assert.Equal(scenario.AdminAccount.UserId, terminal.ActorUserId);
        Assert.Equal(restoreId.ToString("D"), terminal.TargetId);
    }

    [Fact]
    public async Task ExpiryBlocksNewRestoresButARunningOneCompletesAndIsAuditedAfterTheExpiryWasRecorded()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var (session, precheck) = await PrecheckedAsync(scenario, minutes: 1);
        var (_, started) = await scenario.PostAsync(ArtifactPath(session, scenario.ArtifactId, "restore"), Body(scenario, precheckId: precheck), "go");
        var restoreId = started.GetProperty("id").GetGuid();
        var task = await scenario.Backup.MustPollRestoreAsync();
        using (var running = await scenario.Backup.StartAttemptRawAsync(task)) Assert.Equal(HttpStatusCode.OK, running.StatusCode);
        scenario.Clock.Advance(TimeSpan.FromSeconds(75));
        Assert.Equal(1, await scenario.ExpireDueAsync());
        Assert.Equal("Expired", await scenario.StatusOfAsync(session));
        var (status, body) = await scenario.PostAsync(ArtifactPath(session, scenario.ArtifactId, "restore-precheck"), Body(scenario, "Another"), "late");
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("session_expired", body.GetProperty("reason").GetString());
        Assert.Equal("Running", RestoreApiScenario.StatusOf(await scenario.Backup.RestoreAsync(restoreId)));
        using (var completed = await scenario.Backup.CompleteRawAsync(task, RestoreApiScenario.Completion(task, result: RestoreApiScenario.SampleResult())))
            Assert.Equal(HttpStatusCode.NoContent, completed.StatusCode);
        Assert.Equal("Succeeded", RestoreApiScenario.StatusOf(await scenario.Backup.RestoreAsync(restoreId)));
        var actions = await scenario.AuditActionsAsync(session);
        Assert.Contains("support.session.expired", actions);
        Assert.Contains("support.restore.completed", actions);
    }

    [Fact]
    public async Task RevokingAsksARunningRestoreToCancelWithM5SemanticsAndCancelsPreparedDownloads()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var session = IdOf(await scenario.OpenAsync([View, Download, Restore]));
        var (_, precheck) = await scenario.PostAsync(ArtifactPath(session, scenario.ArtifactId, "restore-precheck"), Body(scenario), "pre");
        await scenario.RunAgentAsync(PrecheckTask, targetState: "NotModified");
        var (_, started) = await scenario.PostAsync(ArtifactPath(session, scenario.ArtifactId, "restore"), Body(scenario, precheckId: precheck.GetProperty("id").GetGuid()), "go");
        var restoreId = started.GetProperty("id").GetGuid();
        await scenario.RunAgentAsync("database-restore", complete: false);
        var (_, download) = await scenario.PostAsync(ArtifactPath(session, scenario.ArtifactId, "downloads"), new { });
        var (revoked, view) = await scenario.PostAsync(Path(session, "/revoke"), new { reason = "Customer withdrew consent" }, who: scenario.Other);
        Assert.Equal(HttpStatusCode.OK, revoked);
        Assert.Equal("Revoked", view.GetProperty("status").GetString());
        var restore = await scenario.Backup.RestoreAsync(restoreId);
        // Cooperative: recorded for the agent, the target's condition is reported by the agent, never assumed.
        Assert.NotEqual(JsonValueKind.Null, restore.GetProperty("cancellationRequestedAt").ValueKind);
        Assert.Equal("Running", RestoreApiScenario.StatusOf(restore));
        await using var context = fixture.CreatePlatformContext();
        Assert.Equal(BackupPlatform.Domain.Downloads.DownloadState.Cancelled,
            (await context.DownloadSessions.AsNoTracking().SingleAsync(row => row.Id == download.GetProperty("sessionId").GetGuid())).State);
        var cancelAudit = (await scenario.AuditAsync(session)).Single(entry => entry.Action == "support.restore.cancel_requested");
        Assert.Equal(scenario.OtherAccount.UserId, cancelAudit.ActorUserId);
    }

    [Fact]
    public async Task TheSessionOwnerCancelsItsPendingRestoreWithM5SemanticsAndOnlyItsOwnRestores()
    {
        await using var scenario = await SupportScenario.CreateAsync(fixture);
        var (session, precheck) = await PrecheckedAsync(scenario);
        var (_, started) = await scenario.PostAsync(ArtifactPath(session, scenario.ArtifactId, "restore"), Body(scenario, precheckId: precheck), "go");
        var restoreId = started.GetProperty("id").GetGuid();
        var (otherStatus, _) = await scenario.PostAsync(Path(session, $"/restores/{restoreId}/cancel"), who: scenario.Other);
        Assert.Equal(HttpStatusCode.Forbidden, otherStatus);
        // A restore the tenant started itself is not reachable through a support session.
        var tenantRestore = await scenario.Backup.StartAsync(scenario.ArtifactId, RestoreApiScenario.Body(scenario.Backup.Agent.AgentId, "TenantOwn"));
        var (foreign, _) = await scenario.PostAsync(Path(session, $"/restores/{tenantRestore.GetProperty("id").GetGuid()}/cancel"));
        Assert.Equal(HttpStatusCode.NotFound, foreign);
        Assert.Equal(HttpStatusCode.NotFound, (await scenario.GetAsync(Path(session, $"/restores/{tenantRestore.GetProperty("id").GetGuid()}"))).Status);
        var (cancelStatus, cancelled) = await scenario.PostAsync(Path(session, $"/restores/{restoreId}/cancel"));
        Assert.Equal(HttpStatusCode.OK, cancelStatus);
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal("NotModified", cancelled.GetProperty("targetState").GetString());
        var actions = await scenario.AuditActionsAsync(session);
        Assert.Contains("support.restore.cancel_requested", actions);
        Assert.Contains("support.restore.failed", actions);
    }
}
