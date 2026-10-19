using System.Net;
using System.Security.Cryptography;
using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Downloads;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.IntegrationTests;

public sealed class EncryptedDownloadApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task CiphertextIsTheDefaultAndExplicitDecryptionReturnsVerifiedPlaintext()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var ciphertext = await scenario.EncryptAsync();
        var raw = await scenario.CreateSessionAsync();
        Assert.False(raw.GetProperty("decrypted").GetBoolean());
        Assert.EndsWith(".bak.bpe", raw.GetProperty("filename").GetString());
        using var stored = await scenario.Admin.GetAsync(raw.GetProperty("downloadUrl").GetString()!);
        Assert.Equal(ciphertext, await stored.Content.ReadAsByteArrayAsync());
        using var prepared = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.ArtifactPath + "/downloads", new { decrypt = true });
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        var download = AgentWorld.ReadJson(prepared);
        Assert.True(download.GetProperty("decrypted").GetBoolean());
        Assert.Equal(scenario.Checksum, download.GetProperty("checksum").GetString());
        Assert.EndsWith(".bak", download.GetProperty("filename").GetString());
        using var content = await scenario.Admin.GetAsync(download.GetProperty("downloadUrl").GetString()!);
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal(scenario.Bytes, await content.Content.ReadAsByteArrayAsync());
        Assert.True(content.Headers.CacheControl!.NoStore);
        var session = (await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, download.GetProperty("sessionId").GetGuid(), default))!;
        Assert.Equal(DownloadState.Completed, session.State);
        Assert.Equal(ciphertext.LongLength, session.TransferredBytes);
        Assert.NotNull(session.ReleasedAt);
    }

    [Fact]
    public async Task ASourceHashMismatchReturnsOnlyAnErrorNeverEvenTheFirstPlaintextChunk()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        await scenario.EncryptAsync(new string('a', 64));
        using var prepared = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.ArtifactPath + "/downloads", new { decrypt = true });
        var download = AgentWorld.ReadJson(prepared);
        using var content = await scenario.Admin.GetAsync(download.GetProperty("downloadUrl").GetString()!);
        Assert.Equal(HttpStatusCode.Conflict, content.StatusCode);
        Assert.Null(content.Content.Headers.ContentDisposition);
        Assert.Equal("integrity_mismatch", AgentWorld.ReadJson(content).GetProperty("error").GetString());
        var session = (await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, download.GetProperty("sessionId").GetGuid(), default))!;
        Assert.Equal((DownloadState.Failed, 0L), (session.State, session.TransferredBytes));
        Assert.NotNull(session.ReleasedAt);
    }

    [Fact]
    public async Task CorruptedCiphertextFailsBeforeAnyKeyUnwrap()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var ciphertext = await scenario.EncryptAsync();
        using var prepared = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.ArtifactPath + "/downloads", new { decrypt = true });
        ciphertext[^1] ^= 1;
        await File.WriteAllBytesAsync(scenario.FilePath, ciphertext);
        using var content = await scenario.Admin.GetAsync(AgentWorld.ReadJson(prepared).GetProperty("downloadUrl").GetString()!);
        Assert.Equal(HttpStatusCode.Conflict, content.StatusCode);
        Assert.Equal("integrity_mismatch", AgentWorld.ReadJson(content).GetProperty("error").GetString());
        await using var context = fixture.CreatePlatformContext();
        Assert.False(await context.AuditEvents.AnyAsync(e => e.TenantId == scenario.Backup.Backup.Setup.Tenant.Id && e.Action == "backup_artifact.key_unwrapped"));
    }

    [Fact]
    public async Task RotationKeepsOldDownloadsReadableUntilTheOldKeyIsExplicitlyRetired()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        await scenario.EncryptAsync();
        using var scope = scenario.World.Factory.Services.CreateScope();
        var keys = scope.ServiceProvider.GetRequiredService<ITenantWrapKeyStore>();
        var tenant = scenario.Backup.Backup.Setup.Tenant.Id;
        var rotation = await keys.RotateAsync(tenant, scenario.Backup.Backup.Setup.Account.UserId, default);
        Assert.Equal(TenantWrapKeyState.DecryptOnly, rotation.PreviousKey!.State);
        using var prepared = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.ArtifactPath + "/downloads", new { decrypt = true });
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        using var content = await scenario.Admin.GetAsync(AgentWorld.ReadJson(prepared).GetProperty("downloadUrl").GetString()!);
        Assert.Equal(scenario.Bytes, await content.Content.ReadAsByteArrayAsync());
        await keys.RetireAsync(tenant, rotation.PreviousKey.Version, scenario.Backup.Backup.Setup.Account.UserId, default);
        using var denied = await scenario.Admin.SendAsync(HttpMethod.Post, scenario.ArtifactPath + "/downloads", new { decrypt = true });
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Equal("key_unavailable", AgentWorld.ReadJson(denied).GetProperty("error").GetString());
        using var raw = await scenario.CreateRawAsync();
        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
    }
}
