using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
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
        StorageCapabilityIds.Read, StorageCapabilityIds.Delete,
        StorageCapabilityIds.AtomicPublish, StorageCapabilityIds.VerifyAbsence, StorageCapabilityIds.Resume]);
    private const string ApiBase = "https://www.googleapis.com/drive/v3/files";
    private const string UploadBase = "https://www.googleapis.com/upload/drive/v3/files";
    private const string Fields = "id,name,size,md5Checksum,modifiedTime,trashed,appProperties,parents";
    private static readonly Meter Meter = new("BackupPlatform.GoogleDrive");
    private static readonly Counter<long> Findings = Meter.CreateCounter<long>("google_drive_reconciliation_findings");
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>("google_drive_retries");
    private readonly HttpClient _http;
    private readonly GoogleDriveStorageOptions _options;
    private readonly Func<CancellationToken, ValueTask<string>> _accessTokenSource;
    private readonly IGoogleDriveFileCatalog _catalog;
    private readonly IGoogleDriveStagingStore? _stagingStore;
    private readonly Action<GoogleDriveReconciliationFinding>? _observer;
    private readonly SemaphoreSlim _folderLock = new(1, 1);
    private string? _resolvedFolderId;

    public GoogleDriveStorageProvider(HttpClient http, GoogleDriveStorageOptions options,
        Func<CancellationToken, ValueTask<GoogleDriveCredential>> credentials,
        IGoogleDriveFileCatalog? catalog = null,
        Func<string, CancellationToken, Task>? onTokenRotated = null,
        Func<Func<CancellationToken, ValueTask<string>>, CancellationToken, ValueTask<string>>? refreshCoordinator = null,
        Func<CancellationToken, Task>? onReauthorizationRequired = null,
        Action<GoogleDriveReconciliationFinding>? reconciliationObserver = null,
        IGoogleDriveStagingStore? stagingStore = null)
        : this(http, options,
            new GoogleDriveTokenManager(http, options, credentials ?? throw new ArgumentNullException(nameof(credentials)), onTokenRotated, refreshCoordinator, onReauthorizationRequired).GetAccessTokenAsync,
            catalog, reconciliationObserver, stagingStore)
    {
    }

    public static GoogleDriveStorageProvider CreateDirect(
        HttpClient http,
        GoogleDriveStorageOptions options,
        Func<CancellationToken, ValueTask<string>> accessTokenSource,
        IGoogleDriveFileCatalog? catalog = null,
        Action<GoogleDriveReconciliationFinding>? reconciliationObserver = null,
        IGoogleDriveStagingStore? stagingStore = null) =>
        new(http, options, accessTokenSource, catalog, reconciliationObserver, stagingStore);

    private GoogleDriveStorageProvider(HttpClient http, GoogleDriveStorageOptions options,
        Func<CancellationToken, ValueTask<string>> accessTokenSource,
        IGoogleDriveFileCatalog? catalog,
        Action<GoogleDriveReconciliationFinding>? reconciliationObserver,
        IGoogleDriveStagingStore? stagingStore)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _accessTokenSource = accessTokenSource ?? throw new ArgumentNullException(nameof(accessTokenSource));
        _catalog = catalog ?? new InMemoryGoogleDriveFileCatalog();
        _stagingStore = stagingStore;
        _observer = reconciliationObserver;
        _resolvedFolderId = options.FolderId;
    }

    public StorageProviderId Id => ProviderId;
    public StorageTopology Topology => StorageTopology.BackendRelayed;
    public StorageCapabilities Capabilities => GoogleDriveCapabilities;

    public async Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content,
        StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        if (!content.CanRead) throw new ArgumentException("The content stream must be readable.", nameof(content));
        if (options.ExpectedLength is null or <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Google Drive requires an explicit positive length.");
        if (options.ExpectedChecksum is null || options.ExpectedChecksum.Algorithm != ChecksumAlgorithm.Sha256)
            throw new ArgumentException("Google Drive requires an authoritative SHA-256 checksum.", nameof(options));
        if (_stagingStore is null)
            throw new StorageException(StorageFailureKind.InvalidConfiguration, "Google Drive staging is not configured.");
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.UploadTimeout + options.TransferAllowance);
        try
        {
            return await PutCoreAsync(key, content, options, progress, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            var retained = exception.Data[nameof(StorageException)] as StorageException;
            throw new StorageException(StorageFailureKind.Timeout, "Google Drive upload exceeded its time limit.", exception,
                retained?.ObjectMayRemain == true, retained?.RemainingObjectKey);
        }
    }

    private async Task<StorageObjectMetadata> PutCoreAsync(StorageObjectKey key, Stream content,
        StoragePutOptions options, IProgress<long>? progress, CancellationToken token)
    {
        var expected = new StorageVerificationExpectation(options.ExpectedLength!.Value, options.ExpectedChecksum!);
        var cached = await _catalog.FindAsync(_options.TenantId, _options.ConnectionId, key.Value, token).ConfigureAwait(false);
        var published = cached is null ? null : await GetOwnedFileAsync(cached.FileId, key.Value, token).ConfigureAwait(false);
        if (published is not null && await MatchesAsync(published, expected, token).ConfigureAwait(false))
        {
            if (!string.Equals(cached!.Checksum, expected.Checksum.Hex, StringComparison.OrdinalIgnoreCase))
                await PublishAsync(key, published, expected.Checksum, token).ConfigureAwait(false);
            return Metadata(key, published, expected.Checksum);
        }
        if (published is not null && options.Overwrite == StorageOverwritePolicy.Fail)
            throw new StorageException(StorageFailureKind.AlreadyExists, "Target object already exists in Google Drive.");

        // Unreferenced candidates survive catalog failures and process restarts.
        var candidates = await FindCandidatesAsync(key.Value, token).ConfigureAwait(false);
        foreach (var candidate in candidates.OrderBy(f => f.Id, StringComparer.Ordinal))
        {
            if (await MatchesAsync(candidate, expected, token).ConfigureAwait(false))
            {
                await PublishAsync(key, candidate, expected.Checksum, token).ConfigureAwait(false);
                return Metadata(key, candidate, expected.Checksum);
            }
        }
        if (candidates.Count > 0 && options.Overwrite == StorageOverwritePolicy.Fail)
            throw new StorageException(StorageFailureKind.AlreadyExists, "Conflicting target objects exist in Google Drive.");

        await using var staged = await _stagingStore!.StageAsync(content, expected.SizeBytes, expected.Checksum, token).ConfigureAwait(false);
        token = staged.CancellationToken;
        token.ThrowIfCancellationRequested();
        var folderId = await EnsureTargetFolderAsync(token).ConfigureAwait(false);
        var resumed = options.Checkpoint?.ProviderState is { } state ? JsonSerializer.Deserialize<DriveTransferState>(state) : null;
        var retainUploaded = published is null;
        var fileId = resumed?.FileId ?? (cached is { Checksum: null } && published is null ? cached.FileId
            : await GenerateIdAsync(token).ConfigureAwait(false));
        if (retainUploaded)
        {
            // Persist the locator before Drive can create bytes; retain an old replacement until readback succeeds.
            await _catalog.SaveAsync(_options.TenantId, _options.ConnectionId, key.Value,
                new GoogleDriveFileRecord(fileId, folderId, key.Value.Split('/')[^1], expected.SizeBytes, null, null),
                token).ConfigureAwait(false);
        }
        try
        {
            DriveFileInfo uploaded;
            try
            {
                uploaded = await UploadAsync(staged.Path, folderId, fileId, key, expected, options, progress, token).ConfigureAwait(false);
            }
            catch (StorageException exception) when (exception.Kind == StorageFailureKind.AlreadyExists)
            {
                var recovered = await GetOwnedFileAsync(fileId, key.Value, token).ConfigureAwait(false);
                if (recovered is null || !await MatchesAsync(recovered, expected, token).ConfigureAwait(false)) throw;
                await PublishAsync(key, recovered, expected.Checksum, token).ConfigureAwait(false);
                return Metadata(key, recovered, expected.Checksum);
            }
            var owned = await GetOwnedFileAsync(uploaded.Id, key.Value, token).ConfigureAwait(false);
            if (owned is null || !await MatchesAsync(owned, expected, token).ConfigureAwait(false))
            {
                Observe(new("integrity_mismatch", uploaded.Id, StorageKeyHash(key.Value)));
                throw new StorageException(StorageFailureKind.IntegrityMismatch, "Google Drive readback did not match the uploaded backup.");
            }
            await PublishAsync(key, owned, expected.Checksum, token).ConfigureAwait(false);
            return Metadata(key, owned, expected.Checksum);
        }
        catch (Exception exception) when (retainUploaded)
        {
            var kind = exception is StorageException storage ? storage.Kind
                : exception is OperationCanceledException ? StorageFailureKind.Timeout : StorageFailureKind.Io;
            var failure = new StorageException(kind, "Google Drive upload failed after reserving its object identity.", exception, true, key);
            if (exception is OperationCanceledException)
            {
                exception.Data[nameof(StorageException)] = failure;
                throw;
            }
            throw failure;
        }
    }

    private async Task PublishAsync(StorageObjectKey key, DriveFileInfo file, ContentChecksum checksum, CancellationToken token)
    {
        var parent = file.Parents?.FirstOrDefault() ?? await EnsureTargetFolderAsync(token).ConfigureAwait(false);
        await _catalog.SaveAsync(_options.TenantId, _options.ConnectionId, key.Value,
            new GoogleDriveFileRecord(file.Id, parent, file.Name, file.Size, checksum.Hex, file.Md5Checksum), token).ConfigureAwait(false);
    }

    public async Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken) =>
        await GetMetadataAsync(key, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var cached = await _catalog.FindAsync(_options.TenantId, _options.ConnectionId, key.Value, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            var file = await GetOwnedFileAsync(cached.FileId, key.Value, cancellationToken).ConfigureAwait(false);
            if (file is null)
            {
                await ReportAsync(new("missing", cached.FileId, StorageKeyHash(key.Value)), cancellationToken).ConfigureAwait(false);
                // Keep the catalog evidence; reconciliation is read-only and an upload retry can repair it.
                return null;
            }
            return Metadata(key, file, Checksum(cached.Checksum));
        }
        var candidates = await FindCandidatesAsync(key.Value, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0) return null;
        var first = candidates.OrderBy(f => f.Id, StringComparer.Ordinal).First();
        var checksum = Checksum(first.AppProperties?.GetValueOrDefault("sha256"));
        if (checksum is null || candidates.Any(f => f.Size != first.Size
            || !string.Equals(f.AppProperties?.GetValueOrDefault("sha256"), checksum.Hex, StringComparison.OrdinalIgnoreCase)))
            throw new StorageException(StorageFailureKind.AlreadyExists, "Ambiguous Google Drive objects require reconciliation.");
        if (!await MatchesAsync(first, new(first.Size, checksum), cancellationToken).ConfigureAwait(false))
            throw new StorageException(StorageFailureKind.IntegrityMismatch, "Google Drive recovered object failed verification.");
        await PublishAsync(key, first, checksum, cancellationToken).ConfigureAwait(false);
        return Metadata(key, first, checksum);
    }

    public async Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key,
        StorageVerificationExpectation expected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var fileId = await ResolveFileIdAsync(key, cancellationToken).ConfigureAwait(false);
        if (fileId is null)
            return new(new StorageLocation(Id, key), StorageVerificationStatus.Missing, StorageVerificationMethod.ReadBack, null, null);
        var actual = await HashFileAsync(fileId, expected.SizeBytes, cancellationToken).ConfigureAwait(false);
        var status = actual.Length != expected.SizeBytes ? StorageVerificationStatus.SizeMismatch
            : actual.Checksum.Equals(expected.Checksum) ? StorageVerificationStatus.Verified : StorageVerificationStatus.ChecksumMismatch;
        return new(new StorageLocation(Id, key), status, StorageVerificationMethod.ReadBack, actual.Length, actual.Checksum);
    }

    public async Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        var fileId = await ResolveFileIdAsync(key, cancellationToken).ConfigureAwait(false);
        if (fileId is null) throw new StorageException(StorageFailureKind.NotFound, "The requested Google Drive object is absent.");
        return await OpenFileAsync(fileId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var cached = await _catalog.FindAsync(_options.TenantId, _options.ConnectionId, key.Value, cancellationToken).ConfigureAwait(false);
        var fileId = cached?.FileId;
        if (fileId is null)
        {
            var candidates = await FindCandidatesAsync(key.Value, cancellationToken).ConfigureAwait(false);
            if (candidates.Count > 1)
                throw new StorageException(StorageFailureKind.AlreadyExists, "Ambiguous Google Drive objects require reconciliation.");
            fileId = candidates.SingleOrDefault()?.Id;
        }
        if (fileId is null) return false;
        // Ownership is checked again immediately before the destructive operation.
        if (await GetOwnedFileAsync(fileId, key.Value, cancellationToken).ConfigureAwait(false) is null) return false;
        using var response = await SendAsync(() => new(HttpMethod.Delete, FileUrl(fileId)), true, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound) await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return response.StatusCode != HttpStatusCode.NotFound;
    }

    public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) =>
        throw new StorageException(StorageFailureKind.NotSupported, "Google Drive enumeration is not supported.");

    /// <summary>Read-only detection. Unreferenced objects may belong to active uploads; this operation never repairs or deletes.</summary>
    public async Task<GoogleDriveReconciliationReport> ReconcileAsync(CancellationToken cancellationToken)
    {
        var findings = new List<GoogleDriveReconciliationFinding>();
        var records = await _catalog.ListAsync(_options.TenantId, _options.ConnectionId, cancellationToken).ConfigureAwait(false);
        var files = await ListFilesAsync(ConnectionQuery(), cancellationToken).ConfigureAwait(false);
        files = files.Where(IsConnectionOwned).ToList();
        var referenced = records.Select(r => r.Record.FileId).ToHashSet(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var file = await GetFileAsync(record.Record.FileId, cancellationToken).ConfigureAwait(false);
            var kind = file is null || file.Trashed ? "missing" : !IsOwned(file, record.StorageKey) ? "ownership_conflict" : null;
            if (kind is not null) findings.Add(new(kind, record.Record.FileId, StorageKeyHash(record.StorageKey)));
        }
        foreach (var group in files.GroupBy(f => f.AppProperties?.GetValueOrDefault("storageKeyHash")))
        {
            if (group.Count() > 1)
                foreach (var file in group) findings.Add(new("duplicate", file.Id, group.Key));
            foreach (var file in group.Where(f => !referenced.Contains(f.Id)))
                findings.Add(new("unreferenced_candidate", file.Id, group.Key));
        }
        foreach (var finding in findings) await ReportAsync(finding, cancellationToken).ConfigureAwait(false);
        return new(true, records.Count, files.Count, findings);
    }

    private async Task ReportAsync(GoogleDriveReconciliationFinding finding, CancellationToken token)
    {
        Observe(finding);
        await _catalog.ReportFindingAsync(_options.TenantId, _options.ConnectionId, finding, token).ConfigureAwait(false);
    }

    private void Observe(GoogleDriveReconciliationFinding finding)
    {
        Findings.Add(1, new KeyValuePair<string, object?>("kind", finding.Kind));
        _observer?.Invoke(finding);
    }

    private async Task<string?> ResolveFileIdAsync(StorageObjectKey key, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (await GetMetadataAsync(key, token).ConfigureAwait(false) is null) return null;
        var record = await _catalog.FindAsync(_options.TenantId, _options.ConnectionId, key.Value, token).ConfigureAwait(false);
        return record?.FileId;
    }

    private async Task<DriveFileInfo?> GetOwnedFileAsync(string id, string key, CancellationToken token)
    {
        var file = await GetFileAsync(id, token).ConfigureAwait(false);
        if (file is null || file.Trashed) return null;
        if (!IsOwned(file, key))
        {
            await ReportAsync(new("ownership_conflict", id, StorageKeyHash(key)), token).ConfigureAwait(false);
            throw new StorageException(StorageFailureKind.AccessDenied, "Google Drive object ownership does not match this connection.");
        }
        return file;
    }

    private bool IsConnectionOwned(DriveFileInfo file) => file.AppProperties is { } p
        && p.GetValueOrDefault("tenantId") == _options.TenantId.ToString("N")
        && p.GetValueOrDefault("connectionId") == _options.ConnectionId.ToString("N");
    private bool IsOwned(DriveFileInfo file, string key) => IsConnectionOwned(file)
        && file.AppProperties!.GetValueOrDefault("storageKeyHash") == StorageKeyHash(key);
    private string ConnectionQuery() => "trashed = false"
        + $" and appProperties has {{ key='tenantId' and value='{_options.TenantId:N}' }}"
        + $" and appProperties has {{ key='connectionId' and value='{_options.ConnectionId:N}' }}"
        + " and mimeType != 'application/vnd.google-apps.folder'";

    private async Task<List<DriveFileInfo>> FindCandidatesAsync(string key, CancellationToken token)
    {
        var files = await ListFilesAsync(ConnectionQuery()
            + $" and appProperties has {{ key='storageKeyHash' and value='{StorageKeyHash(key)}' }}", token).ConfigureAwait(false);
        files = files.Where(f => !f.Trashed && IsOwned(f, key)).ToList();
        if (files.Count > 1) Observe(new("duplicate", null, StorageKeyHash(key)));
        return files;
    }

    private async Task<List<DriveFileInfo>> ListFilesAsync(string query, CancellationToken token)
    {
        var files = new List<DriveFileInfo>();
        var seenPages = new HashSet<string>(StringComparer.Ordinal);
        string? page = null;
        do
        {
            var url = $"{ApiBase}?q={Uri.EscapeDataString(query)}&fields=nextPageToken,incompleteSearch,files({Fields})&pageSize=1000";
            if (page is not null) url += "&pageToken=" + Uri.EscapeDataString(page);
            using var response = await SendAsync(() => new(HttpMethod.Get, url), true, token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, token).ConfigureAwait(false);
            var list = await ReadJsonAsync<DriveFileList>(response, token).ConfigureAwait(false)
                ?? throw new StorageException(StorageFailureKind.Io, "Google Drive returned invalid search metadata.");
            if (list.IncompleteSearch) throw new StorageException(StorageFailureKind.NetworkUnavailable, "Google Drive search is incomplete.");
            if (list.Files is not null) files.AddRange(list.Files);
            page = list.NextPageToken;
            if (page is not null && !seenPages.Add(page))
                throw new StorageException(StorageFailureKind.Io, "Google Drive returned a repeated search page.");
        } while (!string.IsNullOrEmpty(page));
        return files;
    }

    private async Task<DriveFileInfo?> GetFileAsync(string id, CancellationToken token)
    {
        using var response = await SendAsync(() => new(HttpMethod.Get, FileUrl(id) + "?fields=" + Fields), true, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, token).ConfigureAwait(false);
        return await ReadJsonAsync<DriveFileInfo>(response, token).ConfigureAwait(false)
            ?? throw new StorageException(StorageFailureKind.Io, "Google Drive returned invalid object metadata.");
    }

    private async Task<bool> MatchesAsync(DriveFileInfo file, StorageVerificationExpectation expected, CancellationToken token)
    {
        if (file.Size != expected.SizeBytes) return false;
        if (!string.Equals(file.AppProperties?.GetValueOrDefault("sha256"), expected.Checksum.Hex, StringComparison.OrdinalIgnoreCase)) return false;
        var actual = await HashFileAsync(file.Id, expected.SizeBytes, token).ConfigureAwait(false);
        return actual.Length == expected.SizeBytes && actual.Checksum.Equals(expected.Checksum);
    }

    private async Task<(long Length, ContentChecksum Checksum)> HashFileAsync(string id, long expectedLength, CancellationToken token)
    {
        await using var stream = await OpenFileAsync(id, token).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long length = 0;
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, count);
                length = checked(length + count);
                if (length > expectedLength) break;
            }
            return (length, ContentChecksum.Sha256(hash.GetHashAndReset()));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task<Stream> OpenFileAsync(string id, CancellationToken token)
    {
        var response = await SendAsync(() => new(HttpMethod.Get, FileUrl(id) + "?alt=media"), true, token).ConfigureAwait(false);
        try
        {
            await EnsureSuccessAsync(response, token).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            return new ResponseStream(stream, response, _options.RequestTimeout);
        }
        catch { response.Dispose(); throw; }
    }

    private async Task<DriveFileInfo> UploadAsync(string path, string folder, string fileId, StorageObjectKey key,
        StorageVerificationExpectation expected, StoragePutOptions options, IProgress<long>? progress, CancellationToken token)
    {
        var retained = options.Checkpoint?.ProviderState is { } state ? JsonSerializer.Deserialize<DriveTransferState>(state) : null;
        if (retained is not null && (!Uri.TryCreate(retained.Session, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Host == "googleapis.com" || uri.Host.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase))))
            throw new StorageException(StorageFailureKind.InvalidConfiguration, "Invalid retained Drive upload session.");
        var session = retained?.Session ?? await InitiateAsync(folder, fileId, key, expected, token).ConfigureAwait(false);
        if (options.RecordCheckpointAsync is { } initial)
            await initial(new(options.Checkpoint?.VerifiedByteOffset ?? 0, JsonSerializer.Serialize(new DriveTransferState(session, fileId))), token).ConfigureAwait(false);
        var failures = 0;
        var restarts = 0;
        Stopwatch? retryClock = null;
        long offset = 0;
        var query = retained is not null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, true);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = query ? 0 : (int)Math.Min(_options.ChunkSizeBytes, expected.SizeBytes - offset);
            var bytes = ArrayPool<byte>.Shared.Rent(Math.Max(count, 1));
            HttpResponseMessage? response = null;
            try
            {
                stream.Position = offset;
                if (count > 0) await stream.ReadExactlyAsync(bytes.AsMemory(0, count), token).ConfigureAwait(false);
                var querying = query;
                response = await SendAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Put, session);
                    request.Content = new ByteArrayContent(bytes, 0, count);
                    request.Content.Headers.ContentType = new("application/octet-stream");
                    request.Content.Headers.ContentLength = count;
                    request.Content.Headers.TryAddWithoutValidation("Content-Range", querying || count == 0
                        ? $"bytes */{expected.SizeBytes}" : $"bytes {offset}-{offset + count - 1}/{expected.SizeBytes}");
                    return request;
                }, false, token, authorize: false).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                {
                    var file = await ReadJsonAsync<DriveFileInfo>(response, token).ConfigureAwait(false);
                    if (file is null || string.IsNullOrWhiteSpace(file.Id))
                        throw new StorageException(StorageFailureKind.Io, "Google Drive upload returned invalid metadata.");
                    if (!string.Equals(file.Id, fileId, StringComparison.Ordinal))
                        throw new StorageException(StorageFailureKind.IntegrityMismatch, "Google Drive upload returned an unexpected object identity.");
                    progress?.Report(expected.SizeBytes);
                    if (options.RecordCheckpointAsync is { } complete)
                        await complete(new(expected.SizeBytes, JsonSerializer.Serialize(new DriveTransferState(session, fileId))), token).ConfigureAwait(false);
                    return file;
                }
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    var completed = await GetOwnedFileAsync(fileId, key.Value, token).ConfigureAwait(false);
                    if (completed is not null) return completed;
                    if (++restarts > _options.MaxSessionRestarts)
                        throw new StorageException(StorageFailureKind.NetworkUnavailable, "Google Drive upload session restart limit exceeded.");
                    retryClock ??= Stopwatch.StartNew();
                    await DelayRetryAsync(response, ++failures, retryClock, token).ConfigureAwait(false);
                    session = await InitiateAsync(folder, fileId, key, expected, token).ConfigureAwait(false);
                    if (options.RecordCheckpointAsync is { } restarted)
                        await restarted(new(0, JsonSerializer.Serialize(new DriveTransferState(session, fileId))), token).ConfigureAwait(false);
                    offset = 0;
                    query = false;
                    continue;
                }
                if ((int)response.StatusCode == 308)
                {
                    var next = ParseOffset(response.Headers, expected.SizeBytes);
                    if (next > offset + count && !querying)
                        throw new StorageException(StorageFailureKind.Io, "Google Drive returned an invalid resumable offset.");
                    if ((next <= offset && !querying) || next == expected.SizeBytes)
                    {
                        if (options.RecordCheckpointAsync is not null) throw Failure(StorageFailureKind.NetworkUnavailable);
                        retryClock ??= Stopwatch.StartNew();
                        await DelayRetryAsync(response, ++failures, retryClock, token).ConfigureAwait(false);
                    }
                    offset = next;
                    if (options.RecordCheckpointAsync is { } checkpoint)
                        await checkpoint(new(offset, JsonSerializer.Serialize(new DriveTransferState(session, fileId))), token).ConfigureAwait(false);
                    query = next == expected.SizeBytes;
                    progress?.Report(offset);
                    continue;
                }
                var kind = await ClassifyAsync(response, token).ConfigureAwait(false);
                if (!IsTransient(kind)) throw Failure(kind);
                if (options.RecordCheckpointAsync is not null) throw Failure(kind);
                retryClock ??= Stopwatch.StartNew();
                await DelayRetryAsync(response, ++failures, retryClock, token).ConfigureAwait(false);
                query = true;
            }
            catch (StorageException ex) when (IsTransient(ex.Kind) && response is null && options.RecordCheckpointAsync is null)
            {
                retryClock ??= Stopwatch.StartNew();
                await DelayRetryAsync(null, ++failures, retryClock, token).ConfigureAwait(false);
                query = true;
            }
            finally
            {
                response?.Dispose();
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }
    }

    private sealed record DriveTransferState(string Session, string FileId);

    private async Task<string> GenerateIdAsync(CancellationToken token)
    {
        using var response = await SendAsync(() => new(HttpMethod.Get,
            $"{ApiBase}/generateIds?count=1&space=drive&type=files"), true, token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, token).ConfigureAwait(false);
        var generated = await ReadJsonAsync<DriveFileIds>(response, token).ConfigureAwait(false);
        if (generated?.Ids is not [var id] || string.IsNullOrWhiteSpace(id))
            throw new StorageException(StorageFailureKind.Io, "Google Drive returned an invalid object identity.");
        return id;
    }

    private async Task<string> InitiateAsync(string folder, string fileId, StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken token)
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{UploadBase}?uploadType=resumable&fields={Fields}");
            request.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
            request.Headers.Add("X-Upload-Content-Length", expected.SizeBytes.ToString(CultureInfo.InvariantCulture));
            request.Content = JsonContent.Create(new
            {
                id = fileId, name = key.Value.Split('/')[^1], parents = new[] { folder },
                appProperties = new Dictionary<string, string>
                {
                    ["storageKeyHash"] = StorageKeyHash(key.Value), ["tenantId"] = _options.TenantId.ToString("N"),
                    ["connectionId"] = _options.ConnectionId.ToString("N"), ["sha256"] = expected.Checksum.Hex
                }
            });
            return request;
        }, true, token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, token).ConfigureAwait(false);
        var location = response.Headers.Location;
        if (location is null || !location.IsAbsoluteUri || location.Scheme != "https"
            || location.Port != 443 || location.UserInfo.Length != 0 || location.Fragment.Length != 0
            || !(location.Host == "googleapis.com" || location.Host.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase)))
            throw new StorageException(StorageFailureKind.Io, "Google Drive returned an invalid upload session.");
        return location.AbsoluteUri;
    }

    private async Task<string> EnsureTargetFolderAsync(CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(_resolvedFolderId)) return _resolvedFolderId;
        await _folderLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_resolvedFolderId)) return _resolvedFolderId;
            var root = await EnsureFolderAsync("Backup Platform", "root", "root", token).ConfigureAwait(false);
            var tenant = await EnsureFolderAsync(_options.TenantId.ToString("N"), root, "tenant:" + _options.TenantId.ToString("N"), token).ConfigureAwait(false);
            _resolvedFolderId = await EnsureFolderAsync(_options.ConnectionId.ToString("N"), tenant, "connection:" + _options.ConnectionId.ToString("N"), token).ConfigureAwait(false);
            return _resolvedFolderId;
        }
        finally { _folderLock.Release(); }
    }

    private async Task<string> EnsureFolderAsync(string name, string parent, string identity, CancellationToken token)
    {
        // Global property identity converges even when another node created a duplicate ancestor concurrently.
        var query = "mimeType = 'application/vnd.google-apps.folder' and trashed = false"
            + $" and appProperties has {{ key='backupFolder' and value='{EscapeQuery(identity)}' }}";
        var matches = await ListFilesAsync(query, token).ConfigureAwait(false);
        matches = matches.Where(f => f.AppProperties?.GetValueOrDefault("backupFolder") == identity).ToList();
        if (matches.Count == 0)
        {
            using var response = await SendAsync(() => new(HttpMethod.Post, ApiBase)
            {
                Content = JsonContent.Create(new { name, mimeType = "application/vnd.google-apps.folder", parents = new[] { parent },
                    appProperties = new Dictionary<string, string> { ["backupFolder"] = identity } })
            }, true, token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, token).ConfigureAwait(false);
            var created = await ReadJsonAsync<DriveFileInfo>(response, token).ConfigureAwait(false);
            if (created is null || string.IsNullOrWhiteSpace(created.Id))
                throw new StorageException(StorageFailureKind.Io, "Google Drive returned invalid folder metadata.");
            matches = await ListFilesAsync(query, token).ConfigureAwait(false);
            matches = matches.Where(f => f.AppProperties?.GetValueOrDefault("backupFolder") == identity).ToList();
            if (matches.All(f => f.Id != created.Id)) matches.Add(created);
        }
        if (matches.Count > 1) Observe(new("duplicate_folder", null, null));
        return matches.OrderBy(f => f.Id, StringComparer.Ordinal).First().Id;
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, bool retry, CancellationToken token, bool authorize = true)
    {
        var clock = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            using var request = create();
            if (authorize) request.Headers.Authorization = new("Bearer", await _accessTokenSource(token).ConfigureAwait(false));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            var remaining = _options.MaxRetryElapsed - clock.Elapsed;
            if (retry && remaining <= TimeSpan.Zero) throw Failure(StorageFailureKind.Timeout);
            timeout.CancelAfter(retry && remaining < _options.RequestTimeout ? remaining : _options.RequestTimeout);
            HttpResponseMessage? response = null;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!retry || response.IsSuccessStatusCode || !IsTransient(await ClassifyAsync(response, timeout.Token).ConfigureAwait(false))) return response;
                await DelayRetryAsync(response, attempt, clock, token).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                if (!retry) throw Failure(StorageFailureKind.NetworkUnavailable);
                await DelayRetryAsync(null, attempt, clock, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                if (!retry) throw Failure(StorageFailureKind.Timeout);
                await DelayRetryAsync(null, attempt, clock, token).ConfigureAwait(false);
            }
            catch { response?.Dispose(); throw; }
            response?.Dispose();
        }
    }

    private async Task DelayRetryAsync(HttpResponseMessage? response, int attempt, Stopwatch clock, CancellationToken token)
    {
        if (attempt >= _options.MaxRetryAttempts || clock.Elapsed >= _options.MaxRetryElapsed)
            throw new StorageException(StorageFailureKind.NetworkUnavailable, "Google Drive retry budget exhausted.");
        var delay = TimeSpan.FromMilliseconds(Math.Min(_options.MaxRetryDelay.TotalMilliseconds,
            _options.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 20))) * (0.5 + Random.Shared.NextDouble() * 0.5));
        var after = response?.Headers.RetryAfter;
        var serverDelay = after?.Delta ?? (after?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.Zero);
        if (serverDelay > delay) delay = serverDelay;
        if (delay >= _options.MaxRetryElapsed - clock.Elapsed)
            throw new StorageException(StorageFailureKind.NetworkUnavailable, "Google Drive retry delay exceeds its time budget.");
        Retries.Add(1);
        await Task.Delay(delay, token).ConfigureAwait(false);
    }

    private async Task<StorageFailureKind> ClassifyAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized) return StorageFailureKind.AuthenticationFailed;
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            var body = await ReadContentAsync(t => response.Content.ReadAsStringAsync(t), token).ConfigureAwait(false);
            try
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("errors", out var errors)
                    && errors.ValueKind == JsonValueKind.Array)
                {
                    var reasons = errors.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Object
                        && e.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null).ToList();
                    if (reasons.Contains("storageQuotaExceeded")) return StorageFailureKind.InsufficientSpace;
                    if (reasons.Any(r => r is "rateLimitExceeded" or "userRateLimitExceeded" or "sharingRateLimitExceeded"))
                        return StorageFailureKind.NetworkUnavailable;
                }
            }
            catch (JsonException) { }
            return StorageFailureKind.AccessDenied;
        }
        return response.StatusCode switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.Gone => StorageFailureKind.NotFound,
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => StorageFailureKind.AlreadyExists,
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => StorageFailureKind.Timeout,
            (HttpStatusCode)429 => StorageFailureKind.NetworkUnavailable,
            _ when (int)response.StatusCode >= 500 => StorageFailureKind.NetworkUnavailable,
            _ => StorageFailureKind.Io
        };
    }

    private static bool IsTransient(StorageFailureKind kind) => kind is StorageFailureKind.NetworkUnavailable or StorageFailureKind.Timeout;
    private static StorageException Failure(StorageFailureKind kind) => new(kind, "Google Drive operation failed (" + kind + ").");
    private async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode) throw Failure(await ClassifyAsync(response, token).ConfigureAwait(false));
    }

    private Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken token) =>
        ReadContentAsync(t => response.Content.ReadFromJsonAsync<T>(cancellationToken: t), token);

    private async Task<T> ReadContentAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_options.RequestTimeout);
        try { return await read(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw Failure(StorageFailureKind.Timeout); }
        catch (HttpRequestException) { throw Failure(StorageFailureKind.NetworkUnavailable); }
        catch (JsonException) { throw Failure(StorageFailureKind.Io); }
    }
    private static long ParseOffset(HttpResponseHeaders headers, long length)
    {
        if (!headers.TryGetValues("Range", out var values)) return 0;
        var ranges = values.ToArray();
        if (ranges.Length != 1 || !ranges[0].StartsWith("bytes=0-", StringComparison.OrdinalIgnoreCase)
            || !long.TryParse(ranges[0].AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out var last)
            || last < 0 || last >= length)
            throw new StorageException(StorageFailureKind.Io, "Google Drive returned a malformed resumable range.");
        return last + 1;
    }
    private static string StorageKeyHash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    private static string EscapeQuery(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
    private static string FileUrl(string id) => ApiBase + "/" + Uri.EscapeDataString(id);
    private StorageObjectMetadata Metadata(StorageObjectKey key, DriveFileInfo file, ContentChecksum? checksum) =>
        new(new StorageLocation(Id, key), file.Size, checksum, file.ModifiedTime);
    private static ContentChecksum? Checksum(string? hex) => hex is { Length: 64 } && hex.All(Uri.IsHexDigit)
        ? new ContentChecksum(ChecksumAlgorithm.Sha256, hex) : null;

    private sealed class DriveFileIds
    {
        [JsonPropertyName("ids")] public string[]? Ids { get; set; }
    }

    private sealed class DriveFileList
    {
        [JsonPropertyName("files")] public List<DriveFileInfo>? Files { get; set; }
        [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
        [JsonPropertyName("incompleteSearch")] public bool IncompleteSearch { get; set; }
    }
    private sealed class DriveFileInfo
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("size"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public long Size { get; set; }
        [JsonPropertyName("md5Checksum")] public string? Md5Checksum { get; set; }
        [JsonPropertyName("trashed")] public bool Trashed { get; set; }
        [JsonPropertyName("modifiedTime")] public DateTimeOffset ModifiedTime { get; set; }
        [JsonPropertyName("appProperties")] public Dictionary<string, string>? AppProperties { get; set; }
        [JsonPropertyName("parents")] public List<string>? Parents { get; set; }
    }

    private sealed class ResponseStream(Stream stream, HttpResponseMessage response, TimeSpan timeout) : Stream
    {
        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            try { return await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
            { throw Failure(StorageFailureKind.Timeout); }
            catch (HttpRequestException) { throw Failure(StorageFailureKind.NetworkUnavailable); }
        }
        protected override void Dispose(bool disposing) { if (disposing) { stream.Dispose(); response.Dispose(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await stream.DisposeAsync().ConfigureAwait(false); response.Dispose(); GC.SuppressFinalize(this); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed record GoogleDriveReconciliationFinding(string Kind, string? FileId, string? StorageKeyHash);
public sealed record GoogleDriveReconciliationReport(bool DryRun, int CatalogCount, int DriveCount,
    IReadOnlyList<GoogleDriveReconciliationFinding> Findings);
