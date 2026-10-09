using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.Agent.Transport;

public sealed class HttpTaskStorageProviderFactory(HttpClient http, AgentBackendEndpoint endpoint, IAgentCredentialStore identities) : ITaskStorageProviderFactory
{
    public IStorageProvider CreateBackup(BackupTaskSnapshot task) => new Provider(http, endpoint, identities,
        $"api/agent/tasks/{task.AttemptId:D}/storage", task.LeaseToken, null);

    public IStorageProvider CreateRestore(RestoreTaskSnapshot task) => new Provider(http, endpoint, identities,
        $"api/agent/restore-tasks/{task.AttemptId:D}/storage", task.LeaseToken, task.OperationId);

    private sealed class Provider(HttpClient http, AgentBackendEndpoint endpoint, IAgentCredentialStore identities,
        string path, Guid lease, Guid? restore) : IStorageProvider
    {
        public StorageProviderId Id { get; } = new("sftp");
        public StorageTopology Topology => StorageTopology.BackendRelayed;
        public StorageCapabilities Capabilities { get; } = new([StorageCapabilityIds.Read, StorageCapabilityIds.Delete,
            StorageCapabilityIds.AtomicPublish, StorageCapabilityIds.VerifyAbsence]);

        public async Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options,
            IProgress<long>? progress, CancellationToken cancellationToken)
        {
            if (restore is not null || options.Overwrite != StorageOverwritePolicy.Fail || options.ExpectedLength is not > 0 || options.ExpectedChecksum is null)
                throw new StorageException(StorageFailureKind.InvalidConfiguration, "A bounded verified backup upload is required.");
            using var body = new StreamContent(content, 64 * 1024);
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            body.Headers.ContentLength = options.ExpectedLength;
            using var response = await SendAsync(HttpMethod.Put, "content", key, body, options.ExpectedChecksum.Hex, cancellationToken).ConfigureAwait(false);
            var result = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
            progress?.Report(result.SizeBytes);
            return Metadata(key, result);
        }

        public async Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken) =>
            await GetMetadataAsync(key, cancellationToken).ConfigureAwait(false) is not null;

        public async Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            using var response = await SendAsync(HttpMethod.Get, "metadata", key, null, null, cancellationToken).ConfigureAwait(false);
            var result = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
            return result.Exists ? Metadata(key, result) : null;
        }

        public async Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken)
        {
            using var content = JsonContent.Create(new TaskStorageVerifyRequest(expected.SizeBytes, expected.Checksum.Hex), options: JsonSerializerOptions.Web);
            using var response = await SendAsync(HttpMethod.Post, "verify", key, content, null, cancellationToken).ConfigureAwait(false);
            var result = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
            if (!Enum.TryParse<StorageVerificationStatus>(result.VerificationStatus, out var status)
                || !Enum.TryParse<StorageVerificationMethod>(result.VerificationMethod, out var method))
                throw new StorageException(StorageFailureKind.Io, "The storage relay response is invalid.");
            return new(new StorageLocation(Id, key), status, method, result.Exists ? result.SizeBytes : null, Checksum(result.Checksum));
        }

        public async Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            using var response = await SendAsync(HttpMethod.Delete, "content", key, null, null, cancellationToken).ConfigureAwait(false);
            return (await ReadAsync(response, cancellationToken).ConfigureAwait(false)).Exists;
        }

        public async Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            var response = await SendAsync(HttpMethod.Get, "content", key, null, null, cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return new ResponseStream(stream, response);
            }
            catch { response.Dispose(); throw; }
        }

        public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) =>
            throw new StorageException(StorageFailureKind.NotSupported, "This storage cannot be enumerated.");

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string operation, StorageObjectKey key,
            HttpContent? content, string? checksum, CancellationToken cancellationToken)
        {
            var identity = await identities.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (identity is null || lease == Guid.Empty)
                throw new StorageException(StorageFailureKind.AccessDenied, "The storage task identity is unavailable.");
            using var request = new HttpRequestMessage(method, new Uri(endpoint.BaseUri, $"{path}/{operation}")) { Content = content };
            request.Headers.TryAddWithoutValidation("Authorization", identity.Current.ToAuthorizationHeaderValue());
            request.Headers.TryAddWithoutValidation(AgentProtocolHeader.Name, AgentProtocol.Current.Version.ToString());
            request.Headers.TryAddWithoutValidation(TaskStorageProtocol.LeaseHeader, lease.ToString("D"));
            request.Headers.TryAddWithoutValidation(TaskStorageProtocol.KeyHeader, key.Value);
            if (checksum is not null) request.Headers.TryAddWithoutValidation(TaskStorageProtocol.ChecksumHeader, checksum);
            if (restore is { } operationId) request.Headers.TryAddWithoutValidation(TaskStorageProtocol.RestoreHeader, operationId.ToString("D"));
            try { return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            { throw new StorageException(StorageFailureKind.Io, "The storage relay is unavailable."); }
        }

        private static async Task<TaskStorageResponse> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            try
            {
                return await response.Content.ReadFromJsonAsync<TaskStorageResponse>(JsonSerializerOptions.Web, cancellationToken).ConfigureAwait(false)
                    ?? throw new StorageException(StorageFailureKind.Io, "The storage relay response is invalid.");
            }
            catch (JsonException) { throw new StorageException(StorageFailureKind.Io, "The storage relay response is invalid."); }
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode) return;
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                var failure = await response.Content.ReadFromJsonAsync<TaskStorageResponse>(JsonSerializerOptions.Web, cancellationToken).ConfigureAwait(false);
                if (Enum.TryParse<StorageFailureKind>(failure?.FailureKind, out var kind))
                    throw new StorageException(kind, "The storage operation failed.");
            }
            throw new StorageException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Conflict
                ? StorageFailureKind.AccessDenied : StorageFailureKind.Io, "The storage relay rejected the operation.");
        }

        private StorageObjectMetadata Metadata(StorageObjectKey key, TaskStorageResponse value) =>
            new(new StorageLocation(Id, key), value.SizeBytes, Checksum(value.Checksum), value.LastModifiedUtc);
        private static ContentChecksum? Checksum(string? value) => value is null ? null : new(ChecksumAlgorithm.Sha256, value);
    }

    private sealed class ResponseStream(Stream stream, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => stream.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { stream.Dispose(); response.Dispose(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await stream.DisposeAsync().ConfigureAwait(false); response.Dispose(); GC.SuppressFinalize(this); }
    }
}
