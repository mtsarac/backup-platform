using System.Collections.Concurrent;
using System.Security.Cryptography;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Application.Backups;

public sealed record ReplicaRepairStartResult(
    bool Success,
    ReplicaRepairOperationView? Operation = null,
    string? Code = null,
    string? Message = null);

public sealed class ReplicaRepairService(
    IReplicaRepairStore store,
    IBackupStorageResolver storageResolver,
    TimeProvider time,
    ILogger<ReplicaRepairService> logger)
{
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runningRepairs = new();

    public async Task<ReplicaRepairStartResult> StartRepairAsync(
        TenantId tenantId,
        Guid artifactId,
        StartReplicaRepairRequest request,
        Guid? requestedByUserId,
        CancellationToken cancellationToken,
        bool executeInBackground = true)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        var now = time.GetUtcNow();

        var admission = await store.AdmitRepairAsync(
            tenantId,
            artifactId,
            request.StorageConnectionId,
            request.StorageProvider,
            now,
            cancellationToken);

        if (!admission.Allowed)
        {
            logger.LogWarning("Replica repair denied for tenant {TenantId}, artifact {ArtifactId}: {Code} - {Reason}",
                tenantId, artifactId, admission.DenialCode, admission.DenialReason);
            return new(false, null, admission.DenialCode, admission.DenialReason);
        }

        var targetDestination = admission.TargetDestination!;
        var selectedSource = admission.SelectedSource!;
        var artifact = admission.Artifact!;

        var operation = new ReplicaRepairOperation(
            Guid.NewGuid(),
            tenantId,
            artifactId,
            targetDestination.StorageConnectionId ?? Guid.Empty,
            targetDestination.StorageProvider,
            selectedSource.Id,
            now,
            requestedByUserId,
            artifact.SizeBytes);

        await store.CreateOperationAsync(operation, cancellationToken);

        logger.LogInformation("Replica repair operation created: Id={OperationId}, Tenant={TenantId}, Artifact={ArtifactId}, Destination={Provider}:{ConnectionId}",
            operation.Id, tenantId, artifactId, targetDestination.StorageProvider, targetDestination.StorageConnectionId);

        if (executeInBackground)
        {
            _ = Task.Run(() => ExecuteRepairAsync(tenantId, artifactId, operation.Id, admission, CancellationToken.None));
        }
        var view = await store.GetOperationAsync(tenantId, artifactId, operation.Id, cancellationToken);
        return new(true, view);
    }

    public async Task<ReplicaRepairOperationView?> ExecuteRepairAsync(
        TenantId tenantId,
        Guid artifactId,
        Guid operationId,
        ReplicaRepairAdmissionResult? admission = null,
        CancellationToken externalToken = default,
        ReplicaRepairOperation? claimedOperation = null)
    {
        var now = time.GetUtcNow();
        var leaseToken = claimedOperation?.LeaseToken ?? Guid.NewGuid();
        var leaseOwner = claimedOperation?.LeaseOwner ?? $"backend-repair-{Environment.ProcessId}";

        var claimed = claimedOperation ?? await store.ClaimOperationAsync(
            tenantId,
            operationId,
            leaseToken,
            leaseOwner,
            now,
            DefaultLeaseDuration,
            externalToken);
        if (claimed is null)
        {
            logger.LogWarning("Failed to claim repair operation {OperationId} for tenant {TenantId}.", operationId, tenantId);
            return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _runningRepairs[operationId] = cts;

        admission ??= await store.AdmitRepairAsync(
            tenantId,
            artifactId,
            claimed.StorageConnectionId == Guid.Empty ? null : claimed.StorageConnectionId,
            claimed.StorageProvider,
            now,
            cts.Token,
            excludeOperationId: operationId);

        if (!admission.Allowed || admission.Artifact is null || admission.TargetDestination is null)
        {
            var code = admission.DenialCode ?? ReplicaRepairFailureCodes.InternalError;
            var reason = admission.DenialReason ?? "Admission re-evaluation failed.";
            await FailOrRetryAsync(claimed, leaseToken, code, reason, CancellationToken.None);
            _runningRepairs.TryRemove(operationId, out _);
            return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
        }

        var artifact = admission.Artifact;
        var targetDestination = admission.TargetDestination;
        var candidateSources = admission.CandidateSources ?? [];

        try
        {
            var destinationTarget = targetDestination.StorageProvider is "sftp" or "google-drive" && targetDestination.StorageConnectionId is { } connId && connId != Guid.Empty
                ? connId.ToString("D")
                : targetDestination.StorageProvider;

            var destinationProvider = storageResolver.Resolve(tenantId, targetDestination.StorageProvider, destinationTarget);
            if (destinationProvider is null)
            {
                await FailOrRetryAsync(claimed, leaseToken, ReplicaRepairFailureCodes.DestinationNotConfigured,
                    "Destination storage provider is not configured or resolved.", CancellationToken.None);
                return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
            }

            IStorageProvider? sourceProvider = null;
            BackupReplica? sourceReplica = null;
            Stream? sourceStream = null;

            foreach (var candidate in candidateSources)
            {
                if (cts.Token.IsCancellationRequested) break;

                if (string.Equals(candidate.StorageProvider, "local", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("AgentLocal source {ReplicaId} cannot be read via backend relay without agent transport.", candidate.Id);
                    continue;
                }

                var sourceTarget = candidate.StorageProvider is "sftp" or "google-drive" && candidate.StorageConnectionId != Guid.Empty
                    ? candidate.StorageConnectionId.ToString("D")
                    : candidate.StorageTarget;

                var resolved = storageResolver.Resolve(tenantId, candidate.StorageProvider, sourceTarget);
                if (resolved is null || !resolved.Capabilities.Supports(StorageCapabilityIds.Read))
                {
                    logger.LogWarning("Candidate source {ReplicaId} ({Provider}) cannot be resolved or does not support reading.",
                        candidate.Id, candidate.StorageProvider);
                    continue;
                }

                try
                {
                    if (!await store.PinSourceAsync(tenantId, operationId, leaseToken, candidate.Id, time.GetUtcNow(), cts.Token)) continue;
                    sourceStream = await resolved.OpenReadAsync(new StorageObjectKey(candidate.Key), cts.Token);
                    sourceProvider = resolved;
                    sourceReplica = candidate;
                    break;
                }
                catch (StorageException ex)
                {
                    logger.LogWarning(ex, "Failed to open candidate source {ReplicaId} ({Provider}): {Message}",
                        candidate.Id, candidate.StorageProvider, ex.Message);
                }
            }

            if (sourceStream is null || sourceReplica is null || sourceProvider is null)
            {
                var failureCode = candidateSources.Any(c => string.Equals(c.StorageProvider, "local", StringComparison.OrdinalIgnoreCase)) && candidateSources.Count == 1
                    ? ReplicaRepairFailureCodes.UnsupportedSource
                    : ReplicaRepairFailureCodes.SourceInaccessible;

                await FailOrRetryAsync(claimed, leaseToken, failureCode,
                    "Unable to open readable stream from any verified source replica.", CancellationToken.None);
                return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
            }

            claimed.SetSourceReplica(sourceReplica.Id);

            var slash = sourceReplica.Key.LastIndexOf('/');
            var fileName = slash >= 0 ? sourceReplica.Key[(slash + 1)..] : sourceReplica.Key;
            var targetScope = slash >= 0 ? sourceReplica.Key[..slash] : "";
            var targetKey = string.IsNullOrEmpty(targetScope) ? fileName : $"{targetScope}/{fileName}";

            await using (sourceStream)
            {
                await using var hashingStream = new Sha256VerifyingStream(sourceStream);

                var progressUpdating = 0;
                long lastReportedBytes = 0;
                var progress = new Progress<long>(bytes =>
                {
                    if (bytes - Volatile.Read(ref lastReportedBytes) >= 10 * 1024 * 1024)
                    {
                        if (Interlocked.CompareExchange(ref progressUpdating, 1, 0) == 0)
                        {
                            Volatile.Write(ref lastReportedBytes, bytes);
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await store.UpdateProgressAsync(tenantId, operationId, leaseToken, bytes, artifact.SizeBytes, time.GetUtcNow(), CancellationToken.None);
                                }
                                finally
                                {
                                    Interlocked.Exchange(ref progressUpdating, 0);
                                }
                            });
                        }
                    }
                });

                var putOptions = new StoragePutOptions(
                    StorageOverwritePolicy.Fail,
                    new ContentChecksum(ChecksumAlgorithm.Sha256, artifact.ArtifactChecksum),
                    artifact.SizeBytes);

                using var heartbeatCts = new CancellationTokenSource();
                var heartbeatTask = Task.Run(async () =>
                {
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20), time);
                    try
                    {
                        while (await timer.WaitForNextTickAsync(heartbeatCts.Token))
                        {
                            var renewed = await store.RenewLeaseAsync(tenantId, operationId, leaseToken, time.GetUtcNow(), DefaultLeaseDuration, heartbeatCts.Token);
                            if (!renewed)
                            {
                                logger.LogWarning("Repair operation {OperationId} lease renewal failed; triggering cooperative cancellation.", operationId);
                                cts.Cancel();
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error in repair lease heartbeat for {OperationId}.", operationId);
                    }
                });

                try
                {
                    await destinationProvider.PutAsync(new StorageObjectKey(targetKey), hashingStream, putOptions, progress, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    logger.LogInformation("Repair operation {OperationId} was cancelled during transfer.", operationId);
                    await TryCleanupOrRecordOrphanAsync(destinationProvider, tenantId, artifact, targetDestination, destinationTarget, targetKey);
                    await store.CancelOperationAsync(tenantId, operationId, leaseToken, time.GetUtcNow(), CancellationToken.None);
                    return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
                }
                catch (StorageException sex)
                {
                    logger.LogError(sex, "Destination storage put failed for repair {OperationId}: {Message}", operationId, sex.Message);
                    if (sex.Kind != StorageFailureKind.AlreadyExists)
                        await TryCleanupOrRecordOrphanAsync(destinationProvider, tenantId, artifact, targetDestination, destinationTarget, targetKey);
                    var code = sex.Kind switch
                    {
                        StorageFailureKind.AlreadyExists => ReplicaRepairFailureCodes.AlreadyVerified,
                        StorageFailureKind.InsufficientSpace => ReplicaRepairFailureCodes.QuotaExceeded,
                        StorageFailureKind.Timeout => ReplicaRepairFailureCodes.OperationTimedOut,
                        StorageFailureKind.NotFound or StorageFailureKind.AccessDenied or StorageFailureKind.Io => ReplicaRepairFailureCodes.StorageWriteFailed,
                        _ => ReplicaRepairFailureCodes.StorageUnavailable
                    };
                    await FailOrRetryAsync(claimed, leaseToken, code, sex.Message, CancellationToken.None);
                    return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
                }
                finally
                {
                    heartbeatCts.Cancel();
                    try { await heartbeatTask; } catch { /* ignore */ }
                }

                // Verify source SHA-256
                var actualSourceChecksum = hashingStream.GetHashHex();
                if (!string.Equals(actualSourceChecksum, artifact.ArtifactChecksum, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogError("Source replica {SourceReplicaId} checksum mismatch: computed {Actual}, expected {Expected}",
                        sourceReplica.Id, actualSourceChecksum, artifact.ArtifactChecksum);
                    await TryCleanupOrRecordOrphanAsync(destinationProvider, tenantId, artifact, targetDestination, destinationTarget, targetKey);
                    await FailOrRetryAsync(claimed, leaseToken, ReplicaRepairFailureCodes.SourceChecksumMismatch,
                        "Source replica data failed SHA-256 integrity verification.", CancellationToken.None);
                    return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
                }

                // Destination readback verification
                var expectation = new StorageVerificationExpectation(artifact.SizeBytes, new ContentChecksum(ChecksumAlgorithm.Sha256, artifact.ArtifactChecksum));
                var verifyResult = await destinationProvider.VerifyAsync(new StorageObjectKey(targetKey), expectation, cts.Token);
                if (verifyResult.Status != StorageVerificationStatus.Verified)
                {
                    logger.LogError("Destination readback verification failed for repair {OperationId}: {Status}", operationId, verifyResult.Status);
                    await TryCleanupOrRecordOrphanAsync(destinationProvider, tenantId, artifact, targetDestination, destinationTarget, targetKey);
                    await FailOrRetryAsync(claimed, leaseToken, ReplicaRepairFailureCodes.DestinationReadbackFailed,
                        "Destination readback verification failed.", CancellationToken.None);
                    return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
                }

                // Complete operation and create verified replica
                var finishTime = time.GetUtcNow();
                var newReplica = new BackupReplica(
                    Guid.NewGuid(),
                    tenantId,
                    artifact.Id,
                    targetDestination.StorageProvider,
                    destinationTarget,
                    targetKey,
                    artifact.SizeBytes,
                    artifact.ArtifactChecksum,
                    artifact.ChecksumAlgorithm,
                    "Verified",
                    verifyResult.Method.ToString(),
                    finishTime,
                    finishTime,
                    BackupStorageExecutionLocation.Backend,
                    owningAgentId: null,
                    storageConnectionId: targetDestination.StorageConnectionId ?? Guid.Empty);
                await store.CompleteOperationAsync(tenantId, operationId, leaseToken, newReplica, finishTime, CancellationToken.None);
                logger.LogInformation("Repair operation {OperationId} succeeded. Created replica {ReplicaId} on {Provider}:{Target}.",
                    operationId, newReplica.Id, newReplica.StorageProvider, newReplica.StorageTarget);
                return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Repair operation {OperationId} cancelled.", operationId);
            await store.CancelOperationAsync(tenantId, operationId, leaseToken, time.GetUtcNow(), CancellationToken.None);
            return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during repair operation {OperationId}: {Message}", operationId, ex.Message);
            await FailOrRetryAsync(claimed, leaseToken, ReplicaRepairFailureCodes.InternalError,
                $"Internal error: {ex.GetType().Name}: {ex.Message}", CancellationToken.None);
            return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
        }
        finally
        {
            _runningRepairs.TryRemove(operationId, out _);
        }
    }

    public async Task<bool> CancelRepairAsync(
        TenantId tenantId,
        Guid artifactId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (_runningRepairs.TryGetValue(operationId, out var cts))
        {
            try { cts.Cancel(); } catch { /* ignore */ }
        }

        return await store.RequestCancellationAsync(tenantId, operationId, time.GetUtcNow(), cancellationToken);
    }

    public Task<ReplicaRepairOperationView?> GetRepairAsync(
        TenantId tenantId,
        Guid artifactId,
        Guid operationId,
        CancellationToken cancellationToken) =>
        store.GetOperationAsync(tenantId, artifactId, operationId, cancellationToken);

    public Task<IReadOnlyList<ReplicaRepairOperationView>> ListRepairsAsync(
        TenantId tenantId,
        Guid artifactId,
        CancellationToken cancellationToken) =>
        store.ListOperationsAsync(tenantId, artifactId, cancellationToken);

    private async Task FailOrRetryAsync(
        ReplicaRepairOperation claimed,
        Guid leaseToken,
        string failureCode,
        string? reason,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (ReplicaRepairRetryPolicy.IsTransient(failureCode) && claimed.AttemptCount < claimed.MaxAttempts)
        {
            var delay = ReplicaRepairRetryPolicy.CalculateDelay(claimed.AttemptCount, Random.Shared.NextDouble());
            var nextRetryAt = now + delay;
            logger.LogInformation("Repair operation {OperationId} failed with transient code {Code}. Scheduling retry {Attempt}/{Max} at {NextRetryAt}.",
                claimed.Id, failureCode, claimed.AttemptCount + 1, claimed.MaxAttempts, nextRetryAt);
            await store.ScheduleRetryAsync(claimed.TenantId, claimed.Id, leaseToken, failureCode, reason, nextRetryAt, now, cancellationToken);
        }
        else
        {
            logger.LogWarning("Repair operation {OperationId} failed permanently or exhausted retries: {Code} - {Reason}.",
                claimed.Id, failureCode, reason);
            await store.FailOperationAsync(claimed.TenantId, claimed.Id, leaseToken, failureCode, reason, now, cancellationToken);
        }
    }

    private async Task TryCleanupOrRecordOrphanAsync(
        IStorageProvider destinationProvider,
        TenantId tenantId,
        BackupArtifact artifact,
        BackupDestination targetDestination,
        string destinationTarget,
        string targetKey)
    {
        try
        {
            await destinationProvider.DeleteAsync(new StorageObjectKey(targetKey), CancellationToken.None);
        }
        catch (Exception delEx)
        {
            logger.LogWarning(delEx, "Failed to delete target key {Key} after repair failure. Recording durable orphan replica.", targetKey);
            await store.RecordRepairOrphanAsync(
                tenantId,
                artifact.Id,
                targetDestination.StorageConnectionId ?? Guid.Empty,
                targetDestination.StorageProvider,
                destinationTarget,
                targetKey,
                artifact.SizeBytes,
                artifact.ArtifactChecksum,
                time.GetUtcNow(),
                CancellationToken.None);
        }
    }

    private sealed class Sha256VerifyingStream : Stream
    {
        private readonly Stream _inner;
        private readonly IncrementalHash _hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _position;

        public Sha256VerifyingStream(Stream inner)
        {
            _inner = inner;
        }

        public string GetHashHex()
        {
            var hash = _hasher.GetHashAndReset();
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            if (read > 0)
            {
                _hasher.AppendData(buffer, offset, read);
                _position += read;
            }
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                _hasher.AppendData(buffer.Span[..read]);
                _position += read;
            }
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
            if (read > 0)
            {
                _hasher.AppendData(buffer, offset, read);
                _position += read;
            }
            return read;
        }

        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hasher.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
