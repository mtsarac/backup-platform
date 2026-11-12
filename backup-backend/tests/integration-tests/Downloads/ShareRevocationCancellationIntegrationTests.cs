using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BackupPlatform.Api.Downloads;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Downloads;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BackupPlatform.IntegrationTests;

public sealed class ShareRevocationCancellationIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static async Task<Guid> CreateShareAsync(DownloadApiScenario scenario, TimeSpan? duration = null)
    {
        using var shareResponse = await scenario.Admin.SendAsync(
            HttpMethod.Post,
            $"{scenario.Root}/backup-artifacts/{scenario.Accepted.ArtifactId}/shares",
            new { reason = "Revocation cancellation test share", durationHours = (int)(duration ?? TimeSpan.FromHours(24)).TotalHours });
        Assert.Equal(HttpStatusCode.Created, shareResponse.StatusCode);
        return AgentWorld.ReadJson(shareResponse).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task InFlightStreaming_SameInstanceRevocation_StopsWithinSignalledBound_AndAuditsPartialBytes()
    {
        var gate = new StreamGate(firstChunkBytes: 256);
        var Gates = new ConcurrentQueue<StreamGate>([gate]);
        await using var scenario = await CreateGatedScenarioAsync(fixture, Gates);
        var shareId = await CreateShareAsync(scenario);
        var platformAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
        var platform = await scenario.World.SignInAsync(platformAccount);

        using var prep = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId });
        Assert.Equal(HttpStatusCode.OK, prep.StatusCode);
        var prepared = AgentWorld.ReadJson(prep);
        var sessionId = prepared.GetProperty("sessionId").GetGuid();
        var contentUrl = prepared.GetProperty("downloadUrl").GetString()!;

        var registry = scenario.World.Factory.Services.GetRequiredService<ActiveDownloadRegistry>();
        Assert.Equal(0, registry.Count);

        var streamTask = Task.Run(async () =>
        {
            try
            {
                using var resp = await platform.Client.GetAsync(contentUrl);
            }
            catch
            {
                // Expected when response stream is aborted on revocation cancellation.
            }
        });

        await gate.FirstChunkTransferred.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, registry.Count);
        Assert.True(registry.ContainsSession(sessionId));

        var sw = Stopwatch.StartNew();
        using var revoke = await scenario.Admin.SendAsync(
            HttpMethod.Post,
            $"{scenario.Root}/backup-shares/{shareId}/revoke",
            new { reason = "Immediate revocation during streaming" });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        await streamTask.WaitAsync(TimeSpan.FromSeconds(3));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Expected stream to stop within 3s signalled bound, took {sw.Elapsed}.");
        Assert.Equal(0, registry.Count);
        Assert.False(registry.ContainsSession(sessionId));

        var persisted = (await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, sessionId, default))!;
        Assert.Equal(DownloadState.Cancelled, persisted.State);
        Assert.Equal("cancelled_by_revocation", persisted.FailureCode);
        Assert.Equal(256, persisted.TransferredBytes);
        Assert.NotNull(persisted.ReleasedAt);

        using var statusResp = await platform.GetAsync($"{scenario.ArtifactPath}/downloads/{sessionId}");
        Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);
        var statusJson = AgentWorld.ReadJson(statusResp);
        Assert.Equal("Cancelled", statusJson.GetProperty("state").GetString());
        Assert.Equal("cancelled_by_revocation", statusJson.GetProperty("failureCode").GetString());
        Assert.Equal(256, statusJson.GetProperty("transferredBytes").GetInt64());

        await using var context = fixture.CreatePlatformContext();
        var audit = await context.AuditEvents
            .SingleAsync(e => e.Action == "download.cancelled" && e.TargetId == sessionId.ToString("D"));
        Assert.Equal(AuditActorKind.PlatformAdmin, audit.ActorKind);
        Assert.Equal(platformAccount.UserId, audit.ActorUserId);
        using var details = JsonDocument.Parse(audit.DetailsJson);
        Assert.Equal("cancelled by revocation", details.RootElement.GetProperty("result").GetString());
        Assert.Equal(256, details.RootElement.GetProperty("bytesTransferred").GetInt64());
        Assert.Equal(256, details.RootElement.GetProperty("transferredBytes").GetInt64());
        Assert.Equal(persisted.ReleasedAt, details.RootElement.GetProperty("endTime").GetDateTimeOffset());
        Assert.Equal(persisted.ReleasedAt, details.RootElement.GetProperty("completedAt").GetDateTimeOffset());
        Assert.Equal(shareId, details.RootElement.GetProperty("shareId").GetGuid());
        Assert.Equal("cancelled_by_revocation", details.RootElement.GetProperty("failureReason").GetString());
    }

    [Fact]
    public async Task CrossInstanceRevocation_TwoApiHostsOnOneDatabase_StopsViaListenNotifyWithinSignalledBound()
    {
        var gate = new StreamGate(firstChunkBytes: 300);
        var gates = new ConcurrentQueue<StreamGate>([gate]);
        // Set ReconciliationInterval to 60s on Host A so only LISTEN/NOTIFY can stop the stream within the 3s bound.
        await using var hostAScenario = await CreateGatedScenarioAsync(fixture, gates, new Dictionary<string, string?>
        {
            ["Downloads:ShareRevocation:EnableListenNotify"] = "true",
            ["Downloads:ShareRevocation:ReconciliationInterval"] = "00:01:00"
        });

        await using var hostBWorld = await AgentWorld.CreateAsync(fixture, clock: hostAScenario.Backup.Clock, configuration: new Dictionary<string, string?>
        {
            ["Backups:Storage:Central:RootDirectory"] = hostAScenario.DirectoryPath,
            [$"Backups:Storage:Central:TenantScopes:{hostAScenario.Backup.TenantId}"] = "download-tenant",
            ["Downloads:ShareRevocation:EnableListenNotify"] = "true",
            ["Downloads:ShareRevocation:ReconciliationInterval"] = "00:01:00"
        });
        var adminOnHostB = await hostBWorld.SignInAsync(hostAScenario.Backup.Backup.Setup.Account);

        var shareId = await CreateShareAsync(hostAScenario);
        var platformAccount = await hostAScenario.World.SeedUserAsync(platformAdmin: true);
        var platformOnHostA = await hostAScenario.World.SignInAsync(platformAccount);

        using var prep = await platformOnHostA.SendAsync(HttpMethod.Post, $"{hostAScenario.ArtifactPath}/downloads", new { shareId });
        Assert.Equal(HttpStatusCode.OK, prep.StatusCode);
        var prepared = AgentWorld.ReadJson(prep);
        var sessionId = prepared.GetProperty("sessionId").GetGuid();
        var contentUrl = prepared.GetProperty("downloadUrl").GetString()!;

        var hostARegistry = hostAScenario.World.Factory.Services.GetRequiredService<ActiveDownloadRegistry>();
        var hostBRegistry = hostBWorld.Factory.Services.GetRequiredService<ActiveDownloadRegistry>();
        Assert.NotSame(hostARegistry, hostBRegistry);

        var streamTask = Task.Run(async () =>
        {
            try
            {
                using var resp = await platformOnHostA.Client.GetAsync(contentUrl);
            }
            catch
            {
            }
        });

        await gate.FirstChunkTransferred.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, hostARegistry.Count);
        Assert.Equal(0, hostBRegistry.Count);

        // Revoke the share on Host B while the stream is running on Host A.
        var sw = Stopwatch.StartNew();
        using var revokeOnHostB = await adminOnHostB.SendAsync(
            HttpMethod.Post,
            $"{hostAScenario.Root}/backup-shares/{shareId}/revoke",
            new { reason = "Cross-instance revocation from Host B" });
        Assert.Equal(HttpStatusCode.OK, revokeOnHostB.StatusCode);

        await streamTask.WaitAsync(TimeSpan.FromSeconds(3));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Expected cross-node LISTEN/NOTIFY cancellation within 3s, took {sw.Elapsed}.");
        Assert.Equal(0, hostARegistry.Count);

        var persisted = (await hostAScenario.Store().GetAsync(hostAScenario.Backup.Backup.Setup.Tenant.Id, sessionId, default))!;
        Assert.Equal(DownloadState.Cancelled, persisted.State);
        Assert.Equal("cancelled_by_revocation", persisted.FailureCode);
        Assert.Equal(300, persisted.TransferredBytes);
        Assert.NotNull(persisted.ReleasedAt);

        await using var context = fixture.CreatePlatformContext();
        var audit = await context.AuditEvents
            .SingleAsync(e => e.Action == "download.cancelled" && e.TargetId == sessionId.ToString("D"));
        using var details = JsonDocument.Parse(audit.DetailsJson);
        Assert.Equal("cancelled by revocation", details.RootElement.GetProperty("result").GetString());
        Assert.Equal(300, details.RootElement.GetProperty("bytesTransferred").GetInt64());
    }

    [Fact]
    public async Task MissedListenNotify_RecoveredByReconciliationWithinReconciliationBound()
    {
        var gate = new StreamGate(firstChunkBytes: 128);
        var gates = new ConcurrentQueue<StreamGate>([gate]);
        // Disable LISTEN/NOTIFY on Host A and configure a 200ms reconciliation interval.
        await using var hostAScenario = await CreateGatedScenarioAsync(fixture, gates, new Dictionary<string, string?>
        {
            ["Downloads:ShareRevocation:EnableListenNotify"] = "false",
            ["Downloads:ShareRevocation:ReconciliationInterval"] = "00:00:00.200"
        });

        var shareId = await CreateShareAsync(hostAScenario);
        var platformAccount = await hostAScenario.World.SeedUserAsync(platformAdmin: true);
        var platformOnHostA = await hostAScenario.World.SignInAsync(platformAccount);

        using var prep = await platformOnHostA.SendAsync(HttpMethod.Post, $"{hostAScenario.ArtifactPath}/downloads", new { shareId });
        Assert.Equal(HttpStatusCode.OK, prep.StatusCode);
        var prepared = AgentWorld.ReadJson(prep);
        var sessionId = prepared.GetProperty("sessionId").GetGuid();
        var contentUrl = prepared.GetProperty("downloadUrl").GetString()!;

        var hostARegistry = hostAScenario.World.Factory.Services.GetRequiredService<ActiveDownloadRegistry>();

        var streamTask = Task.Run(async () =>
        {
            try
            {
                using var resp = await platformOnHostA.Client.GetAsync(contentUrl);
            }
            catch
            {
            }
        });

        await gate.FirstChunkTransferred.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, hostARegistry.Count);

        // Simulate a revoke on another node whose NOTIFY was dropped by updating RevokedAtUtc directly in PostgreSQL without calling Host A's registry.
        var sw = Stopwatch.StartNew();
        var revokedAt = hostAScenario.Backup.Clock.GetUtcNow();
        await using (var db = fixture.CreatePlatformContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE backup_shares
                SET "RevokedAtUtc" = {revokedAt}, "RevokedByUserId" = {hostAScenario.Backup.Backup.Setup.Account.UserId.Value}
                WHERE "Id" = {shareId}
                """);
        }

        await streamTask.WaitAsync(TimeSpan.FromSeconds(2));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Expected reconciliation to cancel stream within bound, took {sw.Elapsed}.");
        Assert.Equal(0, hostARegistry.Count);

        var persisted = (await hostAScenario.Store().GetAsync(hostAScenario.Backup.Backup.Setup.Tenant.Id, sessionId, default))!;
        Assert.Equal(DownloadState.Cancelled, persisted.State);
        Assert.Equal("cancelled_by_revocation", persisted.FailureCode);
        Assert.Equal(128, persisted.TransferredBytes);
        Assert.NotNull(persisted.ReleasedAt);
    }

    [Fact]
    public async Task MultipleSharesForSameArtifact_OnlyRevokedShareStreamStops_OtherShareCompletes()
    {
        var gateA = new StreamGate(firstChunkBytes: 256);
        var gateB = new StreamGate(firstChunkBytes: 256);
        var gates = new ConcurrentQueue<StreamGate>([gateA, gateB]);
        await using var scenario = await CreateGatedScenarioAsync(fixture, gates);

        var shareA = await CreateShareAsync(scenario);
        var shareB = await CreateShareAsync(scenario);

        var platformAccountA = await scenario.World.SeedUserAsync(platformAdmin: true);
        var platformA = await scenario.World.SignInAsync(platformAccountA);
        var platformAccountB = await scenario.World.SeedUserAsync(platformAdmin: true);
        var platformB = await scenario.World.SignInAsync(platformAccountB);

        using var prepA = await platformA.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId = shareA });
        Assert.Equal(HttpStatusCode.OK, prepA.StatusCode);
        var preparedA = AgentWorld.ReadJson(prepA);
        var sessionA = preparedA.GetProperty("sessionId").GetGuid();
        var urlA = preparedA.GetProperty("downloadUrl").GetString()!;

        using var prepB = await platformB.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId = shareB });
        Assert.Equal(HttpStatusCode.OK, prepB.StatusCode);
        var preparedB = AgentWorld.ReadJson(prepB);
        var sessionB = preparedB.GetProperty("sessionId").GetGuid();
        var urlB = preparedB.GetProperty("downloadUrl").GetString()!;

        var streamATask = Task.Run(async () =>
        {
            try
            {
                using var resp = await platformA.Client.GetAsync(urlA);
            }
            catch
            {
            }
        });
        await gateA.FirstChunkTransferred.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var streamBTask = Task.Run(async () => await platformB.Client.GetAsync(urlB));
        await gateB.FirstChunkTransferred.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var registry = scenario.World.Factory.Services.GetRequiredService<ActiveDownloadRegistry>();
        Assert.Equal(2, registry.Count);

        // Revoke Share A only.
        using var revokeA = await scenario.Admin.SendAsync(
            HttpMethod.Post,
            $"{scenario.Root}/backup-shares/{shareA}/revoke",
            new { reason = "Revoke only Share A" });
        Assert.Equal(HttpStatusCode.OK, revokeA.StatusCode);

        await streamATask.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(registry.ContainsSession(sessionA));
        Assert.True(registry.ContainsSession(sessionB));

        // Release Share B's remaining bytes so it can finish.
        gateB.ReleaseRemainder.TrySetResult();
        using var responseB = await streamBTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, responseB.StatusCode);
        var bytesB = await responseB.Content.ReadAsByteArrayAsync();
        Assert.Equal(scenario.Bytes, bytesB);

        Assert.Equal(0, registry.Count);

        var persistedA = (await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, sessionA, default))!;
        Assert.Equal(DownloadState.Cancelled, persistedA.State);
        Assert.Equal("cancelled_by_revocation", persistedA.FailureCode);
        Assert.Equal(256, persistedA.TransferredBytes);

        var persistedB = (await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, sessionB, default))!;
        Assert.Equal(DownloadState.Completed, persistedB.State);
        Assert.Equal(scenario.Bytes.Length, persistedB.TransferredBytes);
    }

    [Fact]
    public async Task RevokeVsDownloadStartRace_EndsInRefusedOrStartedAndCancelled_NeverUncancellable()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var platformAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
        var platform = await scenario.World.SignInAsync(platformAccount);
        var registry = scenario.World.Factory.Services.GetRequiredService<ActiveDownloadRegistry>();

        for (var iteration = 0; iteration < 6; iteration++)
        {
            var shareId = await CreateShareAsync(scenario);
            using var prep = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId });
            Assert.Equal(HttpStatusCode.OK, prep.StatusCode);
            var prepared = AgentWorld.ReadJson(prep);
            var sessionId = prepared.GetProperty("sessionId").GetGuid();
            var contentUrl = prepared.GetProperty("downloadUrl").GetString()!;

            var raceGate = new RaceGate();
            var revokeTask = raceGate.Run(() => scenario.Admin.SendAsync(
                HttpMethod.Post,
                $"{scenario.Root}/backup-shares/{shareId}/revoke",
                new { reason = $"Race iteration {iteration}" }));
            var startTask = raceGate.Run(async () =>
            {
                try
                {
                    using var resp = await platform.Client.GetAsync(contentUrl);
                    return (HttpStatusCode?)resp.StatusCode;
                }
                catch
                {
                    return null;
                }
            });

            raceGate.Open();
            using var revokeResp = await revokeTask;
            Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);
            var statusCode = await startTask.WaitAsync(TimeSpan.FromSeconds(5));

            var persisted = (await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, sessionId, default))!;
            Assert.NotEqual(DownloadState.Completed, persisted.State);
            Assert.NotEqual(DownloadState.Streaming, persisted.State);
            Assert.True(
                (persisted.State == DownloadState.Ready && persisted.TransferredBytes == 0 && statusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict)
                || (persisted.State == DownloadState.Cancelled && persisted.FailureCode == "cancelled_by_revocation"),
                $"Unexpected race outcome: State={persisted.State}, FailureCode={persisted.FailureCode}, StatusCode={statusCode}");
            Assert.Equal(0, registry.Count);
        }
    }

    [Fact]
    public async Task ExpiryDuringStreaming_DoesNotCancelStream_CompletesSuccessfully()
    {
        var gate = new StreamGate(firstChunkBytes: 256);
        var gates = new ConcurrentQueue<StreamGate>([gate]);
        await using var scenario = await CreateGatedScenarioAsync(fixture, gates, new Dictionary<string, string?>
        {
            ["Downloads:ShareRevocation:ReconciliationInterval"] = "00:00:00.150"
        });

        var shareId = await CreateShareAsync(scenario, TimeSpan.FromHours(1));
        var platformAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
        var platform = await scenario.World.SignInAsync(platformAccount);

        using var prep = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId });
        Assert.Equal(HttpStatusCode.OK, prep.StatusCode);
        var prepared = AgentWorld.ReadJson(prep);
        var sessionId = prepared.GetProperty("sessionId").GetGuid();
        var contentUrl = prepared.GetProperty("downloadUrl").GetString()!;

        var streamTask = Task.Run(async () => await platform.Client.GetAsync(contentUrl));
        await gate.FirstChunkTransferred.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Expire the share in the database while the stream is actively running.
        var now = scenario.Backup.Clock.GetUtcNow();
        await using (var context = fixture.CreatePlatformContext())
        {
            var share = await context.BackupShares.SingleAsync(s => s.Id == shareId);
            context.Entry(share).Property(s => s.CreatedAtUtc).CurrentValue = now.AddHours(-2);
            context.Entry(share).Property(s => s.ExpiresAtUtc).CurrentValue = now.AddMinutes(-5);
            await context.SaveChangesAsync();
        }

        // Run reconciliation explicitly and allow background reconciliation ticks to run while expired.
        var worker = scenario.World.Factory.Services.GetRequiredService<ShareRevocationCancellationWorker>();
        await worker.ReconcileOnceAsync(default);
        await Task.Delay(250);

        var registry = scenario.World.Factory.Services.GetRequiredService<ActiveDownloadRegistry>();
        Assert.True(registry.ContainsSession(sessionId));

        gate.ReleaseRemainder.TrySetResult();
        using var response = await streamTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(scenario.Bytes, await response.Content.ReadAsByteArrayAsync());

        var persisted = (await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, sessionId, default))!;
        Assert.Equal(DownloadState.Completed, persisted.State);
        Assert.Equal(scenario.Bytes.Length, persisted.TransferredBytes);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task AgentDirectDownload_CancelledOnShareRevocation_RefusesLeaseKeyAndStorageAccess_AndAudits()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        await scenario.EncryptAsync();
        var connectionId = Guid.NewGuid();
        await using (var context = scenario.World.Database.CreatePlatformContext())
        {
            context.StorageConnections.Add(new StorageConnection(connectionId, scenario.Backup.Backup.Setup.Tenant.Id, "Direct sftp", StorageProviderTypes.Sftp,
                StorageOwnership.TenantManaged, StorageQuotaScopes.Tenant, new Dictionary<string, string>(), false, scenario.Backup.Clock.GetUtcNow()));
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE backup_replicas SET "StorageProvider" = 'sftp', "StorageTarget" = {connectionId.ToString("D")}, "StorageConnectionId" = {connectionId} WHERE "Id" = {scenario.ReplicaId}
                """);
        }

        using (var heartbeat = await scenario.World.HeartbeatAsync(scenario.Backup.Agent))
            Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        using (var capability = await scenario.World.SyncCapabilitiesAsync(scenario.Backup.Agent, AgentWorld.CapabilitySnapshot(
            AgentWorld.CapabilityReport(DownloadTaskTypes.Capability), AgentWorld.CapabilityReport(TaskStorageProtocol.DirectCapability))))
            Assert.Equal(HttpStatusCode.OK, capability.StatusCode);

        var shareId = await CreateShareAsync(scenario);
        var platformAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
        var platform = await scenario.World.SignInAsync(platformAccount);

        using var prep = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId });
        Assert.Equal(HttpStatusCode.OK, prep.StatusCode);
        var prepared = AgentWorld.ReadJson(prep);
        Assert.True(prepared.GetProperty("direct").GetBoolean());
        var sessionId = prepared.GetProperty("sessionId").GetGuid();
        var contentUrl = prepared.GetProperty("downloadUrl").GetString()!;

        using var offer = await platform.SendAsync(HttpMethod.Post, contentUrl, new { sdp = DownloadAgentApiTests.Sdp });
        Assert.Equal(HttpStatusCode.OK, offer.StatusCode);

        var task = await DownloadAgentApiTests.ClaimAsync(scenario);
        var leaseToken = task.GetProperty("leaseToken").GetGuid();

        // Verify lease renewal succeeds before revocation.
        using var initialRenew = await scenario.World.SendAgentAsync(
            HttpMethod.Post, $"/api/agent/downloads/{sessionId}/lease",
            scenario.Backup.Agent.CredentialId, scenario.Backup.Agent.Secret,
            body: new { leaseToken });
        Assert.Equal(HttpStatusCode.OK, initialRenew.StatusCode);

        // Revoke the share.
        using var revoke = await scenario.Admin.SendAsync(
            HttpMethod.Post,
            $"{scenario.Root}/backup-shares/{shareId}/revoke",
            new { reason = "Revoke during agent-direct download" });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        // 1. Lease renewal is refused.
        using var refusedRenew = await scenario.World.SendAgentAsync(
            HttpMethod.Post, $"/api/agent/downloads/{sessionId}/lease",
            scenario.Backup.Agent.CredentialId, scenario.Backup.Agent.Secret,
            body: new { leaseToken });
        Assert.Equal(HttpStatusCode.Conflict, refusedRenew.StatusCode);

        // 2. Artifact key unwrap is refused.
        using var refusedKey = await scenario.World.SendAgentAsync(
            HttpMethod.Post, $"/api/agent/downloads/{sessionId}/artifact-key",
            scenario.Backup.Agent.CredentialId, scenario.Backup.Agent.Secret,
            body: new { leaseToken });
        Assert.Equal(HttpStatusCode.Conflict, refusedKey.StatusCode);

        // 3. Storage access is refused.
        using var accessRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/agent/downloads/{sessionId}/storage/access");
        accessRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "BackupAgent", $"{scenario.Backup.Agent.CredentialId:N}.{scenario.Backup.Agent.Secret}");
        accessRequest.Headers.TryAddWithoutValidation("X-Backup-Agent-Protocol", "1.0");
        accessRequest.Headers.TryAddWithoutValidation(TaskStorageProtocol.LeaseHeader, leaseToken.ToString("D"));
        using var refusedAccess = await scenario.World.Anonymous.SendAsync(accessRequest);
        Assert.Equal(HttpStatusCode.Conflict, refusedAccess.StatusCode);

        // 4. Signaling is refused.
        using var refusedSignal = await scenario.World.SendAgentAsync(
            HttpMethod.Post, $"/api/agent/downloads/{sessionId}/signal",
            scenario.Backup.Agent.CredentialId, scenario.Backup.Agent.Secret,
            body: new { leaseToken, sdp = DownloadAgentApiTests.Sdp });
        Assert.Equal(HttpStatusCode.Conflict, refusedSignal.StatusCode);

        // 5. Session is in terminal Cancelled state with cancelled_by_revocation and audited.
        var persisted = (await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, sessionId, default))!;
        Assert.Equal(DownloadState.Cancelled, persisted.State);
        Assert.Equal("cancelled_by_revocation", persisted.FailureCode);
        Assert.NotNull(persisted.ReleasedAt);

        await using var auditContext = fixture.CreatePlatformContext();
        var audit = await auditContext.AuditEvents
            .SingleAsync(e => e.Action == "download.cancelled" && e.TargetId == sessionId.ToString("D"));
        using var details = JsonDocument.Parse(audit.DetailsJson);
        Assert.Equal("cancelled by revocation", details.RootElement.GetProperty("result").GetString());
        Assert.Equal(0L, details.RootElement.GetProperty("bytesTransferred").GetInt64());
        Assert.Equal(persisted.ReleasedAt, details.RootElement.GetProperty("endTime").GetDateTimeOffset());
    }

    [Fact]
    public async Task RegistryCleanup_NoEntriesRemainAfterCompletionFailureClientAbortOrRevocationCancellation()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var registry = scenario.World.Factory.Services.GetRequiredService<ActiveDownloadRegistry>();
        var platformAccount = await scenario.World.SeedUserAsync(platformAdmin: true);
        var platform = await scenario.World.SignInAsync(platformAccount);
        var shareId = await CreateShareAsync(scenario);

        // 1. Completion exit path
        using (var prep1 = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId }))
        {
            var url1 = AgentWorld.ReadJson(prep1).GetProperty("downloadUrl").GetString()!;
            using var resp1 = await platform.Client.GetAsync(url1);
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
            Assert.Equal(0, registry.Count);
        }

        // 2. Client abort exit path
        using (var prep2 = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId }))
        {
            var id2 = AgentWorld.ReadJson(prep2).GetProperty("sessionId").GetGuid();
            var session2 = (await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, id2, default))!;
            using var abortCts = new CancellationTokenSource();
            var http2 = new DefaultHttpContext { RequestAborted = abortCts.Token };
            http2.Response.Body = new AbortOnWriteStream(abortCts);
            using var scope2 = scenario.World.Factory.Services.CreateScope();
            await scope2.ServiceProvider.GetRequiredService<DownloadStreamer>().StreamAsync(http2, session2);
            Assert.Equal(0, registry.Count);
            Assert.False(registry.ContainsSession(id2));
        }

        // 3. Failure exit path (corrupted storage file)
        var originalBytes = await File.ReadAllBytesAsync(scenario.FilePath);
        try
        {
            var corrupted = originalBytes.ToArray();
            corrupted[0] ^= 0xFF;
            await File.WriteAllBytesAsync(scenario.FilePath, corrupted);
            using var prep3 = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId });
            var url3 = AgentWorld.ReadJson(prep3).GetProperty("downloadUrl").GetString()!;
            using var resp3 = await platform.Client.GetAsync(url3);
            Assert.NotEqual(HttpStatusCode.OK, resp3.StatusCode);
            Assert.Equal(0, registry.Count);
        }
        finally
        {
            await File.WriteAllBytesAsync(scenario.FilePath, originalBytes);
        }

        // 4. Refused stream start exit path (already transitioned / revoked)
        using (var prep4 = await platform.SendAsync(HttpMethod.Post, $"{scenario.ArtifactPath}/downloads", new { shareId }))
        {
            var id4 = AgentWorld.ReadJson(prep4).GetProperty("sessionId").GetGuid();
            var session4 = (await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, id4, default))!;
            using var revoke = await scenario.Admin.SendAsync(HttpMethod.Post, $"{scenario.Root}/backup-shares/{shareId}/revoke", new { });
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

            var http4 = new DefaultHttpContext();
            http4.Response.Body = new MemoryStream();
            using var scope4 = scenario.World.Factory.Services.CreateScope();
            await scope4.ServiceProvider.GetRequiredService<DownloadStreamer>().StreamAsync(http4, session4);
            Assert.Equal(StatusCodes.Status409Conflict, http4.Response.StatusCode);
            Assert.Equal(0, registry.Count);
            Assert.False(registry.ContainsSession(id4));
        }
    }

    private static Task<DownloadApiScenario> CreateGatedScenarioAsync(
        PostgreSqlFixture fixture,
        ConcurrentQueue<StreamGate> gates,
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        return DownloadApiScenario.CreateAsync(
            fixture,
            configureServices: services =>
            {
                var existing = services.Single(d => d.ServiceType == typeof(IBackupStorageResolver));
                services.RemoveAll<IBackupStorageResolver>();
                services.AddSingleton<IBackupStorageResolver>(provider =>
                {
                    var inner = (IBackupStorageResolver)ActivatorUtilities.CreateInstance(provider, existing.ImplementationType!);
                    return new GatedBackupStorageResolver(inner, gates);
                });
            },
            configuration: configuration);
    }

    private sealed class StreamGate(int firstChunkBytes)
    {
        public int FirstChunkBytes { get; } = firstChunkBytes;
        public TaskCompletionSource FirstChunkTransferred { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRemainder { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class GatedBackupStorageResolver(IBackupStorageResolver inner, ConcurrentQueue<StreamGate> gates) : IBackupStorageResolver
    {
        public IReadOnlyList<BackupManagedStorage> ManagedStorages => inner.ManagedStorages;

        public IStorageProvider? Resolve(TenantId tenantId, string providerType, string target)
        {
            var provider = inner.Resolve(tenantId, providerType, target);
            return provider is null ? null : new GatedStorageProvider(provider, gates);
        }
    }

    private sealed class GatedStorageProvider(IStorageProvider inner, ConcurrentQueue<StreamGate> gates) : IStorageProvider
    {
        public StorageProviderId Id => inner.Id;
        public StorageTopology Topology => inner.Topology;
        public StorageCapabilities Capabilities => inner.Capabilities;
        public Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken) =>
            inner.PutAsync(key, content, options, progress, cancellationToken);
        public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken) => inner.ExistsAsync(key, cancellationToken);
        public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken) => inner.GetMetadataAsync(key, cancellationToken);
        public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken) =>
            inner.VerifyAsync(key, expected, cancellationToken);
        public Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);
        public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) =>
            inner.EnumerateAsync(prefix, modifiedBefore, cancellationToken);

        public async Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            var innerStream = await inner.OpenReadAsync(key, cancellationToken);
            if (!gates.TryDequeue(out var gate))
            {
                return innerStream;
            }
            return new GatedReadStream(innerStream, gate);
        }
    }

    private sealed class GatedReadStream(Stream inner, StreamGate gate) : Stream
    {
        private int _readCount;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var step = Interlocked.Increment(ref _readCount);
            if (step == 1)
            {
                var slice = buffer.Slice(0, Math.Min(buffer.Length, gate.FirstChunkBytes));
                return await inner.ReadAsync(slice, cancellationToken);
            }
            if (step == 2)
            {
                gate.FirstChunkTransferred.TrySetResult();
                await gate.ReleaseRemainder.Task.WaitAsync(cancellationToken);
            }
            return await inner.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }

    private sealed class AbortOnWriteStream(CancellationTokenSource abortCts) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            abortCts.Cancel();
            return ValueTask.FromCanceled(abortCts.Token);
        }
    }
}
