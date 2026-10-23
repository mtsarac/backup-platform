using System.Net;
using System.Security.Cryptography;
using System.Text;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.S3;
using Xunit;

namespace BackupPlatform.UnitTests.Storage;

public sealed class S3StorageProviderTests
{
    [Fact]
    public async Task Access_check_rejects_denied_bucket_credentials_without_publishing_an_object()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Head, request.Method);
            Assert.Equal("/bucket", request.RequestUri!.AbsolutePath.TrimEnd('/'));
            return new(HttpStatusCode.Forbidden);
        }));
        IS3AccessChecker checker = Provider(http);
        var error = await Assert.ThrowsAsync<StorageException>(() => checker.CheckAccessAsync(default));
        Assert.Equal(StorageFailureKind.AccessDenied, error.Kind);
    }

    [Fact]
    public async Task Multipart_integrity_failure_aborts_without_publishing_or_retaining_bad_bytes()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.Query}");
            if (request.Method == HttpMethod.Post && request.RequestUri.Query == "?uploads=")
                return Xml("<InitiateMultipartUploadResult><UploadId>upload-1</UploadId></InitiateMultipartUploadResult>");
            if (request.Method == HttpMethod.Put) { var response = Xml(""); response.Headers.TryAddWithoutValidation("ETag", "\"part-1\""); return response; }
            return Xml("");
        }));
        var provider = Provider(http);
        var error = await Assert.ThrowsAsync<StorageException>(() => provider.PutAsync(new("backup.bak"), new MemoryStream("bad"u8.ToArray()),
            new(StorageOverwritePolicy.Fail, ContentChecksum.Sha256(SHA256.HashData("good"u8)), 3), null, default));
        Assert.Equal(StorageFailureKind.IntegrityMismatch, error.Kind);
        Assert.Contains("DELETE ?uploadId=upload-1", requests);
        Assert.DoesNotContain(requests, request => request.StartsWith("POST ?uploadId="));
    }

    [Fact]
    public async Task Locked_object_cannot_be_hidden_by_a_delete_marker()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Head, request.Method);
            var response = Xml("");
            response.Content.Headers.ContentLength = 3;
            response.Headers.TryAddWithoutValidation("x-amz-object-lock-mode", "COMPLIANCE");
            response.Headers.TryAddWithoutValidation("x-amz-object-lock-retain-until-date", "2099-01-01T00:00:00Z");
            response.Headers.TryAddWithoutValidation("x-amz-version-id", "version-1");
            return response;
        }));
        var error = await Assert.ThrowsAsync<StorageException>(() => Provider(http).DeleteAsync(new("backup.bak"), default));
        Assert.Equal(StorageFailureKind.AccessDenied, error.Kind);
    }

    [Fact]
    public async Task Denied_credentials_are_sanitized()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.Forbidden) { Content = new StringContent("secret-access-key") }));
        var error = await Assert.ThrowsAsync<StorageException>(() => Provider(http).GetMetadataAsync(new("backup.bak"), default));
        Assert.Equal(StorageFailureKind.AccessDenied, error.Kind);
        Assert.DoesNotContain("secret-access-key", error.ToString());
    }

    [Theory]
    [InlineData("COMPLIANCE")]
    [InlineData("GOVERNANCE")]
    public async Task Direct_multipart_upload_verifies_bytes_and_service_retention_before_success(string mode)
    {
        string? retention = null;
        byte[]? stored = null;
        using var http = new HttpClient(new AsyncHandler(async request =>
        {
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=access/", request.Headers.GetValues("Authorization").Single());
            Assert.DoesNotContain("secret", request.RequestUri!.AbsoluteUri);
            if (request.RequestUri.Query == "?object-lock=")
                return Xml("<ObjectLockConfiguration><ObjectLockEnabled>Enabled</ObjectLockEnabled></ObjectLockConfiguration>");
            Assert.StartsWith("/bucket/tenants/11111111111111111111111111111111/connections/22222222222222222222222222222222/", request.RequestUri.AbsolutePath);
            if (request.Method == HttpMethod.Post && request.RequestUri.Query == "?uploads=")
            {
                Assert.Equal(mode, request.Headers.GetValues("x-amz-object-lock-mode").Single());
                retention = request.Headers.GetValues("x-amz-object-lock-retain-until-date").Single();
                return Xml("<InitiateMultipartUploadResult><UploadId>upload-1</UploadId></InitiateMultipartUploadResult>");
            }
            if (request.Method == HttpMethod.Put)
            {
                stored = await request.Content!.ReadAsByteArrayAsync();
                Assert.Equal(MD5.HashData(stored), request.Content.Headers.ContentMD5);
                var response = Xml("");
                response.Headers.TryAddWithoutValidation("ETag", "\"part-1\"");
                return response;
            }
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("*", request.Headers.GetValues("If-None-Match").Single());
                Assert.Contains("part-1", await request.Content!.ReadAsStringAsync());
                return Xml("<CompleteMultipartUploadResult/>");
            }
            if (request.Method == HttpMethod.Head)
            {
                var response = Xml("");
                response.Content.Headers.ContentLength = 3;
                response.Headers.TryAddWithoutValidation("x-amz-object-lock-mode", mode);
                response.Headers.TryAddWithoutValidation("x-amz-object-lock-retain-until-date", retention);
                return response;
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(stored!) };
        }));
        var provider = new S3StorageProvider(http,
            new(new Uri("https://storage.example"), "bucket", "us-east-1", Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"), mode, 30),
            _ => ValueTask.FromResult(new S3Credential("access", "secret")));
        var checksum = ContentChecksum.Sha256(SHA256.HashData("abc"u8));
        var metadata = await provider.PutAsync(new("backup.bak"), new MemoryStream("abc"u8.ToArray()),
            new(StorageOverwritePolicy.Fail, checksum, 3), null, default);
        Assert.Equal(3, metadata.SizeBytes);
        Assert.Equal(StorageVerificationStatus.Verified, (await provider.VerifyAsync(new("backup.bak"), new(3, checksum), default)).Status);
        Assert.True(provider.Capabilities.Supports(StorageCapabilityIds.ImmutableStorage));
    }

    [Fact]
    public async Task Conditional_publication_conflict_never_claims_ownership_of_the_existing_object()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Query == "?uploads=")
                return Xml("<InitiateMultipartUploadResult><UploadId>upload-1</UploadId></InitiateMultipartUploadResult>");
            if (request.Method == HttpMethod.Put) { var part = Xml(""); part.Headers.TryAddWithoutValidation("ETag", "\"part-1\""); return part; }
            if (request.Method == HttpMethod.Post) return new(HttpStatusCode.PreconditionFailed);
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Contains("uploadId=", request.RequestUri.Query);
            return Xml("");
        }));
        var error = await Assert.ThrowsAsync<StorageException>(() => Provider(http).PutAsync(new("backup.bak"), new MemoryStream("abc"u8.ToArray()),
            new(StorageOverwritePolicy.Fail, ContentChecksum.Sha256(SHA256.HashData("abc"u8)), 3), null, default));
        Assert.Equal(StorageFailureKind.AlreadyExists, error.Kind);
        Assert.False(error.ObjectMayRemain);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task Temporary_service_failures_are_recoverable(int status)
    {
        using var http = new HttpClient(new Handler(_ => new((HttpStatusCode)status)));
        var error = await Assert.ThrowsAsync<StorageException>(() => Provider(http).GetMetadataAsync(new("backup.bak"), default));
        Assert.Equal(StorageFailureKind.NetworkUnavailable, error.Kind);
    }

    [Fact]
    public async Task Interrupted_response_body_is_recoverable_during_verification()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            var response = Xml("");
            if (request.Method == HttpMethod.Head) response.Content.Headers.ContentLength = 3;
            else response.Content = new StreamContent(new BrokenReadStream());
            return response;
        }));
        var error = await Assert.ThrowsAsync<StorageException>(() => Provider(http).VerifyAsync(new("backup.bak"),
            new(3, ContentChecksum.Sha256(SHA256.HashData("abc"u8))), default));
        Assert.Equal(StorageFailureKind.NetworkUnavailable, error.Kind);
    }

    [Fact]
    public async Task Resume_validates_parts_across_all_pages_before_publishing()
    {
        var bytes = new byte[8 * 1024 * 1024 + 3];
        var checksum = ContentChecksum.Sha256(SHA256.HashData(bytes));
        var published = false;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Head)
            {
                if (!published) return new(HttpStatusCode.NotFound);
                var response = Xml("");
                response.Content.Headers.ContentLength = bytes.Length;
                return response;
            }
            if (request.Method == HttpMethod.Get && request.RequestUri!.Query.Contains("uploadId="))
                return request.RequestUri.Query.Contains("part-number-marker=1")
                    ? Xml("<ListPartsResult><IsTruncated>false</IsTruncated><Part><PartNumber>2</PartNumber><ETag>second</ETag></Part></ListPartsResult>")
                    : Xml("<ListPartsResult><IsTruncated>true</IsTruncated><NextPartNumberMarker>1</NextPartNumberMarker><Part><PartNumber>1</PartNumber><ETag>first</ETag></Part></ListPartsResult>");
            if (request.Method == HttpMethod.Post)
            {
                published = true;
                return Xml("<CompleteMultipartUploadResult/>");
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var state = System.Text.Json.JsonSerializer.Serialize(new
        {
            UploadId = "retained", Offset = bytes.Length, RetainUntil = (DateTimeOffset?)null,
            Parts = "<CompleteMultipartUpload><Part><PartNumber>1</PartNumber><ETag>first</ETag></Part><Part><PartNumber>2</PartNumber><ETag>second</ETag></Part></CompleteMultipartUpload>"
        });
        var options = new StoragePutOptions(StorageOverwritePolicy.Fail, checksum, bytes.Length)
        {
            Checkpoint = new(bytes.Length, state),
            RecordCheckpointAsync = (_, _) => Task.CompletedTask
        };
        var metadata = await Provider(http).PutAsync(new("backup.bak"), new MemoryStream(bytes), options, null, default);
        Assert.Equal(bytes.Length, metadata.SizeBytes);
        Assert.Equal(StorageVerificationStatus.Verified, (await Provider(http).VerifyAsync(new("backup.bak"), new(bytes.Length, checksum), default)).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, true)]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public async Task Abort_deletes_the_retained_multipart_upload_and_surfaces_real_failures(HttpStatusCode status, bool succeeds)
    {
        HttpRequestMessage? seen = null;
        using var http = new HttpClient(new Handler(request => { seen = request; return new(status); }));
        var state = System.Text.Json.JsonSerializer.Serialize(new { UploadId = "retained", Offset = 1, RetainUntil = (DateTimeOffset?)null, Parts = "<CompleteMultipartUpload/>" });
        var abort = Provider(http).AbortTransferAsync(new("backup.bak"), state, default);
        if (succeeds) await abort; else await Assert.ThrowsAsync<StorageException>(() => abort);
        Assert.Equal(HttpMethod.Delete, seen!.Method);
        Assert.Contains("uploadId=retained", seen.RequestUri!.Query);
    }

    private sealed class BrokenReadStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Disconnected");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => ValueTask.FromException<int>(new IOException("Disconnected"));
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private static S3StorageProvider Provider(HttpClient http) => new(http,
        new(new Uri("https://storage.example"), "bucket", "us-east-1", Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222")),
        _ => ValueTask.FromResult(new S3Credential("access", "secret")));
    private static HttpResponseMessage Xml(string xml) => new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
