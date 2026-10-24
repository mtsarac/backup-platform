using System.Net;
using System.Buffers;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using BackupPlatform.StorageProviders.GoogleDrive;
using BackupPlatform.StorageProviders.Sftp;
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
using BackupPlatform.StorageProviders.S3;
using BackupPlatform.StorageProviders.Smb;

namespace BackupPlatform.Agent.Transport;

public sealed class HttpTaskStorageProviderFactory(
    HttpClient http,
    AgentBackendEndpoint endpoint,
    IAgentCredentialStore identities,
    string? stagingDirectory = null, IWindowsNetworkSession? windowsSession = null) : ITaskStorageProviderFactory
{
    private readonly string? _stagingDirectory = stagingDirectory ?? Path.Combine(Path.GetTempPath(), "backup-platform-staging");
    private static readonly HttpClient DirectHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2) }) { Timeout = Timeout.InfiniteTimeSpan };

    public IStorageProvider CreateBackup(BackupTaskSnapshot task, BackupDestinationContract destination)
    {
        if (destination.Direct && destination.StorageProvider is "sftp" or "google-drive" or "s3" or "smb" && destination.StorageConnectionId is { } connectionId)
        {
            return new DirectTaskStorageProvider(http, endpoint, identities, task.AttemptId, task.LeaseToken,
                connectionId, task.TenantId, destination.StorageProvider, _stagingDirectory, null, windowsSession);
        }
        return new Provider(http, endpoint, identities,
            $"api/agent/tasks/{task.AttemptId:D}/storage", task.LeaseToken, null, destination.StorageProvider, destination.StorageConnectionId);
    }

    public IStorageProvider CreateRestore(RestoreTaskSnapshot task)
    {
        if (task.Source.Access == "AgentDirect" && task.Source.StorageProvider is "sftp" or "google-drive" or "s3" or "smb" && Guid.TryParse(task.Source.StorageTarget, out var connectionId))
        {
            return new DirectTaskStorageProvider(http, endpoint, identities, task.AttemptId, task.LeaseToken,
                connectionId, task.TenantId, task.Source.StorageProvider, _stagingDirectory, task.OperationId, windowsSession);
        }
        return new Provider(http, endpoint, identities,
            $"api/agent/restore-tasks/{task.AttemptId:D}/storage", task.LeaseToken, task.OperationId, task.Source.StorageProvider, null);
    }

    public IStorageProvider CreateScoped(DirectStorageScope scope) =>
        new DirectTaskStorageProvider(http, endpoint, identities, Guid.Empty, scope.LeaseToken, scope.ConnectionId, scope.TenantId,
            scope.ProviderType, _stagingDirectory, null, windowsSession, scope.AccessPath);

    private sealed class Provider(HttpClient http, AgentBackendEndpoint endpoint, IAgentCredentialStore identities,
        string path, Guid lease, Guid? restore, string providerType, Guid? destination) : IStorageProvider
    {
        public StorageProviderId Id { get; } = new(providerType);
        public StorageTopology Topology => StorageTopology.BackendRelayed;
        public StorageCapabilities Capabilities { get; } = new([StorageCapabilityIds.Read, StorageCapabilityIds.Delete,
            StorageCapabilityIds.AtomicPublish, StorageCapabilityIds.VerifyAbsence]);

        public async Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options,
            IProgress<long>? progress, CancellationToken cancellationToken)
        {
            if (restore is not null || options.Overwrite != StorageOverwritePolicy.Fail || options.ExpectedLength is not > 0 || options.ExpectedChecksum is null)
                throw new StorageException(StorageFailureKind.InvalidConfiguration, "A bounded verified backup upload is required.");
            using var body = new BorrowedUploadContent(content, options.ExpectedLength.Value);
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
            if (destination is { } destinationId) request.Headers.TryAddWithoutValidation(TaskStorageProtocol.DestinationHeader, destinationId.ToString("D"));
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
                    throw new StorageException(kind, "The storage operation failed.", objectMayRemain: failure!.ObjectMayRemain,
                        remainingObjectKey: failure.RemainingObjectKey is { } key ? new StorageObjectKey(key) : null);
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

    private sealed class BorrowedUploadContent(Stream source, long length) : HttpContent
    {
        protected override bool TryComputeLength(out long computedLength) { computedLength = length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => source.CopyToAsync(stream);
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken) =>
            source.CopyToAsync(stream, cancellationToken);
    }

    private sealed class DirectTaskStorageProvider(
        HttpClient http, AgentBackendEndpoint endpoint, IAgentCredentialStore identities,
        Guid attemptId, Guid lease, Guid connectionId, Guid tenantId, string providerType,
        string? stagingDirectory, Guid? restoreOperationId, IWindowsNetworkSession? windowsSession, string? scopedAccessPath = null) : IStorageProvider, IScopedDriveRecordSource
    {
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private IStorageProvider? _targetProvider;
        private InMemoryGoogleDriveFileCatalog? _scopedCatalog;
        private string? _cachedAccessToken;
        private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;

        public StorageProviderId Id => _targetProvider?.Id ?? new(providerType);
        public StorageTopology Topology => StorageTopology.AgentDirect;
        public StorageCapabilities Capabilities => _targetProvider?.Capabilities ?? new([
            StorageCapabilityIds.Read, StorageCapabilityIds.Delete, StorageCapabilityIds.AtomicPublish, StorageCapabilityIds.VerifyAbsence]);

        public async Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options,
            IProgress<long>? progress, CancellationToken cancellationToken)
        {
            if (scopedAccessPath is null && restoreOperationId is null)
                await RecordIntentAsync(key, options, cancellationToken).ConfigureAwait(false);
            var target = await GetProviderAsync(cancellationToken).ConfigureAwait(false);
            return await target.PutAsync(key, content, options, progress, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            var target = await GetProviderAsync(cancellationToken).ConfigureAwait(false);
            return await target.ExistsAsync(key, cancellationToken).ConfigureAwait(false);
        }

        public async Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            var target = await GetProviderAsync(cancellationToken).ConfigureAwait(false);
            return await target.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        }

        public async Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken)
        {
            var target = await GetProviderAsync(cancellationToken).ConfigureAwait(false);
            return await target.VerifyAsync(key, expected, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            var target = await GetProviderAsync(cancellationToken).ConfigureAwait(false);
            return await target.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        }

        public async Task<TaskStorageCatalogRecord?> FindRecordAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            if (_scopedCatalog is null)
            {
                return null;
            }
            var record = await _scopedCatalog.FindAsync(tenantId, connectionId, key.Value, cancellationToken).ConfigureAwait(false);
            return record is null ? null : new TaskStorageCatalogRecord(record.FileId, record.ParentFolderId, record.FileName, record.SizeBytes, record.Checksum, record.Md5Checksum);
        }

        public async Task AbortTransferAsync(StorageObjectKey key, string providerState, CancellationToken cancellationToken)
        {
            var target = await GetProviderAsync(cancellationToken).ConfigureAwait(false);
            await target.AbortTransferAsync(key, providerState, cancellationToken).ConfigureAwait(false);
        }

        public async Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            var target = await GetProviderAsync(cancellationToken).ConfigureAwait(false);
            return await target.OpenReadAsync(key, cancellationToken).ConfigureAwait(false);
        }

        public async IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var target = await GetProviderAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var entry in target.EnumerateAsync(prefix, modifiedBefore, cancellationToken).ConfigureAwait(false))
                yield return entry;
        }

        private async ValueTask<IStorageProvider> GetProviderAsync(CancellationToken cancellationToken)
        {
            if (_targetProvider is not null) return _targetProvider;
            await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_targetProvider is not null) return _targetProvider;
                var access = await FetchAccessAsync(cancellationToken).ConfigureAwait(false);
                if (access.ProviderType is "s3" or "smb")
                {
                    var config = access.Configuration ?? throw new StorageException(StorageFailureKind.InvalidConfiguration, "Missing network storage configuration.");
                    if (access.ProviderType == "s3")
                    {
                        var options = new S3StorageOptions(new Uri(config["endpoint"]), config["bucket"], config["region"], tenantId, connectionId,
                            config.GetValueOrDefault("objectLockMode"), int.Parse(config.GetValueOrDefault("retentionDays", "0"), System.Globalization.CultureInfo.InvariantCulture));
                        var credential = new S3Credential(access.AccessKey ?? "", access.SecretKey ?? "", access.SessionToken);
                        _targetProvider = new S3StorageProvider(http, options, _ => ValueTask.FromResult(credential));
                    }
                    else
                    {
                        var credential = new SmbCredential(access.Username ?? "", access.Password ?? "", access.Domain);
                        _targetProvider = new SmbStorageProvider(new(config["host"], config["share"], tenantId, connectionId),
                            _ => ValueTask.FromResult(credential), windowsSession);
                    }
                    return _targetProvider;
                }
                if (access.ProviderType == "sftp")
                {
                    if (string.IsNullOrWhiteSpace(access.RemoteBaseDirectory))
                        throw new StorageException(StorageFailureKind.InvalidConfiguration, "Missing SFTP remote base directory.");
                    if (access.AuthenticationType is not (SftpConfiguration.AuthTypePassword or SftpConfiguration.AuthTypePrivateKey))
                        throw new StorageException(StorageFailureKind.InvalidConfiguration, $"Unsupported SFTP authentication type '{access.AuthenticationType}'.");

                    var options = new SftpStorageOptions(
                        access.Host ?? throw new StorageException(StorageFailureKind.InvalidConfiguration, "Missing SFTP host."),
                        access.Port > 0 ? access.Port : 22,
                        access.Username ?? throw new StorageException(StorageFailureKind.InvalidConfiguration, "Missing SFTP username."),
                        access.AuthenticationType,
                        access.RemoteBaseDirectory,
                        access.HostKeyFingerprint ?? throw new StorageException(StorageFailureKind.InvalidConfiguration, "Missing SFTP host key fingerprint."),
                        tenantId,
                        connectionId);
                    var credential = new SftpCredential(access.Password, access.PrivateKey, access.Passphrase);
                    _targetProvider = new SftpStorageProvider(options, _ => ValueTask.FromResult(credential));
                    return _targetProvider;
                }
                if (access.ProviderType == "google-drive")
                {
                    var options = new GoogleDriveStorageOptions(
                        tenantId,
                        connectionId,
                        folderId: access.FolderId,
                        stagingDirectory: stagingDirectory);
                    // A delete or repair scope has no attempt catalog: the provider works from an in-memory one and finds objects by their Drive properties.
                    IGoogleDriveFileCatalog catalog = scopedAccessPath is null
                        ? new BackendGoogleDriveFileCatalog(http, endpoint, identities, attemptId, lease, connectionId, restoreOperationId)
                        : _scopedCatalog = new InMemoryGoogleDriveFileCatalog();
                    _targetProvider = GoogleDriveStorageProvider.CreateDirect(
                        http,
                        options,
                        async token =>
                        {
                            if (_cachedAccessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiresAt - TimeSpan.FromMinutes(2))
                            {
                                return _cachedAccessToken;
                            }
                            var refreshed = await FetchAccessAsync(token).ConfigureAwait(false);
                            return refreshed.AccessToken ?? throw new StorageException(StorageFailureKind.AuthenticationFailed, "No Google Drive access token returned.");
                        },
                        catalog: catalog,
                        stagingStore: !string.IsNullOrWhiteSpace(stagingDirectory) ? new AgentGoogleDriveStagingStore(stagingDirectory) : null);
                    return _targetProvider;
                }
                throw new StorageException(StorageFailureKind.NotSupported, $"Unsupported direct storage provider {access.ProviderType}.");
            }
            finally
            {
                _initLock.Release();
            }
        }

        private async Task RecordIntentAsync(StorageObjectKey key, StoragePutOptions options, CancellationToken cancellationToken)
        {
            if (restoreOperationId is not null || options.ExpectedLength is not > 0 || options.ExpectedChecksum is null)
                throw new StorageException(StorageFailureKind.InvalidConfiguration, "A bounded checksummed backup upload is required.");
            var identity = await identities.LoadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new StorageException(StorageFailureKind.AccessDenied, "The task identity is unavailable.");
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseUri, $"api/agent/tasks/{attemptId:D}/storage/intent"))
            {
                Content = JsonContent.Create(new TaskStorageVerifyRequest(options.ExpectedLength.Value, options.ExpectedChecksum.Hex), options: JsonSerializerOptions.Web)
            };
            request.Headers.TryAddWithoutValidation("Authorization", identity.Current.ToAuthorizationHeaderValue());
            request.Headers.TryAddWithoutValidation(AgentProtocolHeader.Name, AgentProtocol.Current.Version.ToString());
            request.Headers.TryAddWithoutValidation(TaskStorageProtocol.LeaseHeader, lease.ToString("D"));
            request.Headers.TryAddWithoutValidation(TaskStorageProtocol.DestinationHeader, connectionId.ToString("D"));
            request.Headers.TryAddWithoutValidation(TaskStorageProtocol.KeyHeader, key.Value);
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new StorageException(StorageFailureKind.AccessDenied, "The backend refused the direct upload intent.");
        }

        private async Task<TaskStorageAccessResponse> FetchAccessAsync(CancellationToken cancellationToken)
        {
            var identity = await identities.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (identity is null || lease == Guid.Empty)
                throw new StorageException(StorageFailureKind.AccessDenied, "The storage task identity is unavailable.");

            var relativePath = scopedAccessPath ?? (restoreOperationId is null
                ? $"api/agent/tasks/{attemptId:D}/storage/access"
                : $"api/agent/restore-tasks/{attemptId:D}/storage/access");

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseUri, relativePath));
            request.Headers.TryAddWithoutValidation("Authorization", identity.Current.ToAuthorizationHeaderValue());
            request.Headers.TryAddWithoutValidation(AgentProtocolHeader.Name, AgentProtocol.Current.Version.ToString());
            request.Headers.TryAddWithoutValidation(TaskStorageProtocol.LeaseHeader, lease.ToString("D"));
            if (scopedAccessPath is null && restoreOperationId is null)
                request.Headers.TryAddWithoutValidation(TaskStorageProtocol.DestinationHeader, connectionId.ToString("D"));
            else if (restoreOperationId is { } restore)
                request.Headers.TryAddWithoutValidation(TaskStorageProtocol.RestoreHeader, restore.ToString("D"));

            try
            {
                using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new StorageException(StorageFailureKind.AccessDenied, "Failed to resolve direct storage access.");
                var access = await response.Content.ReadFromJsonAsync<TaskStorageAccessResponse>(options: JsonSerializerOptions.Web, cancellationToken).ConfigureAwait(false)
                    ?? throw new StorageException(StorageFailureKind.Io, "Invalid direct storage access response.");
                if (access.AccessToken is not null)
                {
                    _cachedAccessToken = access.AccessToken;
                    _accessTokenExpiresAt = access.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(45);
                }
                return access;
            }
            catch (Exception exception) when (exception is not StorageException && exception is HttpRequestException or IOException)
            {
                throw new StorageException(StorageFailureKind.NetworkUnavailable, "The storage service is unavailable.", exception);
            }
        }
    }

    private sealed class AgentGoogleDriveStagingStore(string stagingDirectory) : IGoogleDriveStagingStore
    {
        public async Task<GoogleDriveStagedFile> StageAsync(Stream content, long expectedLength, ContentChecksum checksum, CancellationToken token)
        {
            var path = Path.Combine(stagingDirectory, $"gdrive_{Guid.NewGuid():N}.staging");
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(65536);
                    try
                    {
                        long length = 0;
                        int count;
                        while ((count = await content.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                        {
                            if (count > expectedLength - length)
                                throw new StorageException(StorageFailureKind.IntegrityMismatch, "Staged length exceeded expectation.");
                            hash.AppendData(buffer, 0, count);
                            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                            length += count;
                        }
                        if (length != expectedLength || !ContentChecksum.Sha256(hash.GetHashAndReset()).Equals(checksum))
                            throw new StorageException(StorageFailureKind.IntegrityMismatch, "Staged file checksum mismatch.");
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
                return new GoogleDriveStagedFile(path, token, () =>
                {
                    try { File.Delete(path); } catch { }
                    return ValueTask.CompletedTask;
                });
            }
            catch
            {
                try { File.Delete(path); } catch { }
                throw;
            }
        }
    }

    private sealed class BackendGoogleDriveFileCatalog(
        HttpClient http, AgentBackendEndpoint endpoint, IAgentCredentialStore identities,
        Guid attemptId, Guid lease, Guid connectionId, Guid? restoreOperationId) : IGoogleDriveFileCatalog
    {
        public async Task<GoogleDriveFileRecord?> FindAsync(Guid tenantId, Guid targetConnectionId, string storageKey, CancellationToken cancellationToken)
        {
            var relative = restoreOperationId is null
                ? $"api/agent/tasks/{attemptId:D}/storage/catalog?key={Uri.EscapeDataString(storageKey)}"
                : $"api/agent/restore-tasks/{attemptId:D}/storage/catalog?key={Uri.EscapeDataString(storageKey)}";
            using var request = CreateRequest(HttpMethod.Get, relative);
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
                throw new StorageException(StorageFailureKind.AccessDenied, "Failed to query Google Drive catalog.");
            var record = await response.Content.ReadFromJsonAsync<TaskStorageCatalogRecord>(options: JsonSerializerOptions.Web, cancellationToken).ConfigureAwait(false);
            return record is null ? null : new GoogleDriveFileRecord(record.FileId, record.ParentFolderId, record.FileName, record.SizeBytes, record.Checksum, record.Md5Checksum);
        }

        public async Task SaveAsync(Guid tenantId, Guid targetConnectionId, string storageKey, GoogleDriveFileRecord record, CancellationToken cancellationToken)
        {
            var relative = $"api/agent/tasks/{attemptId:D}/storage/catalog";
            using var request = CreateRequest(HttpMethod.Post, relative);
            request.Content = JsonContent.Create(new TaskStorageCatalogSaveRequest(
                storageKey,
                new TaskStorageCatalogRecord(record.FileId, record.ParentFolderId, record.FileName, record.SizeBytes, record.Checksum, record.Md5Checksum)));
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new StorageException(StorageFailureKind.AccessDenied, "Failed to save Google Drive catalog record.");
        }

        public async Task DeleteAsync(Guid tenantId, Guid targetConnectionId, string storageKey, string expectedFileId, CancellationToken cancellationToken)
        {
            var relative = $"api/agent/tasks/{attemptId:D}/storage/catalog?key={Uri.EscapeDataString(storageKey)}&expectedFileId={Uri.EscapeDataString(expectedFileId)}";
            using var request = CreateRequest(HttpMethod.Delete, relative);
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new StorageException(StorageFailureKind.AccessDenied, "Failed to delete Google Drive catalog record.");
        }

        public Task<IReadOnlyList<GoogleDriveCatalogEntry>> ListAsync(Guid tenantId, Guid targetConnectionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<GoogleDriveCatalogEntry>>([]);

        public Task ReportFindingAsync(Guid tenantId, Guid targetConnectionId, GoogleDriveReconciliationFinding finding, CancellationToken cancellationToken)
            => Task.CompletedTask;

        private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
        {
            var request = new HttpRequestMessage(method, new Uri(endpoint.BaseUri, relativePath));
            request.Headers.TryAddWithoutValidation(AgentProtocolHeader.Name, AgentProtocol.Current.Version.ToString());
            request.Headers.TryAddWithoutValidation(TaskStorageProtocol.LeaseHeader, lease.ToString("D"));
            if (restoreOperationId is null)
                request.Headers.TryAddWithoutValidation(TaskStorageProtocol.DestinationHeader, connectionId.ToString("D"));
            else
                request.Headers.TryAddWithoutValidation(TaskStorageProtocol.RestoreHeader, restoreOperationId.Value.ToString("D"));
            return request;
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var identity = await identities.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (identity is not null)
                request.Headers.TryAddWithoutValidation("Authorization", identity.Current.ToAuthorizationHeaderValue());
            return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
