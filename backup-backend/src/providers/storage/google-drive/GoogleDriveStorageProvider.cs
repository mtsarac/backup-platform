using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.StorageProviders.GoogleDrive;

public sealed class GoogleDriveStorageProvider : IStorageProvider
{
    public static StorageProviderId ProviderId { get; } = new("google-drive");

    public static StorageCapabilities GoogleDriveCapabilities { get; } = new([
        StorageCapabilityIds.Read,
        StorageCapabilityIds.Delete,
        StorageCapabilityIds.AtomicPublish,
        StorageCapabilityIds.VerifyAbsence
    ]);

    private const string ApiBaseUrl = "https://www.googleapis.com/drive/v3";
    private const string UploadBaseUrl = "https://www.googleapis.com/upload/drive/v3/files";
    private const string UploadQuery = "uploadType=resumable&fields=id,name,size,md5Checksum,modifiedTime,trashed";

    private readonly HttpClient _http;
    private readonly GoogleDriveStorageOptions _options;
    private readonly GoogleDriveTokenManager _tokenManager;
    private readonly IGoogleDriveFileCatalog _catalog;
    private readonly SemaphoreSlim _folderLock = new(1, 1);

    private string? _resolvedFolderId;

    public GoogleDriveStorageProvider(
        HttpClient http,
        GoogleDriveStorageOptions options,
        Func<CancellationToken, ValueTask<GoogleDriveCredential>> credentials,
        IGoogleDriveFileCatalog? catalog = null,
        Func<string, CancellationToken, Task>? onTokenRotated = null,
        Func<Func<CancellationToken, ValueTask<string>>, CancellationToken, ValueTask<string>>? refreshCoordinator = null,
        Func<CancellationToken, Task>? onReauthorizationRequired = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(credentials);

        _tokenManager = new GoogleDriveTokenManager(http, options, credentials, onTokenRotated, refreshCoordinator, onReauthorizationRequired);
        _catalog = catalog ?? new InMemoryGoogleDriveFileCatalog();
        _resolvedFolderId = options.FolderId;
    }

    public StorageProviderId Id => ProviderId;

    public StorageTopology Topology => StorageTopology.BackendRelayed;

    public StorageCapabilities Capabilities => GoogleDriveCapabilities;

    public async Task<StorageObjectMetadata> PutAsync(
        StorageObjectKey key,
        Stream content,
        StoragePutOptions options,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);

        if (!content.CanRead) throw new ArgumentException("The content stream must be readable.", nameof(content));
        if (options.ExpectedLength is null or <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Google Drive upload requires an explicit expected length.");
        if (options.ExpectedChecksum is null)
            throw new ArgumentException("Google Drive upload requires an authoritative checksum.", nameof(options));

        cancellationToken.ThrowIfCancellationRequested();

        var folderId = await EnsureTargetFolderAsync(cancellationToken).ConfigureAwait(false);
        var fileName = ExtractFileName(key);

        // Check if file already exists in target folder
        var existingFile = await FindFileInFolderAsync(folderId, fileName, key.Value, cancellationToken).ConfigureAwait(false);
        if (existingFile is not null)
        {
            if (options.Overwrite == StorageOverwritePolicy.Fail)
            {
                // Idempotency: if size matches and verify matches, return existing metadata
                if (existingFile.Size == options.ExpectedLength.Value)
                {
                    var verification = await VerifyAsync(key, new StorageVerificationExpectation(options.ExpectedLength.Value, options.ExpectedChecksum), cancellationToken).ConfigureAwait(false);
                    if (verification.Status == StorageVerificationStatus.Verified)
                    {
                        return new StorageObjectMetadata(new StorageLocation(Id, key), existingFile.Size, options.ExpectedChecksum, existingFile.ModifiedTime);
                    }
                }

                throw new StorageException(StorageFailureKind.AlreadyExists, "Target object already exists in Google Drive.");
            }
        }

        // Ensure staging directory exists and has bounded capacity
        Directory.CreateDirectory(_options.StagingDirectory);
        CheckStagingCapacity(options.ExpectedLength.Value);

        var tempFilePath = Path.Combine(_options.StagingDirectory, $"{_options.TenantId:N}_{_options.ConnectionId:N}_{Guid.NewGuid():N}.tmp");
        try
        {
            // Stage incoming stream to local temporary file to allow re-reading during resumable chunk upload
            long stagedLength;
            string stagedChecksum;
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows())
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var fileStream = new FileStream(tempFilePath, fileOptions))
            {
                using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    long totalRead = 0;
                    while (true)
                    {
                        var bytesRead = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                        if (bytesRead == 0) break;
                        if (bytesRead > options.ExpectedLength.Value - totalRead)
                        {
                            throw new StorageException(StorageFailureKind.IntegrityMismatch, "Stream exceeds the expected length.");
                        }
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                        sha256.AppendData(buffer, 0, bytesRead);
                        totalRead += bytesRead;
                    }
                    stagedLength = totalRead;
                    stagedChecksum = Convert.ToHexStringLower(sha256.GetHashAndReset());
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            // Verify staged bytes before initiating Google Drive upload
            if (stagedLength != options.ExpectedLength.Value)
            {
                throw new StorageException(StorageFailureKind.IntegrityMismatch, $"Stream length mismatch: received {stagedLength}, expected {options.ExpectedLength.Value}.");
            }
            if (!string.Equals(stagedChecksum, options.ExpectedChecksum.Hex, StringComparison.OrdinalIgnoreCase))
            {
                throw new StorageException(StorageFailureKind.IntegrityMismatch, "Stream checksum mismatch.");
            }

            // Perform Google Drive resumable upload from staged file
            var fileResult = await UploadResumableFileAsync(tempFilePath, folderId, fileName, key.Value, stagedLength, stagedChecksum, existingFile?.Id, progress, cancellationToken).ConfigureAwait(false);

            var record = new GoogleDriveFileRecord(fileResult.Id, folderId, fileName, fileResult.Size, options.ExpectedChecksum.Hex, fileResult.Md5Checksum);
            await _catalog.SaveAsync(_options.TenantId, _options.ConnectionId, key.Value, record, cancellationToken).ConfigureAwait(false);

            return new StorageObjectMetadata(
                new StorageLocation(Id, key),
                fileResult.Size,
                options.ExpectedChecksum,
                fileResult.ModifiedTime);
        }
        finally
        {
            try
            {
                if (File.Exists(tempFilePath)) File.Delete(tempFilePath);
            }
            catch
            {
                // Staging file cleanup is best-effort
            }
        }
    }

    public async Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        var meta = await GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        return meta is not null;
    }

    public async Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Try local catalog first
        var cached = await _catalog.FindAsync(_options.TenantId, _options.ConnectionId, key.Value, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            var googleFile = await GetFileByIdAsync(cached.FileId, cancellationToken).ConfigureAwait(false);
            if (googleFile is null || googleFile.Trashed)
            {
                // Drift detected: file was removed from Google Drive externally
                await _catalog.DeleteAsync(_options.TenantId, _options.ConnectionId, key.Value, cancellationToken).ConfigureAwait(false);
                return null;
            }

            ContentChecksum? checksum = !string.IsNullOrWhiteSpace(cached.Checksum)
                ? new ContentChecksum(ChecksumAlgorithm.Sha256, cached.Checksum)
                : null;

            return new StorageObjectMetadata(new StorageLocation(Id, key), googleFile.Size, checksum, googleFile.ModifiedTime);
        }

        // 2. Fall back to querying Google Drive
        var folderId = await EnsureTargetFolderAsync(cancellationToken).ConfigureAwait(false);
        var fileName = ExtractFileName(key);
        var found = await FindFileInFolderAsync(folderId, fileName, key.Value, cancellationToken).ConfigureAwait(false);
        if (found is null) return null;

        var record = new GoogleDriveFileRecord(found.Id, folderId, fileName, found.Size, found.AppProperties?.GetValueOrDefault("sha256"), found.Md5Checksum);
        await _catalog.SaveAsync(_options.TenantId, _options.ConnectionId, key.Value, record, cancellationToken).ConfigureAwait(false);

        ContentChecksum? cs = !string.IsNullOrWhiteSpace(record.Checksum)
            ? new ContentChecksum(ChecksumAlgorithm.Sha256, record.Checksum)
            : null;

        return new StorageObjectMetadata(new StorageLocation(Id, key), found.Size, cs, found.ModifiedTime);
    }

    public async Task<StorageVerificationResult> VerifyAsync(
        StorageObjectKey key,
        StorageVerificationExpectation expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(expected);
        cancellationToken.ThrowIfCancellationRequested();

        var meta = await GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        if (meta is null)
        {
            return new StorageVerificationResult(new StorageLocation(Id, key), StorageVerificationStatus.Missing, StorageVerificationMethod.ReadBack, null, null);
        }

        if (meta.SizeBytes != expected.SizeBytes)
        {
            return new StorageVerificationResult(new StorageLocation(Id, key), StorageVerificationStatus.SizeMismatch, StorageVerificationMethod.SizeOnly, meta.SizeBytes, null);
        }

        // ReadBack verification: stream from Google Drive and compute authoritative SHA-256
        await using var stream = await OpenReadAsync(key, cancellationToken).ConfigureAwait(false);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long totalBytes = 0;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                sha256.AppendData(buffer, 0, read);
                totalBytes += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (totalBytes != expected.SizeBytes)
        {
            return new StorageVerificationResult(new StorageLocation(Id, key), StorageVerificationStatus.SizeMismatch, StorageVerificationMethod.ReadBack, totalBytes, null);
        }

        var actualChecksum = new ContentChecksum(ChecksumAlgorithm.Sha256, Convert.ToHexStringLower(sha256.GetHashAndReset()));
        var status = actualChecksum.Equals(expected.Checksum)
            ? StorageVerificationStatus.Verified
            : StorageVerificationStatus.ChecksumMismatch;

        return new StorageVerificationResult(new StorageLocation(Id, key), status, StorageVerificationMethod.ReadBack, totalBytes, actualChecksum);
    }

    public async Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        var fileId = await ResolveFileIdAsync(key, cancellationToken).ConfigureAwait(false);
        if (fileId is null)
        {
            throw new StorageException(StorageFailureKind.NotFound, "The requested file does not exist in Google Drive.");
        }

        var accessToken = await _tokenManager.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/files/{fileId}?alt=media");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new StorageException(StorageFailureKind.NotFound, "The requested file was not found in Google Drive.");
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new StorageException(StorageFailureKind.AccessDenied, "Access denied reading file from Google Drive.");
        }

        throw new StorageException(StorageFailureKind.Io, $"Failed to open read stream from Google Drive (HTTP {response.StatusCode}).");
    }

    public async Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        var fileId = await ResolveFileIdAsync(key, cancellationToken).ConfigureAwait(false);
        if (fileId is null)
        {
            // Already absent: idempotent success
            await _catalog.DeleteAsync(_options.TenantId, _options.ConnectionId, key.Value, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var deleted = await DeleteFileByIdAsync(fileId, cancellationToken).ConfigureAwait(false);
        await _catalog.DeleteAsync(_options.TenantId, _options.ConnectionId, key.Value, cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken)
    {
        throw new StorageException(StorageFailureKind.NotSupported, "Google Drive enumeration is not supported.");
    }

    private async Task<string?> ResolveFileIdAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        var cached = await _catalog.FindAsync(_options.TenantId, _options.ConnectionId, key.Value, cancellationToken).ConfigureAwait(false);
        if (cached is not null) return cached.FileId;

        var meta = await GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        if (meta is null) return null;

        var rec = await _catalog.FindAsync(_options.TenantId, _options.ConnectionId, key.Value, cancellationToken).ConfigureAwait(false);
        return rec?.FileId;
    }

    private async Task<bool> DeleteFileByIdAsync(string fileId, CancellationToken cancellationToken)
    {
        var accessToken = await _tokenManager.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"{ApiBaseUrl}/files/{fileId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK)
        {
            return true;
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new StorageException(StorageFailureKind.AccessDenied, "Google Drive authorization failed during delete.");
        }
        if ((int)response.StatusCode >= 500 || response.StatusCode == (HttpStatusCode)429)
        {
            throw new StorageException(StorageFailureKind.NetworkUnavailable, "Google Drive service unavailable during delete.");
        }

        throw new StorageException(StorageFailureKind.Io, $"Failed to delete file from Google Drive (HTTP {response.StatusCode}).");
    }

    private async Task<DriveFileInfo?> GetFileByIdAsync(string fileId, CancellationToken cancellationToken)
    {
        var accessToken = await _tokenManager.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/files/{fileId}?fields=id,name,size,md5Checksum,trashed,modifiedTime,appProperties");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<DriveFileInfo>(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new StorageException(StorageFailureKind.AccessDenied, "Access denied checking file in Google Drive.");
        }

        throw new StorageException(StorageFailureKind.Io, $"Error querying Google Drive file (HTTP {response.StatusCode}).");
    }

    private async Task<DriveFileInfo?> FindFileInFolderAsync(string folderId, string fileName, string storageKey, CancellationToken cancellationToken)
    {
        var accessToken = await _tokenManager.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var keyHash = StorageKeyHash(storageKey);
        var query = $"'{EscapeQuery(folderId)}' in parents and name = '{EscapeQuery(fileName)}' and trashed = false"
            + $" and appProperties has {{ key='storageKeyHash' and value='{keyHash}' }}"
            + $" and appProperties has {{ key='tenantId' and value='{_options.TenantId:N}' }}"
            + $" and appProperties has {{ key='connectionId' and value='{_options.ConnectionId:N}' }}";
        var url = $"{ApiBaseUrl}/files?q={Uri.EscapeDataString(query)}&fields=files(id,name,size,md5Checksum,trashed,modifiedTime,appProperties)&pageSize=10";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            var list = await response.Content.ReadFromJsonAsync<DriveFileList>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (list?.Files is { Count: > 0 } files)
            {
                return files.FirstOrDefault(f => f.AppProperties is { } properties
                    && properties.GetValueOrDefault("storageKeyHash") == keyHash
                    && properties.GetValueOrDefault("tenantId") == _options.TenantId.ToString("N")
                    && properties.GetValueOrDefault("connectionId") == _options.ConnectionId.ToString("N"));
            }
            return null;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new StorageException(StorageFailureKind.AccessDenied, "Access denied searching Google Drive folder.");
        }

        throw new StorageException(StorageFailureKind.Io, $"Failed to search Google Drive folder (HTTP {response.StatusCode}).");
    }

    /// <summary>Drive caps each appProperties key+value at 124 bytes and real storage keys are longer, so the property holds a fixed-size digest of the key.</summary>
    private static string StorageKeyHash(string storageKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(storageKey)));

    private async Task<DriveFileInfo> UploadResumableFileAsync(
        string tempFilePath,
        string folderId,
        string fileName,
        string storageKey,
        long totalLength,
        string sha256Hex,
        string? existingFileId,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        var sessionUri = await InitiateResumableUploadSessionAsync(folderId, fileName, storageKey, totalLength, sha256Hex, existingFileId, cancellationToken).ConfigureAwait(false);

        var attempts = 0;
        const int maxAttempts = 5;
        long currentOffset = 0;

        await using var fileStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);

        while (currentOffset < totalLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkSize = (int)Math.Min(_options.ChunkSizeBytes, totalLength - currentOffset);
            var chunkBytes = ArrayPool<byte>.Shared.Rent(chunkSize);
            try
            {
                fileStream.Position = currentOffset;
                var bytesRead = 0;
                while (bytesRead < chunkSize)
                {
                    var read = await fileStream.ReadAsync(chunkBytes.AsMemory(bytesRead, chunkSize - bytesRead), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    bytesRead += read;
                }

                var chunkEnd = currentOffset + bytesRead - 1;
                using var request = new HttpRequestMessage(HttpMethod.Put, sessionUri);
                request.Content = new ByteArrayContent(chunkBytes, 0, bytesRead);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Content.Headers.ContentLength = bytesRead;
                request.Content.Headers.Add("Content-Range", $"bytes {currentOffset}-{chunkEnd}/{totalLength}");

                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == (HttpStatusCode)308) // Resume Incomplete
                {
                    attempts = 0;
                    currentOffset = ParseRangeHeaderNextOffset(response.Headers, chunkEnd + 1);
                    progress?.Report(currentOffset);
                    continue;
                }

                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                {
                    var result = await response.Content.ReadFromJsonAsync<DriveFileInfo>(cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (result is null || string.IsNullOrWhiteSpace(result.Id))
                    {
                        throw new StorageException(StorageFailureKind.Io, "Google Drive upload succeeded but returned invalid metadata.");
                    }
                    progress?.Report(totalLength);
                    return result;
                }

                // Handle session expiration (404/410)
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    // Session expired: restart from offset 0
                    sessionUri = await InitiateResumableUploadSessionAsync(folderId, fileName, storageKey, totalLength, sha256Hex, existingFileId, cancellationToken).ConfigureAwait(false);
                    currentOffset = 0;
                    attempts = 0;
                    continue;
                }

                // Rate limited or 5xx
                if (response.StatusCode == (HttpStatusCode)429 || (int)response.StatusCode >= 500)
                {
                    attempts++;
                    if (attempts >= maxAttempts)
                    {
                        throw new StorageException(StorageFailureKind.NetworkUnavailable, $"Google Drive resumable upload failed after {maxAttempts} attempts.");
                    }

                    var retryDelay = GetRetryAfterDelay(response.Headers, attempts);
                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);

                    // Query server for actual received byte offset before retrying
                    currentOffset = await QueryUploadOffsetAsync(sessionUri, totalLength, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    var errText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    if (errText.Contains("storageQuotaExceeded", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new StorageException(StorageFailureKind.InsufficientSpace, "Google Drive storage quota exceeded.");
                    }
                    throw new StorageException(StorageFailureKind.AccessDenied, "Google Drive upload permission denied.");
                }

                throw new StorageException(StorageFailureKind.Io, $"Google Drive upload chunk failed (HTTP {response.StatusCode}).");
            }
            catch (HttpRequestException) when (attempts < maxAttempts)
            {
                attempts++;
                await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempts)), cancellationToken).ConfigureAwait(false);
                currentOffset = await QueryUploadOffsetAsync(sessionUri, totalLength, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunkBytes);
            }
        }

        // Final query if upload loop ended without returning result
        var finalResult = await QueryUploadCompletionAsync(sessionUri, totalLength, cancellationToken).ConfigureAwait(false);
        if (finalResult is not null) return finalResult;

        throw new StorageException(StorageFailureKind.Io, "Google Drive resumable upload finished without completion confirmation.");
    }

    private async Task<string> InitiateResumableUploadSessionAsync(
        string folderId, string fileName, string storageKey, long totalLength, string sha256Hex, string? existingFileId, CancellationToken cancellationToken)
    {
        var accessToken = await _tokenManager.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var uploadUrl = existingFileId is null ? $"{UploadBaseUrl}?{UploadQuery}" : $"{UploadBaseUrl}/{Uri.EscapeDataString(existingFileId)}?{UploadQuery}";
        using var request = new HttpRequestMessage(existingFileId is null ? HttpMethod.Post : HttpMethod.Patch, uploadUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
        request.Headers.Add("X-Upload-Content-Length", totalLength.ToString());

        var metadata = new Dictionary<string, object>
        {
            ["name"] = fileName,
            ["appProperties"] = new Dictionary<string, string>
            {
                ["storageKeyHash"] = StorageKeyHash(storageKey),
                ["tenantId"] = _options.TenantId.ToString("N"),
                ["connectionId"] = _options.ConnectionId.ToString("N"),
                ["sha256"] = sha256Hex
            }
        };
        if (existingFileId is null) metadata["parents"] = new[] { folderId };

        request.Content = JsonContent.Create(metadata);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode && response.Headers.Location is { } location)
        {
            return location.ToString();
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new StorageException(StorageFailureKind.AccessDenied, "Permission denied initiating Google Drive upload session.");
        }
        if ((int)response.StatusCode >= 500)
        {
            throw new StorageException(StorageFailureKind.NetworkUnavailable, "Google Drive service temporarily unavailable.");
        }

        throw new StorageException(StorageFailureKind.Io, $"Failed to initiate Google Drive upload session (HTTP {response.StatusCode}).");
    }

    private async Task<long> QueryUploadOffsetAsync(string sessionUri, long totalLength, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, sessionUri);
            request.Content = new ByteArrayContent([]);
            request.Content.Headers.ContentLength = 0;
            request.Content.Headers.Add("Content-Range", $"bytes */{totalLength}");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == (HttpStatusCode)308)
            {
                return ParseRangeHeaderNextOffset(response.Headers, 0);
            }
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                return totalLength;
            }
        }
        catch
        {
            // Query failure falls back to last known offset
        }
        return 0;
    }

    private async Task<DriveFileInfo?> QueryUploadCompletionAsync(string sessionUri, long totalLength, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, sessionUri);
            request.Content = new ByteArrayContent([]);
            request.Content.Headers.ContentLength = 0;
            request.Content.Headers.Add("Content-Range", $"bytes */{totalLength}");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                return await response.Content.ReadFromJsonAsync<DriveFileInfo>(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best effort
        }
        return null;
    }

    private async Task<string> EnsureTargetFolderAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_resolvedFolderId))
        {
            return _resolvedFolderId;
        }

        await _folderLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_resolvedFolderId))
            {
                return _resolvedFolderId;
            }

            // Hierarchy: Root "Backup Platform" -> Tenant "{tenantId:N}" -> Connection "{connectionId:N}"
            var rootFolderId = await EnsureFolderAsync("Backup Platform", parentId: "root", cancellationToken).ConfigureAwait(false);
            var tenantFolderId = await EnsureFolderAsync(_options.TenantId.ToString("N"), rootFolderId, cancellationToken).ConfigureAwait(false);
            var connectionFolderId = await EnsureFolderAsync(_options.ConnectionId.ToString("N"), tenantFolderId, cancellationToken).ConfigureAwait(false);

            _resolvedFolderId = connectionFolderId;
            return connectionFolderId;
        }
        finally
        {
            _folderLock.Release();
        }
    }

    private async Task<string> EnsureFolderAsync(string folderName, string parentId, CancellationToken cancellationToken)
    {
        var accessToken = await _tokenManager.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var query = $"mimeType = 'application/vnd.google-apps.folder' and name = '{EscapeQuery(folderName)}' and '{parentId}' in parents and trashed = false";
        var url = $"{ApiBaseUrl}/files?q={Uri.EscapeDataString(query)}&fields=files(id,name)&pageSize=1";

        using var searchReq = new HttpRequestMessage(HttpMethod.Get, url);
        searchReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var searchResp = await _http.SendAsync(searchReq, cancellationToken).ConfigureAwait(false);
        if (searchResp.IsSuccessStatusCode)
        {
            var list = await searchResp.Content.ReadFromJsonAsync<DriveFileList>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (list?.Files is { Count: > 0 } files)
            {
                return files[0].Id;
            }
        }
        else if (searchResp.StatusCode == HttpStatusCode.Unauthorized || searchResp.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new StorageException(StorageFailureKind.AccessDenied, "Access denied searching Google Drive folder.");
        }

        // Folder not found: create it
        using var createReq = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/files");
        createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        createReq.Content = JsonContent.Create(new
        {
            name = folderName,
            mimeType = "application/vnd.google-apps.folder",
            parents = new[] { parentId }
        });

        using var createResp = await _http.SendAsync(createReq, cancellationToken).ConfigureAwait(false);
        if (createResp.IsSuccessStatusCode)
        {
            var created = await createResp.Content.ReadFromJsonAsync<DriveFileInfo>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (created is not null && !string.IsNullOrWhiteSpace(created.Id))
            {
                return created.Id;
            }
        }

        if (createResp.StatusCode == HttpStatusCode.Unauthorized || createResp.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new StorageException(StorageFailureKind.AccessDenied, "Access denied creating folder in Google Drive.");
        }

        throw new StorageException(StorageFailureKind.Io, $"Failed to create folder in Google Drive (HTTP {createResp.StatusCode}).");
    }

    private void CheckStagingCapacity(long requiredBytes)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_options.StagingDirectory));
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (requiredBytes > drive.AvailableFreeSpace - _options.MinStagingFreeBytes)
                {
                    throw new StorageException(StorageFailureKind.InsufficientSpace, "Insufficient staging disk space for Google Drive upload.");
                }
            }
        }
        catch (StorageException)
        {
            throw;
        }
        catch
        {
            // DriveInfo check might fail in restricted containers; ignore non-fatal environment exceptions
        }
    }

    private static string ExtractFileName(StorageObjectKey key)
    {
        var segments = key.Value.Split('/');
        return segments[^1];
    }

    private static string EscapeQuery(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    private static long ParseRangeHeaderNextOffset(HttpResponseHeaders headers, long fallback)
    {
        if (headers.TryGetValues("Range", out var values))
        {
            var rangeStr = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(rangeStr) && rangeStr.StartsWith("bytes=0-", StringComparison.OrdinalIgnoreCase))
            {
                var endPart = rangeStr["bytes=0-".Length..];
                if (long.TryParse(endPart, out var lastByte))
                {
                    return lastByte + 1;
                }
            }
        }
        return fallback;
    }

    private static TimeSpan GetRetryAfterDelay(HttpResponseHeaders headers, int attempt)
    {
        if (headers.RetryAfter?.Delta is { } delta && delta.TotalSeconds > 0)
        {
            return delta;
        }
        return TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt) + Random.Shared.Next(0, 300));
    }

    private sealed class DriveFileList
    {
        [JsonPropertyName("files")]
        public List<DriveFileInfo>? Files { get; set; }
    }

    private sealed class DriveFileInfo
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("size")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long Size { get; set; }

        [JsonPropertyName("md5Checksum")]
        public string? Md5Checksum { get; set; }

        [JsonPropertyName("trashed")]
        public bool Trashed { get; set; }

        [JsonPropertyName("modifiedTime")]
        public DateTimeOffset ModifiedTime { get; set; }

        [JsonPropertyName("appProperties")]
        public Dictionary<string, string>? AppProperties { get; set; }
    }
}
