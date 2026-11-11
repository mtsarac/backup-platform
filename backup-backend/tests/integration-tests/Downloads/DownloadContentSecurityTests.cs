using System.Net;
using System.Security.Cryptography;
using System.Text;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// ART-02: the content GET of an API-streamed download starts the transfer (Ready to Streaming), so it must not be reachable without the owner's own cookie, and a session must
/// serve its bytes at most once. The control is the session cookie (SameSite=Strict, see AuthIntegrationTests), the unguessable session id bound to tenant, artifact, owner and
/// channel, and the single-use state machine; there is no separate ticket. The cross-site half is proven in a real browser by the download-security lab script.
/// </summary>
public sealed class DownloadContentSecurityTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task EveryCallerThatIsNotTheOwnerGetsNoBytesAndNoStorageDetailsAndTheSessionStaysReady()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var created = await scenario.CreateSessionAsync();
        var id = created.GetProperty("sessionId").GetGuid();
        var url = created.GetProperty("downloadUrl").GetString()!;
        var otherTenant = await scenario.World.CreateTenantAsync();
        var platformSession = await SignInAsync(scenario, member: false, platformAdmin: true);
        var sameTenantMember = await SignInAsync(scenario, member: true);
        var callers = new (string Name, HttpClient Client, HttpStatusCode Expected)[]
        {
            ("anonymous", scenario.World.Anonymous, HttpStatusCode.Unauthorized),
            ("same-tenant member (not session owner)", sameTenantMember.Client, HttpStatusCode.NotFound),
            ("platform admin", platformSession.Client, HttpStatusCode.Forbidden),
            ("administrator of another tenant", otherTenant.Admin.Client, HttpStatusCode.Forbidden)
        };
        foreach (var (name, client, expected) in callers)
        {
            using var response = await client.GetAsync(url);
            Assert.True(response.StatusCode == expected, $"{name}: expected {expected} but got {response.StatusCode}");
            await AssertNoLeakAsync(scenario, response);
        }
        Assert.Equal(DownloadState.Ready, await StateAsync(scenario, id));
        Assert.Equal(0, await AuditCountAsync(id, "download.streaming"));
        await AssertOwnerDownloadsAsync(scenario, url);
    }

    [Fact]
    public async Task ARemovedMemberCannotRedeemAPreparedSession()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var account = await scenario.World.SeedUserAsync();
        await scenario.World.GrantAsync(scenario.Backup.Backup.Setup.Tenant, account);
        var browser = await scenario.World.SignInAsync(account);
        var created = await scenario.CreateSessionAsync(browser);
        await using (var context = scenario.World.Database.CreatePlatformContext())
        {
            var membership = await context.TenantMemberships.SingleAsync(row => row.TenantId == scenario.Backup.Backup.Setup.Tenant.Id && row.UserId == account.UserId);
            context.TenantMemberships.Remove(membership);
            await context.SaveChangesAsync();
        }
        using var response = await browser.GetAsync(created.GetProperty("downloadUrl").GetString()!);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertNoLeakAsync(scenario, response);
        Assert.Equal(DownloadState.Ready, await StateAsync(scenario, created.GetProperty("sessionId").GetGuid()));
    }

    /// <summary>A cross-site request carries no SameSite=Strict cookie, so to the API it is an anonymous request, whatever headers the attacking page adds.</summary>
    [Fact]
    public async Task ACrossSiteStyleRequestWithoutTheCookieCannotStartTheTransferOrGainCorsAccess()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var created = await scenario.CreateSessionAsync();
        var id = created.GetProperty("sessionId").GetGuid();
        var url = created.GetProperty("downloadUrl").GetString()!;
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head, HttpMethod.Options, HttpMethod.Post })
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Add("Origin", "https://attacker.example");
            request.Headers.Add("Referer", "https://attacker.example/page");
            request.Headers.Add("Sec-Fetch-Site", "cross-site");
            request.Headers.Add("Sec-Fetch-Mode", method == HttpMethod.Get ? "no-cors" : "cors");
            request.Headers.Add("Sec-Fetch-Dest", "image");
            if (method == HttpMethod.Options)
            {
                request.Headers.Add("Access-Control-Request-Method", "GET");
                request.Headers.Add("Access-Control-Request-Headers", "x-csrf-token");
            }
            using var response = await scenario.World.Anonymous.SendAsync(request);
            Assert.False(response.IsSuccessStatusCode, $"{method}: {response.StatusCode}");
            Assert.True(method != HttpMethod.Get || response.StatusCode == HttpStatusCode.Unauthorized, $"{method}: {response.StatusCode}");
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
            await AssertNoLeakAsync(scenario, response);
        }
        Assert.Equal(DownloadState.Ready, await StateAsync(scenario, id));
        Assert.Equal(0, await AuditCountAsync(id, "download.streaming"));
        // The owner's own request, even one tagged with a foreign Origin, is served and grants no cross-origin read.
        using var own = new HttpRequestMessage(HttpMethod.Get, url);
        own.Headers.Add("Origin", "https://attacker.example");
        using var ownResponse = await scenario.Admin.Client.SendAsync(own);
        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
        Assert.False(ownResponse.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(scenario.Checksum, Convert.ToHexStringLower(SHA256.HashData(await ownResponse.Content.ReadAsByteArrayAsync())));
    }

    [Fact]
    public async Task ACompletedSessionCannotBeReplayed()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var created = await scenario.CreateSessionAsync();
        var id = created.GetProperty("sessionId").GetGuid();
        var url = created.GetProperty("downloadUrl").GetString()!;
        await AssertOwnerDownloadsAsync(scenario, url);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var replay = await scenario.Admin.GetAsync(url);
            Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
            await AssertNoLeakAsync(scenario, replay);
        }
        Assert.Equal(DownloadState.Completed, await StateAsync(scenario, id));
        Assert.Equal(1, await AuditCountAsync(id, "download.streaming"));
        Assert.Equal(1, await AuditCountAsync(id, "download.completed"));
    }

    [Fact]
    public async Task AFailedSessionStaysRefusedEvenAfterTheFaultIsRepaired()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var created = await scenario.CreateSessionAsync();
        var id = created.GetProperty("sessionId").GetGuid();
        var url = created.GetProperty("downloadUrl").GetString()!;
        var damaged = scenario.Bytes.ToArray();
        damaged[0] ^= 0xff;
        await File.WriteAllBytesAsync(scenario.FilePath, damaged);
        using var failed = await scenario.Admin.GetAsync(url);
        Assert.NotEqual(HttpStatusCode.OK, failed.StatusCode);
        await File.WriteAllBytesAsync(scenario.FilePath, scenario.Bytes);
        using var replay = await scenario.Admin.GetAsync(url);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        Assert.Equal("integrity_mismatch", AgentWorld.ReadJson(replay).GetProperty("error").GetString());
        await AssertNoLeakAsync(scenario, replay);
        Assert.Equal(DownloadState.Failed, await StateAsync(scenario, id));
    }

    [Fact]
    public async Task AnExpiredSessionIsRefusedAndRecordedAsExpired()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var created = await scenario.CreateSessionAsync();
        var id = created.GetProperty("sessionId").GetGuid();
        scenario.World.Clock.Advance(DownloadService.SessionLifetime + TimeSpan.FromSeconds(1));
        using var response = await scenario.Admin.GetAsync(created.GetProperty("downloadUrl").GetString()!);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("download_expired", AgentWorld.ReadJson(response).GetProperty("error").GetString());
        await AssertNoLeakAsync(scenario, response);
        var status = await scenario.StatusAsync(id);
        Assert.Equal("Failed", status.GetProperty("state").GetString());
        Assert.Equal("download_expired", status.GetProperty("failureCode").GetString());
        Assert.Equal(1, await AuditCountAsync(id, "download.expired"));
        Assert.Equal(0, await AuditCountAsync(id, "download.streaming"));
    }

    [Fact]
    public async Task ConcurrentRequestsForOneSessionServeTheArtifactExactlyOnce()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var created = await scenario.CreateSessionAsync();
        var id = created.GetProperty("sessionId").GetGuid();
        var url = created.GetProperty("downloadUrl").GetString()!;
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => scenario.Admin.Client.GetAsync(url)));
        try
        {
            var winner = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Equal(scenario.Bytes, await winner.Content.ReadAsByteArrayAsync());
            foreach (var loser in responses.Where(response => response != winner))
            {
                Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
                await AssertNoLeakAsync(scenario, loser);
            }
        }
        finally { foreach (var response in responses) response.Dispose(); }
        Assert.Equal(DownloadState.Completed, await StateAsync(scenario, id));
        Assert.Equal(1, await AuditCountAsync(id, "download.streaming"));
        Assert.Equal(1, await AuditCountAsync(id, "download.completed"));
    }

    private static async Task<BrowserSession> SignInAsync(DownloadApiScenario scenario, bool member = true, bool platformAdmin = false)
    {
        var account = await scenario.World.SeedUserAsync(platformAdmin);
        if (member)
        {
            await scenario.World.GrantAsync(scenario.Backup.Backup.Setup.Tenant, account);
        }
        return await scenario.World.SignInAsync(account);
    }

    private static async Task<DownloadState> StateAsync(DownloadApiScenario scenario, Guid id) =>
        (await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, id, default))!.State;

    private async Task<int> AuditCountAsync(Guid id, string action)
    {
        await using var context = fixture.CreatePlatformContext();
        return await context.AuditEvents.CountAsync(entry => entry.Action == action && entry.TargetId == id.ToString());
    }

    private static async Task AssertOwnerDownloadsAsync(DownloadApiScenario scenario, string url)
    {
        using var response = await scenario.Admin.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var received = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(scenario.Bytes.Length, received.Length);
        Assert.Equal(scenario.Checksum, Convert.ToHexStringLower(SHA256.HashData(received)));
    }

    /// <summary>A refusal carries neither artifact bytes nor an attachment header, and nothing that names the storage object, its directory or a credential.</summary>
    private static async Task AssertNoLeakAsync(DownloadApiScenario scenario, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.False(body.AsSpan().IndexOf(scenario.Bytes.AsSpan(0, 64)) >= 0);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.NotEqual("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        var text = response + Encoding.UTF8.GetString(body);
        foreach (var secret in new[] { scenario.Accepted.Key, scenario.DirectoryPath, scenario.FilePath, "download-tenant", "Backups:Storage", "Password", "Secret" })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
    }
}
