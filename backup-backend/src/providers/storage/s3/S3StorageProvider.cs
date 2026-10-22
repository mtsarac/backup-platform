using System.Buffers;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using System.Text.Json;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.StorageProviders.S3;

public sealed record S3Credential(string AccessKey, string SecretKey, string? SessionToken = null)
{
    public override string ToString() => "S3Credential { [REDACTED] }";
}

public sealed record S3StorageOptions(Uri Endpoint, string Bucket, string Region, Guid TenantId, Guid ConnectionId,
    string? ObjectLockMode = null, int RetentionDays = 0)
{
    public S3StorageOptions Validate()
    {
        if (!Endpoint.IsAbsoluteUri || Endpoint.Scheme != "https" || Endpoint.UserInfo.Length != 0 || Endpoint.Query.Length != 0
            || Endpoint.Fragment.Length != 0 || Endpoint.AbsolutePath != "/" || Uri.CheckHostName(Endpoint.Host) == UriHostNameType.Unknown
            || Bucket.Length is < 3 or > 63 || Bucket.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.'))
            || !char.IsAsciiLetterOrDigit(Bucket[0]) || !char.IsAsciiLetterOrDigit(Bucket[^1]) || Bucket.Contains("..")
            || Region.Length is < 1 or > 64 || Region.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))
            || TenantId == Guid.Empty || ConnectionId == Guid.Empty
            || ObjectLockMode is not (null or "COMPLIANCE" or "GOVERNANCE")
            || (ObjectLockMode is null ? RetentionDays != 0 : RetentionDays is < 1 or > 36500))
            throw new StorageException(StorageFailureKind.InvalidConfiguration, "The S3 storage configuration is invalid.");
        return this;
    }
}

public sealed class S3StorageProvider(HttpClient http, S3StorageOptions configuration,
    Func<CancellationToken, ValueTask<S3Credential>> credentials) : IStorageProvider, IS3AccessChecker
{
    private readonly S3StorageOptions _options = configuration.Validate();
    public StorageProviderId Id { get; } = new("s3");
    public StorageTopology Topology => StorageTopology.AgentDirect;
    public StorageCapabilities Capabilities { get; } = new(configuration.ObjectLockMode is null
        ? [StorageCapabilityIds.Read, StorageCapabilityIds.Delete, StorageCapabilityIds.AtomicPublish, StorageCapabilityIds.Multipart, StorageCapabilityIds.Resume, StorageCapabilityIds.VerifyAbsence]
        : [StorageCapabilityIds.Read, StorageCapabilityIds.Delete, StorageCapabilityIds.AtomicPublish, StorageCapabilityIds.Multipart, StorageCapabilityIds.Resume, StorageCapabilityIds.VerifyAbsence, StorageCapabilityIds.ImmutableStorage]);

    public async Task CheckAccessAsync(CancellationToken cancellationToken)
    {
        using var bucket = await SendAsync(HttpMethod.Head, null, "", null, null, cancellationToken);
        if (_options.ObjectLockMode is not null)
        {
            using var response = await SendAsync(HttpMethod.Get, null, "object-lock=", null, null, cancellationToken);
            if (!(await ReadXmlAsync(response, cancellationToken)).Descendants().Any(x => x.Name.LocalName == "ObjectLockEnabled" && x.Value == "Enabled"))
                throw new StorageException(StorageFailureKind.InvalidConfiguration, "S3 Object Lock must be enabled on the bucket.");
        }
    }

    public async Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (options.Overwrite != StorageOverwritePolicy.Fail || options.ExpectedLength is not > 0 || options.ExpectedChecksum is null)
            throw new StorageException(StorageFailureKind.InvalidConfiguration, "S3 requires an immutable, bounded, checksummed upload.");
        var headers = new Dictionary<string, string>();
        DateTimeOffset? retainUntil = null;
        if (_options.ObjectLockMode is { } mode)
        {
            retainUntil = DateTimeOffset.UtcNow.AddDays(_options.RetentionDays);
            headers["x-amz-object-lock-mode"] = mode;
            headers["x-amz-object-lock-retain-until-date"] = retainUntil.Value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            // A bucket without Object Lock must fail before any protected data is published.
            using var lockResponse = await SendAsync(HttpMethod.Get, null, "object-lock=", null, null, cancellationToken);
            var lockXml = await ReadXmlAsync(lockResponse, cancellationToken);
            if (!lockXml.Descendants().Any(x => x.Name.LocalName == "ObjectLockEnabled" && x.Value == "Enabled"))
                throw new StorageException(StorageFailureKind.InvalidConfiguration, "S3 Object Lock must be enabled on the bucket.");
        }
        var retained = options.Checkpoint?.ProviderState is { } state ? JsonSerializer.Deserialize<MultipartState>(state) : null;
        if (retained is not null && await GetMetadataAsync(key, cancellationToken) is { } existing &&
            (await VerifyAsync(key, new(options.ExpectedLength.Value, options.ExpectedChecksum), cancellationToken)).Status == StorageVerificationStatus.Verified)
            return existing with { Checksum = options.ExpectedChecksum };
        string uploadId;
        if (retained is null)
        {
            using var start = await SendAsync(HttpMethod.Post, key, "uploads=", null, headers, cancellationToken);
            uploadId = (await ReadXmlAsync(start, cancellationToken)).Descendants().FirstOrDefault(x => x.Name.LocalName == "UploadId")?.Value
                ?? throw new StorageException(StorageFailureKind.Io, "S3 did not return a multipart upload identifier.");
        }
        else
        {
            uploadId = retained.UploadId;
            retainUntil = retained.RetainUntil;
        }
        var parts = retained is null ? new XElement("CompleteMultipartUpload") : XElement.Parse(retained.Parts);
        if (options.RecordCheckpointAsync is { } initial)
            await initial(new(retained?.Offset ?? 0, JsonSerializer.Serialize(new MultipartState(uploadId, parts.ToString(SaveOptions.DisableFormatting), retained?.Offset ?? 0, retainUntil))), cancellationToken);
        var published = false;
        var completionSent = false;
        try
        {
            if (retained is not null)
            {
                var acknowledgedParts = new Dictionary<string, string>(StringComparer.Ordinal);
                string? marker = null;
                do
                {
                    var query = $"uploadId={Escape(uploadId)}" + (marker is null ? "" : $"&part-number-marker={Escape(marker)}");
                    using var list = await SendAsync(HttpMethod.Get, key, query, null, null, cancellationToken);
                    var remote = await ReadXmlAsync(list, cancellationToken);
                    foreach (var part in remote.Descendants().Where(x => x.Name.LocalName == "Part"))
                    {
                        var number = part.Elements().SingleOrDefault(x => x.Name.LocalName == "PartNumber")?.Value;
                        var etag = part.Elements().SingleOrDefault(x => x.Name.LocalName == "ETag")?.Value;
                        if (number is null || etag is null || !acknowledgedParts.TryAdd(number, etag)) throw Integrity();
                    }
                    var next = remote.Descendants().SingleOrDefault(x => x.Name.LocalName == "IsTruncated")?.Value == "true"
                        ? remote.Descendants().SingleOrDefault(x => x.Name.LocalName == "NextPartNumberMarker")?.Value : null;
                    if (next is not null && (!int.TryParse(next, out var nextNumber) ||
                        nextNumber <= (marker is null ? 0 : int.Parse(marker, CultureInfo.InvariantCulture)))) throw Integrity();
                    if (next is null && remote.Descendants().Any(x => x.Name.LocalName == "IsTruncated" && x.Value == "true")) throw Integrity();
                    marker = next;
                } while (marker is not null);
                foreach (var part in parts.Elements())
                {
                    var number = part.Element("PartNumber")?.Value;
                    var acknowledged = number is not null && acknowledgedParts.TryGetValue(number, out var etag) ? etag : null;
                    if (acknowledged is null || acknowledged != part.Element("ETag")?.Value)
                        throw Integrity();
                }
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            var partSize = Math.Max(8L * 1024 * 1024, (options.ExpectedLength.Value + 9999) / 10000);
            if (partSize > 5L * 1024 * 1024 * 1024 || options.ExpectedLength > 5L * 1024 * 1024 * 1024 * 1024)
                throw new StorageException(StorageFailureKind.InvalidConfiguration, "The object exceeds S3 multipart limits.");
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            var resumedPartCount = parts.Elements().Count();
            try
            {
                for (var number = 1; total < options.ExpectedLength; number++)
                {
                    if (number <= resumedPartCount)
                    {
                        var skipped = Math.Min(partSize, options.ExpectedLength.Value - total);
                        while (skipped > 0)
                        {
                            var read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, skipped)), cancellationToken);
                            if (read == 0) throw Integrity();
                            hash.AppendData(buffer, 0, read);
                            total += read;
                            skipped -= read;
                        }
                        if (number == resumedPartCount && total != retained?.Offset) throw Integrity();
                        continue;
                    }
                    var temporary = Path.Combine(Path.GetTempPath(), $"bp-s3-{Guid.NewGuid():N}.part");
                    var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
                        Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan };
                    if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    await using var part = new FileStream(temporary, fileOptions);
                    using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                    var remaining = Math.Min(partSize, options.ExpectedLength.Value - total);
                    while (remaining > 0)
                    {
                        var read = await content.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                        if (read == 0) throw Integrity();
                        await part.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        hash.AppendData(buffer, 0, read);
                        md5.AppendData(buffer, 0, read);
                        total += read;
                        remaining -= read;
                    }
                    part.Position = 0;
                    using var body = new StreamContent(part);
                    body.Headers.ContentLength = part.Length;
                    body.Headers.ContentMD5 = md5.GetHashAndReset();
                    using var response = await SendAsync(HttpMethod.Put, key, $"partNumber={number}&uploadId={Escape(uploadId)}", body, null, cancellationToken);
                    var etag = response.Headers.ETag?.ToString();
                    if (string.IsNullOrEmpty(etag)) throw new StorageException(StorageFailureKind.Io, "S3 did not acknowledge the uploaded part.");
                    parts.Add(new XElement("Part", new XElement("PartNumber", number), new XElement("ETag", etag)));
                    if (options.RecordCheckpointAsync is { } checkpoint)
                        await checkpoint(new(total, JsonSerializer.Serialize(new MultipartState(uploadId, parts.ToString(SaveOptions.DisableFormatting), total, retainUntil))), cancellationToken);
                    progress?.Report(total);
                }
                if (await content.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0
                    || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(options.ExpectedChecksum.Hex))) throw Integrity();
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            using var completeBody = new StringContent(parts.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "application/xml");
            completionSent = true;
            using var completed = await SendAsync(HttpMethod.Post, key, $"uploadId={Escape(uploadId)}", completeBody,
                new Dictionary<string, string> { ["If-None-Match"] = "*" }, cancellationToken);
            var completion = await ReadXmlAsync(completed, cancellationToken);
            if (completion.Name.LocalName == "Error") throw new StorageException(StorageFailureKind.Io, "S3 could not complete the multipart upload.");
            published = true;
            var metadata = await HeadAsync(key, cancellationToken);
            if (metadata is null || metadata.Value.Size != total) throw Integrity();
            if (retainUntil is not null && (metadata.Value.LockMode != _options.ObjectLockMode
                || metadata.Value.RetainUntil is null || metadata.Value.RetainUntil < retainUntil.Value.AddSeconds(-1)))
                throw new StorageException(StorageFailureKind.AccessDenied, "S3 did not confirm the required WORM retention.");
            return new(new(Id, key), total, options.ExpectedChecksum, metadata.Value.Modified);
        }
        catch (StorageException error) when ((published || completionSent) && error.Kind != StorageFailureKind.AlreadyExists)
        {
            throw new StorageException(error.Kind, error.Message, objectMayRemain: true, remainingObjectKey: key);
        }
        catch (OperationCanceledException error) when (completionSent)
        {
            error.Data["StorageFailure"] = new StorageException(StorageFailureKind.Io, "The S3 completion outcome is unknown.", objectMayRemain: true, remainingObjectKey: key);
            throw;
        }
        finally
        {
            if (!published && options.RecordCheckpointAsync is null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { using var aborted = await SendAsync(HttpMethod.Delete, key, $"uploadId={Escape(uploadId)}", null, null, cleanup.Token); }
                catch (Exception error) when (error is StorageException or OperationCanceledException) { }
            }
        }
    }

    private sealed record MultipartState(string UploadId, string Parts, long Offset, DateTimeOffset? RetainUntil);

    public async Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken) => await HeadAsync(key, cancellationToken) is not null;
    public async Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        var head = await HeadAsync(key, cancellationToken);
        return head is { } value ? new(new(Id, key), value.Size, null, value.Modified) : null;
    }
    public async Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken)
    {
        var meta = await GetMetadataAsync(key, cancellationToken);
        if (meta is null) return new(new(Id, key), StorageVerificationStatus.Missing, StorageVerificationMethod.ReadBack, null, null);
        await using var stream = await OpenReadAsync(key, cancellationToken);
        var checksum = ContentChecksum.Sha256(await SHA256.HashDataAsync(stream, cancellationToken));
        return new(new(Id, key), meta.SizeBytes != expected.SizeBytes ? StorageVerificationStatus.SizeMismatch
            : checksum != expected.Checksum ? StorageVerificationStatus.ChecksumMismatch : StorageVerificationStatus.Verified,
            StorageVerificationMethod.ReadBack, meta.SizeBytes, checksum);
    }
    public async Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        var head = await HeadAsync(key, cancellationToken);
        if (head is null) return false;
        if (head.Value.RetainUntil > DateTimeOffset.UtcNow || head.Value.LegalHold)
            throw new StorageException(StorageFailureKind.AccessDenied, "The S3 object is protected by WORM retention or legal hold.");
        // Delete the actual version, never a delete marker that could hide a protected backup.
        using var deleted = await SendAsync(HttpMethod.Delete, key, head.Value.Version is { } version ? "versionId=" + Escape(version) : "", null, null, cancellationToken);
        if (await ExistsAsync(key, cancellationToken)) throw new StorageException(StorageFailureKind.Io, "The S3 object remains after deletion.");
        return true;
    }
    public async Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, key, "", null, null, cancellationToken);
        try { return new ResponseStream(await response.Content.ReadAsStreamAsync(cancellationToken), response); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { response.Dispose(); throw new StorageException(StorageFailureKind.Timeout, "The S3 response timed out."); }
        catch (Exception error) when (error is IOException or HttpRequestException) { response.Dispose(); throw new StorageException(StorageFailureKind.NetworkUnavailable, "The S3 response was interrupted."); }
        catch { response.Dispose(); throw; }
    }
    public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) =>
        throw new StorageException(StorageFailureKind.NotSupported, "S3 enumeration is not available through this provider.");

    private async Task<(long Size, DateTimeOffset Modified, string? Version, string? LockMode, DateTimeOffset? RetainUntil, bool LegalHold)?> HeadAsync(StorageObjectKey key, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Head, key, "", null, null, token, allowMissing: true);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        string? Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        DateTimeOffset? retention = DateTimeOffset.TryParse(Header("x-amz-object-lock-retain-until-date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
        if (Header("x-amz-object-lock-mode") is not null && retention is null)
            throw new StorageException(StorageFailureKind.AccessDenied, "S3 retention information is incomplete.");
        return (response.Content.Headers.ContentLength ?? throw new StorageException(StorageFailureKind.Io, "S3 metadata is incomplete."),
            response.Content.Headers.LastModified ?? DateTimeOffset.UtcNow, Header("x-amz-version-id"), Header("x-amz-object-lock-mode"), retention, Header("x-amz-object-lock-legal-hold") == "ON");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, StorageObjectKey? key, string query, HttpContent? content,
        IReadOnlyDictionary<string, string>? headers, CancellationToken token, bool allowMissing = false)
    {
        var path = "/" + Escape(_options.Bucket) + (key is null ? "" : $"/tenants/{_options.TenantId:N}/connections/{_options.ConnectionId:N}/" + string.Join('/', key.Value.Split('/').Select(Escape)));
        var canonicalQuery = string.Join('&', query.Split('&', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        var uri = new Uri(_options.Endpoint, path + (query.Length == 0 ? "" : "?" + canonicalQuery));
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        if (headers is not null) foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        var credential = await credentials(token);
        if (string.IsNullOrWhiteSpace(credential.AccessKey) || string.IsNullOrWhiteSpace(credential.SecretKey))
            throw new StorageException(StorageFailureKind.AuthenticationFailed, "The S3 credentials are unavailable.");
        var now = DateTimeOffset.UtcNow;
        var date = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var timestamp = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        request.Headers.Host = uri.Authority;
        request.Headers.TryAddWithoutValidation("x-amz-date", timestamp);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", "UNSIGNED-PAYLOAD");
        if (credential.SessionToken is { } session) request.Headers.TryAddWithoutValidation("x-amz-security-token", session);
        var signed = request.Headers.Where(h => h.Key.Equals("host", StringComparison.OrdinalIgnoreCase) || h.Key.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase))
            .Select(h => (Name: h.Key.ToLowerInvariant(), Value: string.Join(',', h.Value).Trim())).OrderBy(h => h.Name, StringComparer.Ordinal).ToArray();
        var names = string.Join(';', signed.Select(h => h.Name));
        var canonical = $"{method}\n{uri.AbsolutePath}\n{canonicalQuery}\n{string.Concat(signed.Select(h => h.Name + ":" + h.Value + "\n"))}\n{names}\nUNSIGNED-PAYLOAD";
        var scope = $"{date}/{_options.Region}/s3/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{timestamp}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        byte[] Hmac(byte[] secret, string text) => HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(text));
        var signing = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + credential.SecretKey), date), _options.Region), "s3"), "aws4_request");
        request.Headers.TryAddWithoutValidation("Authorization", $"AWS4-HMAC-SHA256 Credential={credential.AccessKey}/{scope}, SignedHeaders={names}, Signature={Convert.ToHexStringLower(Hmac(signing, toSign))}");
        CryptographicOperations.ZeroMemory(signing);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new StorageException(StorageFailureKind.Timeout, "The S3 request timed out."); }
        catch (Exception error) when (error is HttpRequestException or IOException) { throw new StorageException(StorageFailureKind.NetworkUnavailable, "The S3 storage is unavailable."); }
        if (response.IsSuccessStatusCode || allowMissing && response.StatusCode == HttpStatusCode.NotFound) return response;
        var status = response.StatusCode;
        response.Dispose();
        throw new StorageException(status switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => StorageFailureKind.AccessDenied,
            HttpStatusCode.NotFound => StorageFailureKind.NotFound,
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => StorageFailureKind.AlreadyExists,
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or HttpStatusCode.TooManyRequests => StorageFailureKind.NetworkUnavailable,
            _ => StorageFailureKind.Io
        }, "The S3 storage rejected the operation.");
    }
    private static string Escape(string value) => Uri.EscapeDataString(value);
    private static StorageException Integrity() => new(StorageFailureKind.IntegrityMismatch, "The S3 upload did not match the expected content.");
    private static async Task<XElement> ReadXmlAsync(HttpResponseMessage response, CancellationToken token)
    {
        try { await using var stream = await response.Content.ReadAsStreamAsync(token); return await XElement.LoadAsync(stream, LoadOptions.None, token); }
        catch (System.Xml.XmlException) { throw new StorageException(StorageFailureKind.Io, "S3 returned invalid metadata."); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new StorageException(StorageFailureKind.Timeout, "The S3 response timed out."); }
        catch (Exception error) when (error is IOException or HttpRequestException) { throw new StorageException(StorageFailureKind.NetworkUnavailable, "The S3 response was interrupted."); }
    }
    private sealed class ResponseStream(Stream stream, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            try { return stream.Read(buffer, offset, count); }
            catch (Exception error) when (error is IOException or HttpRequestException) { throw new StorageException(StorageFailureKind.NetworkUnavailable, "The S3 response was interrupted."); }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            try { return await stream.ReadAsync(buffer, token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new StorageException(StorageFailureKind.Timeout, "The S3 response timed out."); }
            catch (Exception error) when (error is IOException or HttpRequestException) { throw new StorageException(StorageFailureKind.NetworkUnavailable, "The S3 response was interrupted."); }
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { stream.Dispose(); response.Dispose(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await stream.DisposeAsync(); response.Dispose(); GC.SuppressFinalize(this); }
    }
}
