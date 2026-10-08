using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

public sealed class DownloadApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Theory]
    [InlineData(TenantRole.Owner)]
    [InlineData(TenantRole.Admin)]
    public async Task AdministratorDownloadsRealCentralBytesThroughANativeAttachment(TenantRole role)
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var account = await scenario.World.SeedUserAsync();
        await scenario.World.GrantAsync(scenario.Backup.Backup.Setup.Tenant, account, role);
        var browser = await scenario.World.SignInAsync(account);
        using var availability = await browser.GetAsync(scenario.ArtifactPath + "/download-availability");
        Assert.Equal(HttpStatusCode.OK, availability.StatusCode);
        var available = AgentWorld.ReadJson(availability);
        Assert.True(available.GetProperty("available").GetBoolean());
        Assert.Equal(scenario.Checksum, available.GetProperty("checksum").GetString());
        Assert.Equal(scenario.Bytes.Length, available.GetProperty("sizeBytes").GetInt64());
        var replica = Assert.Single(available.GetProperty("replicas").EnumerateArray());
        Assert.Equal(scenario.ReplicaId, replica.GetProperty("replicaId").GetGuid());
        Assert.True(replica.GetProperty("available").GetBoolean());
        var created = await scenario.CreateSessionAsync(browser);
        var sessionId = created.GetProperty("sessionId").GetGuid();
        var url = created.GetProperty("downloadUrl").GetString()!;
        Assert.Equal(scenario.ArtifactPath + $"/downloads/{sessionId}/content", url);
        Assert.Equal(scenario.Checksum, created.GetProperty("checksum").GetString());
        Assert.Equal(scenario.Bytes.Length, created.GetProperty("sizeBytes").GetInt64());
        Assert.EndsWith(".bak", created.GetProperty("filename").GetString());
        using var status = await browser.GetAsync(scenario.ArtifactPath + $"/downloads/{sessionId}");
        Assert.Equal("Ready", AgentWorld.ReadJson(status).GetProperty("state").GetString());
        using var content = await browser.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("attachment", content.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("application/octet-stream", content.Content.Headers.ContentType!.MediaType);
        Assert.Equal(scenario.Bytes.Length, content.Content.Headers.ContentLength);
        await using var stream = await content.Content.ReadAsStreamAsync();
        using var received = new MemoryStream();
        await stream.CopyToAsync(received);
        Assert.Equal(scenario.Bytes, received.ToArray());
        Assert.Equal(scenario.Checksum, Convert.ToHexStringLower(SHA256.HashData(received.ToArray())));
        var persisted = (await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, sessionId, default))!;
        Assert.Equal(DownloadState.Completed, persisted.State);
        Assert.Equal(scenario.Bytes.Length, persisted.TransferredBytes);
        Assert.NotNull(persisted.ReleasedAt);
        await using var context = fixture.CreatePlatformContext();
        var audit = await context.AuditEvents.SingleAsync(entry => entry.Action == "download.completed" && entry.TargetId == sessionId.ToString());
        Assert.Equal(account.UserId, audit.ActorUserId);
        using var details = JsonDocument.Parse(audit.DetailsJson);
        Assert.Equal(scenario.Bytes.Length, details.RootElement.GetProperty("transferredBytes").GetInt64());
        Assert.Equal("Completed", details.RootElement.GetProperty("result").GetString());
        Assert.Equal(persisted.ReleasedAt, details.RootElement.GetProperty("completedAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData(TenantRole.Operator)]
    [InlineData(TenantRole.Viewer)]
    public async Task LowerRolesCannotReadAvailabilityCreateStatusOrContent(TenantRole role)
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var account = await scenario.World.SeedUserAsync();
        await scenario.World.GrantAsync(scenario.Backup.Backup.Setup.Tenant, account, role);
        var browser = await scenario.World.SignInAsync(account);
        var created = await scenario.CreateSessionAsync();
        using var availability = await browser.GetAsync(scenario.ArtifactPath + "/download-availability");
        using var create = await scenario.CreateRawAsync(browser);
        using var status = await browser.GetAsync(scenario.ArtifactPath + $"/downloads/{created.GetProperty("sessionId").GetGuid()}");
        using var content = await browser.GetAsync(created.GetProperty("downloadUrl").GetString()!);
        Assert.All(new[] { availability, create, status, content }, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
    }

    [Fact]
    public async Task PlatformAdministratorHasNoTenantMembershipBypass()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var platform = await scenario.World.SignInAsync(await scenario.World.SeedUserAsync(platformAdmin: true));
        var created = await scenario.CreateSessionAsync();
        using var availability = await platform.GetAsync(scenario.ArtifactPath + "/download-availability");
        using var create = await scenario.CreateRawAsync(platform);
        using var status = await platform.GetAsync(scenario.ArtifactPath + $"/downloads/{created.GetProperty("sessionId").GetGuid()}");
        using var content = await platform.GetAsync(created.GetProperty("downloadUrl").GetString()!);
        Assert.All(new[] { availability, create, status, content }, response => Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode));
    }

    [Fact]
    public async Task SessionsAreBoundToCreatorAndTenantEvenForAnotherOwner()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var created = await scenario.CreateSessionAsync();
        var account = await scenario.World.SeedUserAsync();
        await scenario.World.GrantAsync(scenario.Backup.Backup.Setup.Tenant, account, TenantRole.Owner);
        var owner = await scenario.World.SignInAsync(account);
        var other = await scenario.World.CreateTenantAsync();
        var sessionId = created.GetProperty("sessionId").GetGuid();
        using var status = await owner.GetAsync(scenario.ArtifactPath + $"/downloads/{sessionId}");
        using var content = await owner.GetAsync(created.GetProperty("downloadUrl").GetString()!);
        using var foreignStatus = await other.Admin.GetAsync(BackupApiScenario.RootOf(other) + $"/backup-artifacts/{scenario.Accepted.ArtifactId}/downloads/{sessionId}");
        using var foreignContent = await other.Admin.GetAsync(BackupApiScenario.RootOf(other) + $"/backup-artifacts/{scenario.Accepted.ArtifactId}/downloads/{sessionId}/content");
        using var foreignArtifact = await scenario.CreateRawAsync(other.Admin, artifactPath: BackupApiScenario.RootOf(other) + $"/backup-artifacts/{scenario.Accepted.ArtifactId}");
        Assert.All(new[] { status, content, foreignStatus, foreignContent, foreignArtifact }, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.Equal("Ready", (await scenario.StatusAsync(sessionId)).GetProperty("state").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TamperedContentCannotComplete(bool changeSize)
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var created = await scenario.CreateSessionAsync();
        var damaged = scenario.Bytes.ToArray();
        damaged[0] ^= 0xff;
        await File.WriteAllBytesAsync(scenario.FilePath, changeSize ? damaged[..^1] : damaged);
        using var content = await scenario.Admin.GetAsync(created.GetProperty("downloadUrl").GetString()!);
        Assert.NotEqual(HttpStatusCode.OK, content.StatusCode);
        var status = await scenario.StatusAsync(created.GetProperty("sessionId").GetGuid());
        Assert.Equal("Failed", status.GetProperty("state").GetString());
        Assert.Equal("integrity_mismatch", status.GetProperty("failureCode").GetString());
        Assert.Equal(0, status.GetProperty("transferredBytes").GetInt64());
    }

    [Fact]
    public async Task AnonymousClientsCannotReadDownloadRoutes()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var created = await scenario.CreateSessionAsync();
        using var availability = await scenario.World.Anonymous.GetAsync(scenario.ArtifactPath + "/download-availability");
        using var status = await scenario.World.Anonymous.GetAsync(scenario.ArtifactPath + $"/downloads/{created.GetProperty("sessionId").GetGuid()}");
        using var content = await scenario.World.Anonymous.GetAsync(created.GetProperty("downloadUrl").GetString()!);
        Assert.All(new[] { availability, status, content }, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
    }
    [Theory]
    [InlineData("download.requested")]
    [InlineData("download.streaming")]
    public async Task AuditFailureRollsBackLifecycleBeforeAnyTransfer(string action)
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var created = action == "download.streaming" ? await scenario.CreateSessionAsync() : default;
        var name = "reject_download_" + Guid.NewGuid().ToString("N");
        await scenario.Backup.ExecuteAsync($"""
            CREATE FUNCTION {name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."TenantId" = '{scenario.Backup.TenantId}'::uuid AND NEW."Action" = '{action}' THEN
                    RAISE EXCEPTION 'Download test audit failure';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER {name} BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION {name}();
            """);
        try
        {
            using var response = action == "download.requested"
                ? await scenario.CreateRawAsync()
                : await scenario.Admin.GetAsync(created.GetProperty("downloadUrl").GetString()!);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Null(response.Content.Headers.ContentDisposition);
            Assert.Equal("download_unavailable", AgentWorld.ReadJson(response).GetProperty("error").GetString());
            await using var context = fixture.CreatePlatformContext();
            Assert.False(await context.AuditEvents.AnyAsync(audit => audit.TenantId == scenario.Backup.Backup.Setup.Tenant.Id && audit.Action == action));
            if (action == "download.streaming")
            {
                var session = (await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, created.GetProperty("sessionId").GetGuid(), default))!;
                Assert.Equal(DownloadState.Ready, session.State);
                Assert.Equal(0, session.TransferredBytes);
                Assert.Null(session.LeaseOwner);
                Assert.Null(session.ReleasedAt);
            }
            else
            {
                Assert.Equal(0L, await new RestoreWorld(fixture).ScalarAsync<long>(
                    "SELECT count(*) FROM download_sessions WHERE \"ArtifactId\" = @id", ("id", scenario.Accepted.ArtifactId)));
            }
        }
        finally
        {
            await scenario.Backup.ExecuteAsync($"DROP TRIGGER {name} ON audit_events; DROP FUNCTION {name}();");
        }
    }

    [Fact]
    public async Task ForeignReplicaAndSoftDeletedArtifactCannotStartNewDownloads()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var other = await scenario.Backup.AcceptBackupAsync();
        var artifact = await scenario.Backup.ArtifactAsync(other.ArtifactId);
        var foreignReplica = RetentionApiSupport.ReplicaOf(artifact).GetProperty("id").GetGuid();
        using var mismatched = await scenario.CreateRawAsync(replicaId: foreignReplica);
        Assert.Equal(HttpStatusCode.NotFound, mismatched.StatusCode);
        var existing = await scenario.CreateSessionAsync();
        var sessionId = existing.GetProperty("sessionId").GetGuid();
        using var wrongArtifactStatus = await scenario.Admin.GetAsync(scenario.Root + $"/backup-artifacts/{other.ArtifactId}/downloads/{sessionId}");
        using var wrongArtifactContent = await scenario.Admin.GetAsync(scenario.Root + $"/backup-artifacts/{other.ArtifactId}/downloads/{sessionId}/content");
        Assert.Equal(HttpStatusCode.NotFound, wrongArtifactStatus.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongArtifactContent.StatusCode);
        await new RetentionWorld(fixture).Store().RequestDeleteAsync(scenario.Backup.Backup.Setup.Tenant.Id, scenario.Accepted.ArtifactId,
            scenario.Backup.Backup.Setup.Account.UserId.Value, scenario.Backup.Clock.GetUtcNow(), default);
        using var availability = await scenario.Admin.GetAsync(scenario.ArtifactPath + "/download-availability");
        Assert.Equal(HttpStatusCode.OK, availability.StatusCode);
        Assert.False(AgentWorld.ReadJson(availability).GetProperty("available").GetBoolean());
        using var rejected = await scenario.CreateRawAsync();
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        using var content = await scenario.Admin.GetAsync(existing.GetProperty("downloadUrl").GetString()!);
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal(scenario.Bytes, await content.Content.ReadAsByteArrayAsync());
    }
}

