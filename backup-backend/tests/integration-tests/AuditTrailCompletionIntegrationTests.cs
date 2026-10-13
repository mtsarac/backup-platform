using System.Net;
using System.Text.Json;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class AuditTrailCompletionIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task AuthenticationAuditLogsSucceededFailedAndLogoutEvents()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();
        await using var context = fixture.CreatePlatformContext();
        var login = await context.AuditEvents.AsNoTracking().SingleAsync(row =>
            row.Action == AuditActions.UserLoginSucceeded && row.ActorUserId == setup.Account.UserId);
        Assert.Equal(AuditActorKind.User, login.ActorKind);
        Assert.Equal(world.Clock.GetUtcNow(), login.At);
        Assert.Equal("session", login.TargetType);
        Assert.Null(login.TenantId);

        var anonymous = new BrowserSession(world.Factory.CreateBrowser());
        foreach (var identifier in new[] { setup.Account.Identifier, "secret-input/not-a-login", null })
        {
            using var denied = await anonymous.SendAsync(HttpMethod.Post, "/api/auth/login",
                new { loginIdentifier = identifier, password = "wrong-password" });
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        var failures = await context.AuditEvents.AsNoTracking().Where(row => row.Action == AuditActions.UserLoginFailed).ToListAsync();
        Assert.Equal(3, failures.Count);
        Assert.Contains(failures, row => row.TargetId == setup.Account.UserId.ToString());
        Assert.All(failures, row =>
        {
            Assert.Equal(AuditActorKind.Anonymous, row.ActorKind);
            Assert.Equal(UserId.Anonymous, row.ActorUserId);
            Assert.Null(row.TenantId);
            Assert.DoesNotContain("wrong-password", row.DetailsJson);
            Assert.DoesNotContain("secret-input", row.DetailsJson);
        });
        Assert.Equal(1, await context.UserSessions.CountAsync(row => row.UserId == setup.Account.UserId));

        using var logout = await setup.Admin.SendAsync(HttpMethod.Post, "/api/auth/logout");
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var store = new PostgreSqlIdentityStore(fixture.Options);
        await store.RevokeSessionAsync(Guid.Parse(login.TargetId), setup.Account.UserId, default);
        var loggedOut = await context.AuditEvents.AsNoTracking().SingleAsync(row =>
            row.Action == AuditActions.UserLogout && row.ActorUserId == setup.Account.UserId);
        Assert.Equal(login.TargetId, loggedOut.TargetId);
        Assert.Equal(AuditActorKind.User, loggedOut.ActorKind);
        Assert.False(await store.IsSessionActiveAsync(Guid.Parse(login.TargetId), setup.Account.UserId, world.Clock.GetUtcNow(), default));
    }

    [Fact]
    public async Task AgentLifecycleAuditLogsTokenEnrollmentRotationAndRevocation()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var setup = await world.CreateTenantAsync();

        // 1. Create Token
        var (tokenId, tokenSecret) = await world.CreateTokenAsync(setup);

        await using (var context = fixture.CreatePlatformContext())
        {
            var tokenAudit = await context.AuditEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Action == AuditActions.AgentEnrollmentTokenCreated && e.TargetId == tokenId.ToString("D"));
            Assert.NotNull(tokenAudit);
            Assert.Equal(setup.Tenant.Id, tokenAudit.TenantId);
            Assert.Equal(setup.Account.UserId, tokenAudit.ActorUserId);
            Assert.DoesNotContain(tokenSecret, tokenAudit.DetailsJson);
        }
        var otherTenant = await world.CreateTenantAsync();
        using var deniedRevoke = await otherTenant.Admin.SendAsync(HttpMethod.Delete, $"/api/tenants/{otherTenant.Tenant.Id.Value}/agent-enrollment-tokens/{tokenId}");
        Assert.Equal(HttpStatusCode.NotFound, deniedRevoke.StatusCode);
        using var revokeToken = await setup.Admin.SendAsync(HttpMethod.Delete, $"/api/tenants/{setup.Tenant.Id.Value}/agent-enrollment-tokens/{tokenId}");
        Assert.Equal(HttpStatusCode.NoContent, revokeToken.StatusCode);
        using var repeatTokenRevoke = await setup.Admin.SendAsync(HttpMethod.Delete, $"/api/tenants/{setup.Tenant.Id.Value}/agent-enrollment-tokens/{tokenId}");
        Assert.Equal(HttpStatusCode.NotFound, repeatTokenRevoke.StatusCode);
        await using (var context = fixture.CreatePlatformContext())
        {
            var audit = await context.AuditEvents.SingleAsync(row => row.Action == AuditActions.AgentEnrollmentTokenRevoked && row.TargetId == tokenId.ToString("D"));
            Assert.Equal(setup.Account.UserId, audit.ActorUserId);
            Assert.Equal(setup.Tenant.Id, audit.TenantId);
        }

        // 2. Enroll Agent
        var enrolled = await world.EnrollAsync(setup);

        await using (var context = fixture.CreatePlatformContext())
        {
            var enrollAudit = await context.AuditEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Action == AuditActions.AgentEnrolled && e.TargetId == enrolled.AgentId.ToString());
            Assert.NotNull(enrollAudit);
            Assert.Equal(setup.Tenant.Id, enrollAudit.TenantId);
            Assert.Equal("agent", enrollAudit.TargetType);
            Assert.Equal(AuditActorKind.System, enrollAudit.ActorKind);
            Assert.Equal(UserId.System, enrollAudit.ActorUserId);
        }

        // 3. Credential Rotation via HTTP
        using var rotateResp = await world.AgentCallAsync(HttpMethod.Post, "/api/agent/credential/rotate", enrolled.CredentialId, enrolled.Secret);
        Assert.Equal(HttpStatusCode.Created, rotateResp.StatusCode);
        var rotateJson = AgentWorld.ReadJson(rotateResp);
        var pendingId = rotateJson.GetProperty("credentialId").GetGuid();
        var pendingSecret = rotateJson.GetProperty("secret").GetString()!;

        await using (var context = fixture.CreatePlatformContext())
        {
            var rotateStartAudit = await context.AuditEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Action == AuditActions.AgentCredentialRotationStarted && e.TargetId == enrolled.AgentId.ToString());
            Assert.NotNull(rotateStartAudit);
            Assert.Equal(AuditActorKind.System, rotateStartAudit.ActorKind);
            Assert.Equal(UserId.System, rotateStartAudit.ActorUserId);
            Assert.DoesNotContain(pendingSecret, rotateStartAudit.DetailsJson);
        }

        using var confirmResp = await world.AgentCallAsync(HttpMethod.Post, "/api/agent/credential/confirm", pendingId, pendingSecret);
        Assert.Equal(HttpStatusCode.OK, confirmResp.StatusCode);
        using var repeatedConfirm = await world.AgentCallAsync(HttpMethod.Post, "/api/agent/credential/confirm", pendingId, pendingSecret);
        Assert.Equal(HttpStatusCode.OK, repeatedConfirm.StatusCode);

        await using (var context = fixture.CreatePlatformContext())
        {
            var rotateConfirmAudit = await context.AuditEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Action == AuditActions.AgentCredentialRotationConfirmed && e.TargetId == enrolled.AgentId.ToString());
            Assert.NotNull(rotateConfirmAudit);
            Assert.Equal(AuditActorKind.System, rotateConfirmAudit.ActorKind);
        }

        // 4. Revoke Agent
        using var revokeResp = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{setup.Tenant.Id.Value}/agents/{enrolled.AgentId}/revoke");
        Assert.Equal(HttpStatusCode.NoContent, revokeResp.StatusCode);
        using var repeatedRevoke = await setup.Admin.SendAsync(HttpMethod.Post, $"/api/tenants/{setup.Tenant.Id.Value}/agents/{enrolled.AgentId}/revoke");
        Assert.Equal(HttpStatusCode.NoContent, repeatedRevoke.StatusCode);

        await using (var context = fixture.CreatePlatformContext())
        {
            var revokeAudit = await context.AuditEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Action == AuditActions.AgentRevoked && e.TargetId == enrolled.AgentId.ToString());
            Assert.NotNull(revokeAudit);
            Assert.Equal(setup.Tenant.Id, revokeAudit.TenantId);
            Assert.Equal(setup.Account.UserId, revokeAudit.ActorUserId);
        }
    }

    [Fact]
    public async Task BackupJobLifecycleAndRunAuditsAreRecorded()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);

        // 1. Create Job via Scenario (HTTP API)
        var jobId = await scenario.CreateJobAsync();

        await using (var context = fixture.CreatePlatformContext())
        {
            var jobAudit = await context.AuditEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Action == AuditActions.BackupJobCreated && e.TargetId == jobId.ToString("D"));
            Assert.NotNull(jobAudit);
            Assert.Equal(scenario.Setup.Tenant.Id, jobAudit.TenantId);
            Assert.Equal("backup_job", jobAudit.TargetType);
        }

        // 2. Change Schedule & Disable Job via HTTP API
        using var scheduleResp = await scenario.Admin.SendAsync(HttpMethod.Put, scenario.Root + $"/backup-jobs/{jobId}/schedule",
            new { cronExpression = "0 3 * * *", timeZone = "UTC", missedRunGraceSeconds = 300 });
        Assert.Equal(HttpStatusCode.OK, scheduleResp.StatusCode);

        using var disableResp = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + $"/backup-jobs/{jobId}/disable");
        Assert.Equal(HttpStatusCode.OK, disableResp.StatusCode);

        await using (var context = fixture.CreatePlatformContext())
        {
            var scheduleAudit = await context.AuditEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Action == AuditActions.BackupJobScheduleChanged && e.TargetId == jobId.ToString("D"));
            Assert.NotNull(scheduleAudit);
            var disableAudit = await context.AuditEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Action == AuditActions.BackupJobDisabled && e.TargetId == jobId.ToString("D"));
            Assert.NotNull(disableAudit);
        }

        // 3. Re-enable & Trigger Run
        using var enableResp = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + $"/backup-jobs/{jobId}/enable");
        Assert.Equal(HttpStatusCode.OK, enableResp.StatusCode);

        var runId = await scenario.StartRunAsync(jobId, "audit-run-key");

        await using (var context = fixture.CreatePlatformContext())
        {
            var runAudit = await context.AuditEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Action == AuditActions.BackupRunTriggered && e.TargetId == runId.ToString("D"));
            Assert.NotNull(runAudit);
            Assert.Equal(scenario.Setup.Tenant.Id, runAudit.TenantId);
            Assert.Equal("backup_run", runAudit.TargetType);
        }

        // 4. Cancel Run
        using var cancelResp = await scenario.CancelRawAsync(runId);
        Assert.Equal(HttpStatusCode.OK, cancelResp.StatusCode);

        await using (var context = fixture.CreatePlatformContext())
        {
            var cancelAudit = await context.AuditEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Action == AuditActions.BackupRunCancelRequested && e.TargetId == runId.ToString("D"));
            Assert.NotNull(cancelAudit);
            Assert.Equal(scenario.Setup.Tenant.Id, cancelAudit.TenantId);
        }
    }

    [Fact]
    public async Task RetentionAndReplicaDeletionAuditsAreRecorded()
    {
        await using var scenario = await BackupApiScenario.CreateAsync(fixture);
        var jobId = await scenario.CreateJobAsync();
        var runId = await scenario.StartRunAsync(jobId);

        // Complete the run to produce an artifact
        await scenario.SucceedNextAsync();

        Guid artifactId;
        Guid replicaId;
        await using (var context = fixture.CreatePlatformContext())
        {
            var artifact = await context.BackupArtifacts.Include(a => a.Replicas).SingleAsync(a => a.TenantId == scenario.Setup.Tenant.Id);
            artifactId = artifact.Id;
            replicaId = artifact.Replicas.First().Id;
        }

        // Request Soft Delete via API
        using var deleteResp = await scenario.Admin.SendAsync(HttpMethod.Delete, scenario.Root + $"/backup-artifacts/{artifactId}");
        Assert.Equal(HttpStatusCode.Accepted, deleteResp.StatusCode);

        await using (var context = fixture.CreatePlatformContext())
        {
            var delAudit = await context.AuditEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Action == AuditActions.BackupArtifactDeleteRequested && e.TargetId == artifactId.ToString("D"));
            Assert.NotNull(delAudit);
            Assert.Equal(scenario.Setup.Account.UserId, delAudit.ActorUserId);
        }

        // Cancel Soft Delete via API
        using var cancelResp = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.Root + $"/backup-artifacts/{artifactId}/cancel-delete");
        Assert.Equal(HttpStatusCode.OK, cancelResp.StatusCode);

        await using (var context = fixture.CreatePlatformContext())
        {
            var cancelAudit = await context.AuditEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Action == AuditActions.BackupArtifactDeleteCancelled && e.TargetId == artifactId.ToString("D"));
            Assert.NotNull(cancelAudit);
        }
        // Request delete again so replica is marked for deletion and due
        using var deleteAgainResp = await scenario.Admin.SendAsync(HttpMethod.Delete, scenario.Root + $"/backup-artifacts/{artifactId}");
        Assert.Equal(HttpStatusCode.Accepted, deleteAgainResp.StatusCode);

        var retentionStore = new BackupPlatform.Infrastructure.Backups.PostgreSqlBackupRetentionStore(fixture.Options);
        var claimed = await retentionStore.ClaimDueDeletionsAsync("test-worker", DateTimeOffset.UtcNow.AddYears(1), TimeSpan.FromMinutes(5), 10, CancellationToken.None);
        var replicaClaim = Assert.Single(claimed, c => c.ReplicaId == replicaId);

        var physicalDel = await retentionStore.CompleteDeletionAsync(replicaId, replicaClaim.LeaseToken, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.True(physicalDel);

        await using (var context = fixture.CreatePlatformContext())
        {
            var physicalAudit = await context.AuditEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Action == AuditActions.BackupReplicaPhysicalDeleteCompleted && e.TargetId == replicaId.ToString("D"));
            Assert.NotNull(physicalAudit);
            Assert.Equal(AuditActorKind.System, physicalAudit.ActorKind);
        }
    }
}
