extern alias AgentCore;
extern alias AgentTransport;

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AgentCore::BackupPlatform.Agent.Core.Connectivity;
using AgentCore::BackupPlatform.Agent.Core.Identity;
using AgentCore::BackupPlatform.Agent.Core.Tasks;
using AgentTransport::BackupPlatform.Agent.Transport;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Downloads;
using BackupPlatform.Domain.Downloads;
using BackupPlatform.StorageProviders.Local;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;


namespace BackupPlatform.IntegrationTests;

public sealed class RealBrowserDirectDownloadSmokeTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private sealed class StaticCredentialStore(AgentLocalIdentity identity) : AgentCore::BackupPlatform.Agent.Core.Identity.IAgentCredentialStore
    {
        public Task<AgentLocalIdentity?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult<AgentLocalIdentity?>(identity);
        public Task SaveAsync(AgentLocalIdentity identity, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CountingFilter(Action<long> onBytes) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path.Value?.EndsWith("/content", StringComparison.Ordinal) == true)
                {
                    var original = context.Response.Body;
                    using var counter = new CountingStream(original, count =>
                    {
                        if (context.Response.ContentType?.Contains("octet-stream", StringComparison.OrdinalIgnoreCase) == true) onBytes(count);
                    });
                    context.Response.Body = counter;
                    try { await nextMiddleware(); }
                    finally { context.Response.Body = original; }
                    return;
                }
                await nextMiddleware();
            });
            next(app);
        };
    }

    private sealed class CountingStream(Stream inner, Action<long> onBytes) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) { onBytes(count); inner.Write(buffer, offset, count); }
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            onBytes(count);
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onBytes(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken);
        }
    }

    [Fact]
    public async Task RealBrowserDirectDownloadStreamsDirectlyFromAgentWithZeroBackendPayloadBytes()
    {
        long backendPayloadBytes = 0;
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture, agentLocal: true, kestrel: true, configureServices: services =>
        {
            services.AddSingleton<IStartupFilter>(new CountingFilter(count => Interlocked.Add(ref backendPayloadBytes, count)));
        });
        await DownloadAgentApiTests.MakeOnlineAsync(scenario);

        var cookies = new CookieContainer();
        var browserSession = await scenario.World.SignInAsync(scenario.Backup.Backup.Setup.Account, cookies);
        var created = await scenario.CreateSessionAsync(browserSession);
        Assert.True(created.GetProperty("direct").GetBoolean());
        var sessionId = created.GetProperty("sessionId").GetGuid();
        var downloadUrl = created.GetProperty("downloadUrl").GetString()!;
        using var client = AgentWorld.CreateTrustingClient(scenario.World.ServerAddress!, handleCookies: false);
        using var antiforgeryResponse = await browserSession.GetAsync("/api/auth/antiforgery");
        var csrf = AgentWorld.ReadJson(antiforgeryResponse).GetProperty("requestToken").GetString()!;
        var cookieHeader = cookies.GetCookieHeader(scenario.World.ServerAddress!);

        var agentIdentity = new AgentLocalIdentity(scenario.Backup.Agent.AgentId, scenario.World.ServerAddress!,
            scenario.Backup.Agent.InstallationId, new AgentStoredCredential(scenario.Backup.Agent.CredentialId, scenario.Backup.Agent.Secret, 1));
        var credStore = new StaticCredentialStore(agentIdentity);
        var directClient = new HttpDownloadTaskClient(client, new AgentBackendEndpoint(scenario.World.ServerAddress!), scenario.Backup.Clock);
        var worker = new DownloadTaskWorker(directClient, credStore, [new LocalStorageProvider(new LocalStorageOptions(Path.Combine(scenario.DirectoryPath, "tenants", "download-tenant")))],
            new AgentSoftwareInfo("1.0.0", "linux", null, null), scenario.Backup.Clock, NullLogger<DownloadTaskWorker>.Instance);

        using var workerCts = new CancellationTokenSource();
        var workerTask = worker.RunAsync(workerCts.Token);

        var script = $$"""
        import { chromium } from 'playwright-core';
        const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROMIUM_PATH || '/usr/bin/chromium', args: ['--no-sandbox'] });
        try {
        const context = await browser.newContext({ ignoreHTTPSErrors: true });
        const origin = {{JsonSerializer.Serialize(scenario.World.ServerAddress!.GetLeftPart(UriPartial.Authority))}};
        const cookieHeader = {{JsonSerializer.Serialize(cookieHeader)}};
        await context.addCookies(cookieHeader.split('; ').map(pair => {
          const at = pair.indexOf('=');
          return { name: pair.slice(0, at), value: pair.slice(at + 1), url: origin };
        }));
        const page = await context.newPage();
        page.on('console', message => { if (message.text() === 'OFFER') console.log('OFFER'); });
        await page.goto(origin + '/api/auth/antiforgery');
        const result = await page.evaluate(async ({ url, csrf }) => {
        const peer = new RTCPeerConnection({ iceServers: [] });
        const channel = peer.createDataChannel('backup-download-v1', { ordered: true });
        channel.binaryType = 'arraybuffer';
        let received = [];
        const done = new Promise((resolve, reject) => {
          channel.onmessage = async event => {
            if (typeof event.data === 'string') {
              if (event.data === 'done') {
                const length = received.reduce((n, chunk) => n + chunk.length, 0);
                const bytes = new Uint8Array(length);
                let offset = 0;
                for (const chunk of received) { bytes.set(chunk, offset); offset += chunk.length; }
                const hash = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)), b => b.toString(16).padStart(2, '0')).join('');
                channel.send('saved');
                resolve({ length, hash, bytes: Array.from(bytes) });
              } else {
                channel.send('ready');
              }
            } else {
              received.push(new Uint8Array(event.data));
              channel.send('ack');
            }
          };
          channel.onerror = reject;
          peer.onconnectionstatechange = () => {
            if (['failed', 'disconnected', 'closed'].includes(peer.connectionState)) reject(new Error('peer ' + peer.connectionState));
          };
        });

        const offer = await peer.createOffer();
        await peer.setLocalDescription(offer);
        await new Promise(r => {
          if (peer.iceGatheringState === 'complete') r();
          else peer.onicegatheringstatechange = () => { if (peer.iceGatheringState === 'complete') r(); };
        });

        const postRes = await fetch(url, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf },
          body: JSON.stringify({ sdp: peer.localDescription.sdp })
        });
        if (!postRes.ok) throw new Error('post offer failed: ' + postRes.status);
        console.log('OFFER');

        let answer = null;
        for (let i = 0; i < 40; i++) {
          await new Promise(r => setTimeout(r, 250));
          const getRes = await fetch(url);
          if (getRes.ok) {
            const body = await getRes.json();
            if (body.sdp) { answer = body.sdp; break; }
          }
        }
        if (!answer) throw new Error('answer timeout');
        await peer.setRemoteDescription({ type: 'answer', sdp: answer });

        const result = await done;
        peer.close();
        return result;
        }, { url: {{JsonSerializer.Serialize(downloadUrl)}}, csrf: {{JsonSerializer.Serialize(csrf)}} });
        console.log(JSON.stringify(result));
        } finally { await browser.close(); }
        """;

        var frontend = new DirectoryInfo(AppContext.BaseDirectory);
        while (frontend is not null && !Directory.Exists(Path.Combine(frontend.FullName, "backup-frontend")))
            frontend = frontend.Parent;
        Assert.NotNull(frontend);
        using var browserProcess = new Process
        {
            StartInfo = new ProcessStartInfo("bun", ["--eval", script])
            {
                WorkingDirectory = Path.Combine(frontend.FullName, "backup-frontend"),
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        Assert.True(browserProcess.Start());
        var stderr = browserProcess.StandardError.ReadToEndAsync();

        try
        {
            Assert.Equal("OFFER", await browserProcess.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            var taskClaim = await DownloadAgentApiTests.ClaimAsync(scenario);
            var envelope = new AgentTaskEnvelope
            {
                Id = sessionId.ToString("D"),
                Type = DownloadTaskTypes.BackupDownload,
                Download = new DownloadTaskSnapshot
                {
                    SessionId = sessionId,
                    TenantId = scenario.Backup.TenantId,
                    ArtifactId = scenario.Accepted.ArtifactId,
                    ReplicaId = scenario.ReplicaId,
                    LeaseToken = taskClaim.GetProperty("leaseToken").GetGuid(),
                    LeaseExpiresAt = scenario.Backup.Clock.GetUtcNow().AddMinutes(5),
                    StorageProvider = "local",
                    StorageTarget = "local",
                    StorageKey = scenario.Accepted.Key,
                    SizeBytes = scenario.Bytes.Length,
                    Checksum = scenario.Checksum,
                    ChecksumAlgorithm = "sha256"
                }
            };

            Assert.True(worker.TryPublish(envelope));

            var output = await browserProcess.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
            await browserProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(browserProcess.ExitCode == 0, await stderr);
            Assert.NotNull(output);
            using var result = JsonDocument.Parse(output);
            Assert.Equal(scenario.Bytes.Length, result.RootElement.GetProperty("length").GetInt32());
            Assert.Equal(scenario.Checksum, result.RootElement.GetProperty("hash").GetString());
            Assert.Equal(scenario.Bytes, result.RootElement.GetProperty("bytes").EnumerateArray().Select(value => value.GetByte()).ToArray());
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            DownloadSession? sessionRow = null;
            while (DateTimeOffset.UtcNow < deadline && (sessionRow = await scenario.Store().GetAsync(scenario.Backup.Backup.Setup.Tenant.Id, sessionId, default))?.State != DownloadState.Completed)
                await Task.Delay(100);
            Assert.NotNull(sessionRow);
            Assert.Equal(DownloadState.Completed, sessionRow.State);
            using var status = await browserSession.GetAsync(scenario.ArtifactPath + $"/downloads/{sessionId}");
            Assert.Equal("Completed", AgentWorld.ReadJson(status).GetProperty("state").GetString());
            Assert.Equal(0, backendPayloadBytes);
        }
        finally
        {
            if (!browserProcess.HasExited) browserProcess.Kill(entireProcessTree: true);
            await workerCts.CancelAsync();
            try { await workerTask; } catch (OperationCanceledException) { }
        }
    }
}
