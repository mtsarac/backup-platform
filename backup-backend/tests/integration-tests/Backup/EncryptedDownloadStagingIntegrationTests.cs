using BackupPlatform.Api.Downloads;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Domain.Downloads;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.IntegrationTests.Backup;

public sealed partial class ManualReplicaRepairIntegrationTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("source-hash")]
    [InlineData("ciphertext")]
    [InlineData("cancel")]
    public async Task NonSeekableEncryptedDownloadsVerifyBothWholeFilesBeforeExposureAndDeleteEveryStage(string outcome)
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var bytes = await scenario.EncryptAsync(outcome == "source-hash" ? new string('a', 64) : null);
        if (outcome == "ciphertext") bytes[^1] ^= 1;
        using var scope = scenario.World.Factory.Services.CreateScope();
        var keys = scope.ServiceProvider.GetRequiredService<ITenantWrapKeyStore>();
        var resolver = new FakeResolver();
        var provider = (InMemoryStorageProvider)resolver.Resolve(scenario.Backup.Backup.Setup.Tenant.Id, "central", "central")!;
        provider.Storage[scenario.Accepted.Key] = bytes;
        var store = scenario.Store();
        var service = new DownloadService(store, resolver, new NoAgentDownloadSource(), scenario.Backup.Clock, NullLogger<DownloadService>.Instance, keys);
        var prepared = (await service.PrepareAsync(scenario.Backup.Backup.Setup.Tenant.Id, scenario.Accepted.ArtifactId,
            scenario.Backup.Backup.Setup.Account.UserId.Value, new PrepareDownloadRequest(Decrypt: true), default))!;
        var session = (await store.GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, prepared.SessionId, default))!;
        using var spool = new AgentDownloadSpool(scenario.Backup.Clock, NullLogger<AgentDownloadSpool>.Instance);
        string staging;
        using (var probe = spool.CreateFile()) staging = Path.GetDirectoryName(probe.Name)!;
        var http = new DefaultHttpContext();
        using var response = new MemoryStream();
        http.Response.Body = response;
        using var cancel = new CancellationTokenSource();
        http.RequestAborted = cancel.Token;
        var sawCiphertext = false;
        var source = new ObservedCiphertext(bytes, () =>
        {
            sawCiphertext = true;
            Assert.Equal(0, response.Length);
            var files = Directory.GetFiles(staging);
            Assert.Single(files);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(files[0]));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(staging));
            }
            if (outcome == "cancel") cancel.Cancel();
        });
        provider.ReadStream = _ => source;
        await new DownloadStreamer(store, service, scenario.Backup.Clock, NullLogger<DownloadStreamer>.Instance, spool, keys).StreamAsync(http, session);
        Assert.True(sawCiphertext);
        Assert.True(source.Disposed);
        Assert.Empty(Directory.GetFiles(staging));
        var persisted = (await store.GetAsync(session.TenantId, session.Id, default))!;
        Assert.NotNull(persisted.ReleasedAt);
        if (outcome == "success")
        {
            Assert.Equal(scenario.Bytes, response.ToArray());
            Assert.Equal((DownloadState.Completed, bytes.LongLength), (persisted.State, persisted.TransferredBytes));
        }
        else
        {
            Assert.Equal(0, persisted.TransferredBytes);
            Assert.Equal(outcome == "cancel" ? DownloadState.Cancelled : DownloadState.Failed, persisted.State);
            Assert.Equal(outcome == "cancel" ? "download_cancelled" : "integrity_mismatch", persisted.FailureCode);
            Assert.Equal(StatusCodes.Status409Conflict, http.Response.StatusCode);
        }
        await using var context = fixture.CreatePlatformContext();
        var unwraps = await context.AuditEvents.CountAsync(e => e.TenantId == session.TenantId && e.Action == "backup_artifact.key_unwrapped");
        Assert.Equal(outcome is "success" or "source-hash" ? 1 : 0, unwraps);
    }

    private sealed class ObservedCiphertext(byte[] bytes, Action onRead) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onRead();
            cancellationToken.ThrowIfCancellationRequested();
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 137)], cancellationToken);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
