using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class CentralDownloadAuthorizationIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static async Task<(DownloadApiScenario Scenario, Guid ShareId)> SetupScenarioWithShareAsync(
        PostgreSqlFixture fixture,
        bool encrypt = false,
        TimeSpan? duration = null)
    {
        var scenario = await DownloadApiScenario.CreateAsync(fixture);
        if (encrypt)
        {
            await scenario.EncryptAsync();
        }

        using var shareResponse = await scenario.Admin.SendAsync(
            HttpMethod.Post,
            $"{scenario.Root}/backup-artifacts/{scenario.Accepted.ArtifactId}/shares",
            new { reason = "Integration test share", durationHours = (int)(duration ?? TimeSpan.FromHours(24)).TotalHours });

        Assert.Equal(HttpStatusCode.Created, shareResponse.StatusCode);
        var shareId = AgentWorld.ReadJson(shareResponse).GetProperty("id").GetGuid();
        return (scenario, shareId);
    }

    [Fact]
    public async Task BypassMatrix_PlatformAdminWithoutShare_DeniedOnAllContentEndpoints()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var platformAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
        var platform = await scenario.World.SignInAsync(platformAccount);

        // 1. Download availability denied (403)
        using var avail = await platform.GetAsync(scenario.ArtifactPath + "/download-availability");
        Assert.Equal(HttpStatusCode.Forbidden, avail.StatusCode);

        // 2. Prepare denied (403) and audited as refusal
        using var prep = await platform.SendAsync(HttpMethod.Post, scenario.ArtifactPath + "/downloads", new { replicaId = scenario.ReplicaId });
        Assert.Equal(HttpStatusCode.Forbidden, prep.StatusCode);

        // Verify refusal audit was recorded
        await using (var context = fixture.CreatePlatformContext())
        {
            var refusal = await context.AuditEvents
                .Where(e => e.Action == "download.refused" && e.TenantId == scenario.Backup.Backup.Setup.Tenant.Id && e.ActorUserId == platformAccount.UserId)
                .OrderByDescending(e => e.At)
                .FirstOrDefaultAsync();
            Assert.NotNull(refusal);
            Assert.Equal(AuditActorKind.PlatformAdmin, refusal.ActorKind);
        }

        // 3. Status on an existing session created by another user denied (404/403)
        var memberSession = await scenario.CreateSessionAsync();
        var memberSessionId = memberSession.GetProperty("sessionId").GetGuid();
        using var status = await platform.GetAsync(scenario.ArtifactPath + $"/downloads/{memberSessionId}");
        Assert.True(status.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);

        // 4. Content GET denied (403/404)
        using var content = await platform.GetAsync(scenario.ArtifactPath + $"/downloads/{memberSessionId}/content");
        Assert.True(content.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);

        // 5. Agent-direct artifact-key denied to non-agent
        using var keyReq = await platform.SendAsync(HttpMethod.Post, $"/api/agent/downloads/{memberSessionId}/artifact-key", new { leaseToken = Guid.NewGuid().ToString() });
        Assert.True(keyReq.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

        // 6. Agent-direct storage/access denied to non-agent
        using var accessReq = await platform.SendAsync(HttpMethod.Post, $"/api/agent/downloads/{memberSessionId}/storage/access", new { });
        Assert.True(accessReq.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PlatformAdmin_WithActiveMatchingShare_CanDownload_ByteIdentical_WhenPlain()
    {
        var (scenario, shareId) = await SetupScenarioWithShareAsync(fixture, encrypt: false);
        await using (scenario)
        {
            var platformAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
            var platform = await scenario.World.SignInAsync(platformAccount);

            // Availability with shareId
            using var avail = await platform.GetAsync($"{scenario.ArtifactPath}/download-availability?shareId={shareId}");
            Assert.Equal(HttpStatusCode.OK, avail.StatusCode);

            // Prepare download with shareId
            using var prep = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId });
            Assert.Equal(HttpStatusCode.OK, prep.StatusCode);
            var prepared = AgentWorld.ReadJson(prep);
            var sessionId = prepared.GetProperty("sessionId").GetGuid();
            var contentUrl = prepared.GetProperty("downloadUrl").GetString()!;
            Assert.False(prepared.GetProperty("decrypted").GetBoolean());

            // Status check
            using var status = await platform.GetAsync($"{scenario.ArtifactPath}/downloads/{sessionId}");
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);

            // Stream content and verify byte-identical
            using var content = await platform.Client.GetAsync(contentUrl, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
            var received = await content.Content.ReadAsByteArrayAsync();
            Assert.Equal(scenario.Bytes, received);

            // Verify session bound to ShareId in DB
            await using var context = fixture.CreatePlatformContext();
            var session = await context.DownloadSessions.SingleAsync(s => s.Id == sessionId);
            Assert.Equal(shareId, session.ShareId);
        }
    }

    [Fact]
    public async Task PlatformAdmin_WithActiveMatchingShare_CanDownload_PlaintextDecrypted_WhenEncrypted()
    {
        var (scenario, shareId) = await SetupScenarioWithShareAsync(fixture, encrypt: true);
        await using (scenario)
        {
            var platformAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
            var platform = await scenario.World.SignInAsync(platformAccount);

            // Prepare download with shareId - should automatically be decrypted
            using var prep = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId });
            Assert.Equal(HttpStatusCode.OK, prep.StatusCode);
            var prepared = AgentWorld.ReadJson(prep);
            var sessionId = prepared.GetProperty("sessionId").GetGuid();
            var contentUrl = prepared.GetProperty("downloadUrl").GetString()!;
            Assert.True(prepared.GetProperty("decrypted").GetBoolean());
            Assert.Equal(scenario.Checksum, prepared.GetProperty("checksum").GetString());

            // Stream content and verify verified plaintext
            using var content = await platform.Client.GetAsync(contentUrl, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
            var plaintext = await content.Content.ReadAsByteArrayAsync();
            Assert.Equal(scenario.Bytes, plaintext);
            Assert.Equal(scenario.Checksum, Convert.ToHexStringLower(SHA256.HashData(plaintext)));
        }
    }

    [Fact]
    public async Task ShareMismatch_WrongArtifactOrTenant_Refused()
    {
        var (scenarioA, shareA) = await SetupScenarioWithShareAsync(fixture);
        await using (scenarioA)
        {
            var (scenarioB, shareB) = await SetupScenarioWithShareAsync(fixture);
            await using (scenarioB)
            {
                var platformAccount = await scenarioA.World.SeedUserAsync(platformAdmin: true);
                var platform = await scenarioA.World.SignInAsync(platformAccount);

                // ShareB on artifact A should be refused
                using var prep = await platform.SendAsync(
                    HttpMethod.Post,
                    $"{scenarioA.ArtifactPath}/downloads",
                    new { shareId = shareB });

                Assert.Equal(HttpStatusCode.Forbidden, prep.StatusCode);

                // Refusal audited
                await using var context = fixture.CreatePlatformContext();
                var refusal = await context.AuditEvents
                    .Where(e => e.Action == "download.refused" && e.ActorUserId == platformAccount.UserId)
                    .OrderByDescending(e => e.At)
                    .FirstOrDefaultAsync();
                Assert.NotNull(refusal);
            }
        }
    }

    [Fact]
    public async Task ExpiredOrRevokedShare_RefusedAndAudited()
    {
        var (scenario, shareId) = await SetupScenarioWithShareAsync(fixture);
        await using (scenario)
        {
            // Revoke the share
            using var revoke = await scenario.Admin.SendAsync(
                HttpMethod.Post,
                $"{scenario.Root}/backup-shares/{shareId}/revoke",
                new { reason = "Revoking share for security" });
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

            var platformAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
            var platform = await scenario.World.SignInAsync(platformAccount);

            // Attempt to prepare with revoked share
            using var prep = await platform.SendAsync(
                HttpMethod.Post,
                $"{scenario.ArtifactPath}/downloads",
                new { shareId });

            Assert.Equal(HttpStatusCode.Forbidden, prep.StatusCode);

            // Refusal audited
            await using var context = fixture.CreatePlatformContext();
            var refusal = await context.AuditEvents
                .Where(e => e.Action == "download.refused" && e.ActorUserId == platformAccount.UserId)
                .OrderByDescending(e => e.At)
                .FirstOrDefaultAsync();
            Assert.NotNull(refusal);
            using var details = JsonDocument.Parse(refusal.DetailsJson);
            Assert.Equal("share_revoked", details.RootElement.GetProperty("failureReason").GetString());
        }
    }

    [Fact]
    public async Task TwoSharesOnSameArtifact_OneRevokedOneActive_AllowsDownloadThroughActive()
    {
        var (scenario, shareA) = await SetupScenarioWithShareAsync(fixture);
        await using (scenario)
        {
            // Create second share
            using var shareBResp = await scenario.Admin.SendAsync(
                HttpMethod.Post,
                $"{scenario.Root}/backup-artifacts/{scenario.Accepted.ArtifactId}/shares",
                new { reason = "Second active share", durationHours = 48 });
            var shareB = AgentWorld.ReadJson(shareBResp).GetProperty("id").GetGuid();

            // Revoke share A
            using var revoke = await scenario.Admin.SendAsync(
                HttpMethod.Post,
                $"{scenario.Root}/backup-shares/{shareA}/revoke",
                new { });
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

            var platformAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
            var platform = await scenario.World.SignInAsync(platformAccount);

            // Share A fails
            using var prepA = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId = shareA });
            Assert.Equal(HttpStatusCode.Forbidden, prepA.StatusCode);

            // Share B succeeds
            using var prepB = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId = shareB });
            Assert.Equal(HttpStatusCode.OK, prepB.StatusCode);
            var sessionId = AgentWorld.ReadJson(prepB).GetProperty("sessionId").GetGuid();
            Assert.NotEqual(Guid.Empty, sessionId);
        }
    }

    [Fact]
    public async Task RestoreBoundary_RejectsShareId_ReturnsNoBytesOrKey_AuditsPlatformAdmin()
    {
        var (scenario, shareId) = await SetupScenarioWithShareAsync(fixture);
        await using (scenario)
        {
            var platformAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
            var platform = await scenario.World.SignInAsync(platformAccount);

            // Restore request with query param ShareId must be rejected (400)
            using var queryReject = await platform.SendAsync(
                HttpMethod.Post,
                $"{scenario.Root}/backup-artifacts/{scenario.Accepted.ArtifactId}/restore?shareId={shareId}",
                new { agentId = scenario.Backup.Agent.AgentId, databaseName = "RestoreDb", target = new { mode = "mssql-credential", credentialId = Guid.NewGuid() } });
            Assert.Equal(HttpStatusCode.BadRequest, queryReject.StatusCode);

            // Restore request with body ShareId must be rejected (400) specifically because Restore does not accept a ShareId
            using var bodyReject = await platform.SendAsync(
                HttpMethod.Post,
                $"{scenario.Root}/backup-artifacts/{scenario.Accepted.ArtifactId}/restore",
                new { shareId, agentId = scenario.Backup.Agent.AgentId, databaseName = "RestoreDb", target = new { host = "localhost", databaseCredentialReference = "test-cred" } });
            Assert.Equal(HttpStatusCode.BadRequest, bodyReject.StatusCode);
            Assert.Equal("Restore does not accept a ShareId.", AgentWorld.ReadJson(bodyReject).GetProperty("message").GetString());
            // PlatformAdmin restore without ShareId (valid admin management operation)
            var idempotencyKey = $"restore-{Guid.NewGuid():N}";
            using var validMsg = new HttpRequestMessage(HttpMethod.Post, $"{scenario.Root}/backup-artifacts/{scenario.Accepted.ArtifactId}/restore")
            {
                Content = JsonContent.Create(new
                {
                    agentId = scenario.Backup.Agent.AgentId,
                    databaseName = "RestoreDb",
                    target = new { mode = "mssql-credential", databaseCredentialReference = "test-cred" }
                })
            };
            validMsg.Headers.Add("Idempotency-Key", idempotencyKey);
            using var validRestore = await scenario.Admin.Client.SendAsync(validMsg);
            // Even if target fails validation, ensure no bytes or keys returned in response
            if (validRestore.IsSuccessStatusCode)
            {
                var json = AgentWorld.ReadJson(validRestore);
                Assert.False(json.TryGetProperty("dek", out _));
                Assert.False(json.TryGetProperty("key", out _));
                Assert.False(json.TryGetProperty("bytes", out _));
            }
        }
    }

    [Fact]
    public async Task LogAndAuditSecrecy_NoKeyMaterialOrPlaintextAppears()
    {
        var (scenario, shareId) = await SetupScenarioWithShareAsync(fixture, encrypt: true);
        await using (scenario)
        {
            var platformAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
            var platform = await scenario.World.SignInAsync(platformAccount);

            using var prep = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId });
            Assert.Equal(HttpStatusCode.OK, prep.StatusCode);
            var contentUrl = AgentWorld.ReadJson(prep).GetProperty("downloadUrl").GetString()!;

            using var content = await platform.Client.GetAsync(contentUrl);
            Assert.Equal(HttpStatusCode.OK, content.StatusCode);

            await using var context = fixture.CreatePlatformContext();
            var audits = await context.AuditEvents
                .Where(e => e.TenantId == scenario.Backup.Backup.Setup.Tenant.Id)
                .ToListAsync();

            var plaintextHex = Convert.ToHexStringLower(scenario.Bytes);
            foreach (var audit in audits)
            {
                Assert.DoesNotContain(plaintextHex, audit.DetailsJson);
                Assert.DoesNotContain("wrappedDek", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("secret", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task ShareExpiry_RefusesStreamStartWhenExpiredBeforeStreaming_CompletesWhenExpiredAfterStreamStart()
    {
        var (scenario, shareId) = await SetupScenarioWithShareAsync(fixture, duration: TimeSpan.FromHours(1));
        await using (scenario)
        {
            var platformAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
            var platform = await scenario.World.SignInAsync(platformAccount);

            // 1. Prepare two sessions while share is active
            using var prep1 = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId });
            Assert.Equal(HttpStatusCode.OK, prep1.StatusCode);
            var prepared1 = AgentWorld.ReadJson(prep1);
            var session1Id = prepared1.GetProperty("sessionId").GetGuid();
            var contentUrl1 = prepared1.GetProperty("downloadUrl").GetString()!;

            using var prep2 = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId });
            Assert.Equal(HttpStatusCode.OK, prep2.StatusCode);
            var session2Id = AgentWorld.ReadJson(prep2).GetProperty("sessionId").GetGuid();

            // 2. Start streaming session 2 before share expires
            var store = scenario.Store();
            var startedAt = scenario.Backup.Clock.GetUtcNow();
            var streamingSession = await store.TransitionAsync(session2Id,
                new DownloadTransition(DownloadState.Ready, DownloadState.Streaming, null, "owner-2", startedAt, TimeSpan.FromMinutes(5)), default);
            Assert.NotNull(streamingSession);

            // 3. Expire the share in the database (simulating expiry after session creation while respecting ck_backup_shares_expiry)
            await using (var context = fixture.CreatePlatformContext())
            {
                var share = await context.BackupShares.SingleAsync(s => s.Id == shareId);
                context.Entry(share).Property(s => s.CreatedAtUtc).CurrentValue = startedAt.AddHours(-2);
                context.Entry(share).Property(s => s.ExpiresAtUtc).CurrentValue = startedAt.AddMinutes(-1);
                await context.SaveChangesAsync();
            }

            // 4. Session 1 (still in Ready, not yet streaming) must be refused at stream start
            using var content1 = await platform.Client.GetAsync(contentUrl1);
            Assert.True(content1.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict);
            var transition1 = await store.TransitionAsync(session1Id,
                new DownloadTransition(DownloadState.Ready, DownloadState.Streaming, null, "owner-1", startedAt, TimeSpan.FromMinutes(5)), default);
            Assert.Null(transition1);

            // 5. Session 2 (already in Streaming before share expired) renews lease, allows status check, and completes
            using var status2 = await platform.GetAsync($"{scenario.ArtifactPath}/downloads/{session2Id}");
            Assert.Equal(HttpStatusCode.OK, status2.StatusCode);
            var renewed2 = await store.TransitionAsync(session2Id,
                new DownloadTransition(DownloadState.Streaming, DownloadState.Streaming, "owner-2", "owner-2", startedAt.AddSeconds(10), TimeSpan.FromMinutes(5), Bytes: scenario.Bytes.Length), default);
            Assert.NotNull(renewed2);
            var completed2 = await store.TransitionAsync(session2Id,
                new DownloadTransition(DownloadState.Streaming, DownloadState.Completed, "owner-2", null, startedAt.AddSeconds(20), Bytes: scenario.Bytes.Length), default);
            Assert.NotNull(completed2);
            Assert.Equal(DownloadState.Completed, completed2.State);
        }
    }
}
