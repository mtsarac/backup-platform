using System.Net;
using System.Text;
using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.S3;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.UnitTests.Backup;

/// <summary>A checkpointed S3 multipart upload that is abandoned leaves no remote parts: aborted on cancel, and on expiry through the retained-transfer journal.</summary>
public sealed class AbandonedS3UploadTests
{
    private static readonly StagingCleanupOptions Options = new() { MinimumAge = TimeSpan.FromHours(12) };

    [Fact]
    public async Task Cancelled_run_aborts_its_multipart_upload()
    {
        using var s3 = new FakeS3();
        using var harness = new PipelineHarness();
        using var cancel = new CancellationTokenSource();
        s3.OnPart = cancel.Cancel;
        var request = Request(harness, s3);
        var result = await harness.RunAsync(request, cancel.Token);
        Assert.Equal(DatabaseBackupStatus.Cancelled, result.Status);
        Assert.Equal(["DELETE ?uploadId=up-1"], s3.Aborts);
        Assert.False(File.Exists(Journal(harness, request)));
    }

    [Fact]
    public async Task Expired_attempt_aborts_its_upload_when_the_cleaner_releases_the_journal_and_never_before_it_is_terminal()
    {
        using var s3 = new FakeS3();
        using var harness = new PipelineHarness();
        using var pause = new CancellationTokenSource();
        s3.OnPart = pause.Cancel;
        var request = Request(harness, s3) with { PauseToken = pause.Token };
        var paused = await harness.RunAsync(request);
        Assert.Equal(DatabaseBackupStatus.Paused, paused.Status);
        var journal = Journal(harness, request);
        Assert.True(File.Exists(journal));

        var terminal = false;
        var cleaner = Cleaner(harness, request, (_, _) => Task.FromResult(terminal), s3);
        await cleaner.RunAsync(Options, default);
        Assert.Empty(s3.Aborts);
        Assert.True(File.Exists(journal));

        terminal = true;
        await cleaner.RunAsync(Options, default);
        Assert.Equal(["DELETE ?uploadId=up-1"], s3.Aborts);
        Assert.False(File.Exists(journal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_abort_keeps_the_journal_and_the_next_sweep_retries(bool failedOnCancel)
    {
        using var s3 = new FakeS3 { FailAbort = true };
        using var harness = new PipelineHarness();
        using var stop = new CancellationTokenSource();
        s3.OnPart = stop.Cancel;
        var request = Request(harness, s3);
        var result = failedOnCancel
            ? await harness.RunAsync(request, stop.Token)
            : await harness.RunAsync(request with { PauseToken = stop.Token });
        var journal = Journal(harness, request);
        Assert.Equal(failedOnCancel ? DatabaseBackupStatus.Cancelled : DatabaseBackupStatus.Paused, result.Status);
        Assert.True(File.Exists(journal));
        var attempts = s3.Aborts.Count;

        var cleaner = Cleaner(harness, request, (_, _) => Task.FromResult(true), s3);
        await cleaner.RunAsync(Options, default);
        Assert.Equal(attempts + 1, s3.Aborts.Count);
        Assert.True(File.Exists(journal));

        s3.FailAbort = false;
        await cleaner.RunAsync(Options, default);
        Assert.Equal(attempts + 2, s3.Aborts.Count);
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task Cleaner_without_an_aborter_never_forgets_a_journaled_upload()
    {
        using var s3 = new FakeS3();
        using var harness = new PipelineHarness();
        using var pause = new CancellationTokenSource();
        s3.OnPart = pause.Cancel;
        var request = Request(harness, s3) with { PauseToken = pause.Token };
        await harness.RunAsync(request);
        var cleaner = new StagingCleaner(new(request.Staging.AgentDirectory), new StagingUsageRegistry(), TimeProvider.System,
            NullLogger<StagingCleaner>.Instance, (_, _) => Task.FromResult(true));
        await cleaner.RunAsync(Options, default);
        Assert.True(File.Exists(Journal(harness, request)));
    }

    private static DatabaseBackupPipelineRequest Request(PipelineHarness harness, FakeS3 s3) => harness.Request() with
    {
        AttemptId = Guid.NewGuid(), RecordCheckpointAsync = (_, _) => Task.CompletedTask,
        Destinations = [new(new StorageProviderId("s3"), s3.Provider(), Guid.NewGuid(), true)]
    };

    private static string Journal(PipelineHarness harness, DatabaseBackupPipelineRequest request) => Path.Combine(harness.StagingDirectory, $"transfer-{request.AttemptId:N}.json");

    private static StagingCleaner Cleaner(PipelineHarness harness, DatabaseBackupPipelineRequest request, Func<Guid, CancellationToken, Task<bool>> canRelease, FakeS3 s3) =>
        new(new(request.Staging.AgentDirectory), new StagingUsageRegistry(), TimeProvider.System, NullLogger<StagingCleaner>.Instance, canRelease, new S3Aborter(s3));

    private sealed class S3Aborter(FakeS3 s3) : IRetainedUploadAborter
    {
        public Task AbortAsync(Guid attemptId, RetainedUpload upload, CancellationToken cancellationToken) =>
            s3.Provider().AbortTransferAsync(new(upload.Key), upload.ProviderState, cancellationToken);
    }

    private sealed class FakeS3 : HttpMessageHandler
    {
        private readonly HttpClient _http;
        public FakeS3() => _http = new HttpClient(this);
        public List<string> Aborts { get; } = [];
        public bool FailAbort { get; set; }
        public Action? OnPart { get; set; }

        public S3StorageProvider Provider() => new(_http,
            new(new Uri("https://storage.example"), "bucket", "us-east-1", Guid.NewGuid(), Guid.NewGuid()),
            _ => ValueTask.FromResult(new S3Credential("access", "secret")));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.Query;
            if (request.Method == HttpMethod.Post && query == "?uploads=")
                return Task.FromResult(Xml("<InitiateMultipartUploadResult><UploadId>up-1</UploadId></InitiateMultipartUploadResult>"));
            if (request.Method == HttpMethod.Put)
            {
                OnPart?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                var part = Xml("");
                part.Headers.TryAddWithoutValidation("ETag", "\"part-1\"");
                return Task.FromResult(part);
            }
            if (request.Method == HttpMethod.Delete)
            {
                Aborts.Add($"DELETE {query}");
                return Task.FromResult(new HttpResponseMessage(FailAbort ? HttpStatusCode.Forbidden : HttpStatusCode.NoContent));
            }
            return Task.FromResult(Xml(""));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _http.Dispose();
            base.Dispose(disposing);
        }

        private static HttpResponseMessage Xml(string xml) => new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
    }
}
