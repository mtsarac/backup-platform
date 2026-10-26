using System.Net;
using System.Text.Json;
using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Agent.Transport;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.AgentTests;

/// <summary>A terminal attempt has no lease: the agent aborts its journaled S3 multipart upload with credentials the backend serves only for such an attempt.</summary>
public sealed class RetainedUploadAborterTests
{
    private static readonly Guid Tenant = Guid.NewGuid(), Job = Guid.NewGuid(), Run = Guid.NewGuid(), Attempt = Guid.NewGuid(), Connection = Guid.NewGuid();
    private static readonly string State = JsonSerializer.Serialize(new { UploadId = "up-1", Parts = "<CompleteMultipartUpload/>", Offset = 1, RetainUntil = (DateTimeOffset?)null });
    private static readonly string Key = $"{Tenant:N}/{Job:N}/{Run:N}/{Attempt:N}/Sales_20261010T120000Z_abcdef12_FULL.bak";

    private const string Access = """
        {"providerType":"s3","accessKey":"access","secretKey":"secret","configuration":{"endpoint":"https://storage.example","bucket":"bucket","region":"us-east-1"}}
        """;

    private static HttpRetainedUploadAborter Aborter(FakeHttpHandler handler, out HttpClient http)
    {
        http = new HttpClient(handler);
        return new HttpRetainedUploadAborter(http, new AgentBackendEndpoint(new Uri("https://backup.example.test/")), new FakeCredentialStore(TestIdentity.Create()));
    }

    [Fact]
    public async Task Aborts_the_multipart_upload_with_credentials_from_the_abort_only_route()
    {
        using var handler = new FakeHttpHandler();
        handler.Respond = (request, _) => Task.FromResult(request.RequestUri!.Host == "backup.example.test"
            ? FakeHttpHandler.Json(HttpStatusCode.OK, Access)
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        var aborter = Aborter(handler, out var http);
        using var client = http;

        await aborter.AbortAsync(Attempt, new RetainedUpload("s3", Connection, Key, State), default);

        var backend = Assert.Single(handler.Requests, r => r.Uri.Host == "backup.example.test");
        Assert.Equal(HttpMethod.Post, backend.Method);
        Assert.Equal($"/api/agent/tasks/{Attempt:D}/storage/abort-access", backend.Uri.AbsolutePath);
        Assert.Equal(Connection.ToString("D"), backend.Headers[TaskStorageProtocol.DestinationHeader]);
        Assert.False(backend.Headers.ContainsKey(TaskStorageProtocol.LeaseHeader));
        var s3 = Assert.Single(handler.Requests, r => r.Uri.Host == "storage.example");
        Assert.Equal(HttpMethod.Delete, s3.Method);
        Assert.Contains("uploadId=up-1", s3.Uri.Query);
        Assert.Contains($"/tenants/{Tenant:N}/connections/{Connection:N}/", s3.Uri.AbsolutePath);
    }

    [Fact]
    public async Task A_refusal_by_the_backend_fails_the_abort_without_contacting_the_storage()
    {
        using var handler = new FakeHttpHandler();
        handler.Respond = (_, _) => Task.FromResult(FakeHttpHandler.Json(HttpStatusCode.Conflict, """{"error":"attempt_not_terminal"}"""));
        var aborter = Aborter(handler, out var http);
        using var client = http;

        var failure = await Assert.ThrowsAsync<StorageException>(() => aborter.AbortAsync(Attempt, new RetainedUpload("s3", Connection, Key, State), default));

        Assert.Equal(StorageFailureKind.AccessDenied, failure.Kind);
        Assert.DoesNotContain(handler.Requests, r => r.Uri.Host == "storage.example");
    }

    [Fact]
    public async Task A_failed_storage_abort_is_not_swallowed()
    {
        using var handler = new FakeHttpHandler();
        handler.Respond = (request, _) => Task.FromResult(request.RequestUri!.Host == "backup.example.test"
            ? FakeHttpHandler.Json(HttpStatusCode.OK, Access)
            : new HttpResponseMessage(HttpStatusCode.Forbidden));
        var aborter = Aborter(handler, out var http);
        using var client = http;

        await Assert.ThrowsAsync<StorageException>(() => aborter.AbortAsync(Attempt, new RetainedUpload("s3", Connection, Key, State), default));
    }

    [Theory]
    [InlineData("sftp", true)]
    [InlineData("s3", false)]
    public async Task Other_providers_and_foreign_keys_make_no_request(string provider, bool succeeds)
    {
        using var handler = new FakeHttpHandler();
        var aborter = Aborter(handler, out var http);
        using var client = http;
        var key = succeeds ? Key : $"{Tenant:N}/{Job:N}/{Run:N}/{Guid.NewGuid():N}/Sales_20261010T120000Z_abcdef12_FULL.bak";

        var abort = aborter.AbortAsync(Attempt, new RetainedUpload(provider, Connection, key, State), default);
        if (succeeds) await abort; else await Assert.ThrowsAsync<StorageException>(() => abort);

        Assert.Empty(handler.Requests);
    }
}
