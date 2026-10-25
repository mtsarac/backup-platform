using BackupPlatform.Domain.Storage;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using BackupPlatform.Contracts.Backups;
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

public sealed partial class ReplicaRepairService(
    IReplicaRepairStore store,
    IBackupStorageResolver storageResolver,
    TimeProvider time,
    ILogger<ReplicaRepairService> logger,
    IReplicaMissingReporter? missing = null)
{
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeLocalCts = new();

    public async Task<ReplicaRepairStartResult> StartRepairAsync(
        TenantId tenantId,
        Guid artifactId,
        StartReplicaRepairRequest request,
        Guid? requestedByUserId,
        CancellationToken cancellationToken,
        bool executeInBackground = false)
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

        var created = await store.CreateOperationAsync(operation, cancellationToken);
        if (!created.Allowed)
        {
            logger.LogWarning("Replica repair denied for tenant {TenantId}, artifact {ArtifactId}: {Code} - {Reason}",
                tenantId, artifactId, created.DenialCode, created.DenialReason);
            return new(false, null, created.DenialCode, created.DenialReason);
        }

        logger.LogInformation("Replica repair operation created: Id={OperationId}, Tenant={TenantId}, Artifact={ArtifactId}, Destination={Provider}:{ConnectionId}",
            operation.Id, tenantId, artifactId, targetDestination.StorageProvider, targetDestination.StorageConnectionId);

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
        var leaseLost = false;
        var heartbeatFailed = false;
        var userCancellationRequested = false;

        try
        {
            _activeLocalCts[operationId] = cts;
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
                return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
            }

            var artifact = admission.Artifact;
            var targetDestination = admission.TargetDestination;
            var candidateSources = admission.CandidateSources ?? [];

            var destinationTarget = StorageProviderTypes.External(targetDestination.StorageProvider) && targetDestination.StorageConnectionId is { } connId && connId != Guid.Empty
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

            string? targetKey = null;
            foreach (var candidate in candidateSources)
            {
                if (cts.Token.IsCancellationRequested) break;

                if (string.Equals(candidate.StorageProvider, "local", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("AgentLocal source {ReplicaId} cannot be read via backend relay without agent transport.", candidate.Id);
                    continue;
                }
                // Central-involving repairs still run here; never fall back to relaying an external-to-external copy when the central source cannot be opened.
                if (StorageProviderTypes.AgentExecuted(targetDestination.StorageProvider)
                    && claimed.LeaseOwner?.StartsWith("agent:", StringComparison.Ordinal) != true
                    && StorageProviderTypes.External(candidate.StorageProvider))
                    continue;

                var sourceTarget = StorageProviderTypes.External(candidate.StorageProvider) && candidate.StorageConnectionId != Guid.Empty
                    ? candidate.StorageConnectionId.ToString("D")
                    : candidate.StorageTarget;

                var resolved = storageResolver.Resolve(tenantId, candidate.StorageProvider, sourceTarget);
                if (resolved is null || !resolved.Capabilities.Supports(StorageCapabilityIds.Read))
                {
                    logger.LogWarning("Candidate source {ReplicaId} ({Provider}) cannot be resolved or does not support reading.",
                        candidate.Id, candidate.StorageProvider);
                    continue;
                }

                var slash = candidate.Key.LastIndexOf('/');
                var fileName = ManagedBackupLayout.RepairFileName(operationId, leaseToken);
                var targetScope = slash >= 0 ? candidate.Key[..slash] : "";
                var candidateTargetKey = string.IsNullOrEmpty(targetScope) ? fileName : $"{targetScope}/{fileName}";

                try
                {
                    if (!await store.PinSourceAsync(tenantId, operationId, leaseToken, candidate.Id, candidateTargetKey, time.GetUtcNow(), cts.Token)) continue;
                    sourceStream = await resolved.OpenReadAsync(new StorageObjectKey(candidate.Key), cts.Token);
                    sourceProvider = resolved;
                    sourceReplica = candidate;
                    targetKey = candidateTargetKey;
                    break;
                }
                catch (StorageException ex)
                {
                    logger.LogWarning(ex, "Failed to open candidate source {ReplicaId} ({Provider}): {Message}",
                        candidate.Id, candidate.StorageProvider, ex.Message);
                    if (ex.Kind == StorageFailureKind.NotFound && missing is not null) await missing.ReportMissingAsync(tenantId, candidate.Id, CancellationToken.None);
                }
            }

            if (sourceStream is null || sourceReplica is null || sourceProvider is null || targetKey is null)
            {
                var failureCode = candidateSources.Any(c => string.Equals(c.StorageProvider, "local", StringComparison.OrdinalIgnoreCase)) && candidateSources.Count == 1
                    ? ReplicaRepairFailureCodes.UnsupportedSource
                    : ReplicaRepairFailureCodes.SourceInaccessible;

                await FailOrRetryAsync(claimed, leaseToken, failureCode,
                    "Unable to open readable stream from any verified source replica.", CancellationToken.None);
                return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
            }

            claimed.SetTargetIntent(sourceReplica.Id, targetKey);
            await using (sourceStream)
            {
                await using var hashingStream = new Sha256VerifyingStream(sourceStream);

                var progressUpdating = 0;
                long lastReportedBytes = 0;
                Task progressTask = Task.CompletedTask;
                var progress = new Progress<long>(bytes =>
                {
                    if (bytes - Volatile.Read(ref lastReportedBytes) >= 10 * 1024 * 1024)
                    {
                        if (Interlocked.CompareExchange(ref progressUpdating, 1, 0) == 0)
                        {
                            Volatile.Write(ref lastReportedBytes, bytes);
                            progressTask = store.UpdateProgressAsync(tenantId, operationId, leaseToken, bytes, artifact.SizeBytes, time.GetUtcNow(), CancellationToken.None)
                                .ContinueWith(_ => Interlocked.Exchange(ref progressUpdating, 0), TaskScheduler.Default);
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
                            ReplicaRepairLeaseRenewal renewal;
                            try
                            {
                                renewal = await store.RenewLeaseAsync(
                                    tenantId, operationId, leaseToken, time.GetUtcNow(), DefaultLeaseDuration, heartbeatCts.Token);
                            }
                            catch (OperationCanceledException) when (heartbeatCts.IsCancellationRequested)
                            {
                                break;
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "Error in repair lease heartbeat for {OperationId}. Stopping operation to preserve lease safety.", operationId);
                                heartbeatFailed = true;
                                cts.Cancel();
                                break;
                            }

                            if (!renewal.Success)
                            {
                                leaseLost = true;
                                logger.LogWarning("Repair operation {OperationId} lease renewal returned false; lease lost. Fencing operation.", operationId);
                                cts.Cancel();
                                break;
                            }

                            if (renewal.CancellationRequested)
                            {
                                userCancellationRequested = true;
                                logger.LogInformation("Repair operation {OperationId} cancellation observed in database lease heartbeat. Stopping transfer.", operationId);
                                cts.Cancel();
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                });

                var putCompleted = false;

                try
                {
                    await destinationProvider.PutAsync(new StorageObjectKey(targetKey), hashingStream, putOptions, progress, cts.Token);
                    putCompleted = true;

                    // If provider read the source stream, verify its SHA-256 hash.
                    // If provider did not read the stream (Position == 0), the destination object was already present
                    // with matching metadata (idempotent PUT); readback verification will validate and adopt it.
                    if (hashingStream.Position > 0)
                    {
                        var actualSourceChecksum = hashingStream.GetHashHex();
                        if (hashingStream.Position != artifact.SizeBytes || !string.Equals(actualSourceChecksum, artifact.ArtifactChecksum, StringComparison.OrdinalIgnoreCase))
                        {
                            logger.LogError("Source replica {SourceReplicaId} checksum mismatch: computed {Actual}, expected {Expected}",
                                sourceReplica.Id, actualSourceChecksum, artifact.ArtifactChecksum);
                            await TryCleanupOrRecordOrphanAsync(destinationProvider, tenantId, operationId, leaseToken, artifact, targetDestination, destinationTarget, targetKey);
                            await FailOrRetryAsync(claimed, leaseToken, ReplicaRepairFailureCodes.SourceChecksumMismatch,
                                "Source replica data failed SHA-256 integrity verification.", CancellationToken.None);
                            return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
                        }
                    }
                    else
                    {
                        logger.LogInformation("Destination object already present for repair {OperationId}. Validating existing object via readback.", operationId);
                    }

                    // Destination readback verification (for both freshly uploaded and existing adopted objects)
                    var expectation = new StorageVerificationExpectation(artifact.SizeBytes, new ContentChecksum(ChecksumAlgorithm.Sha256, artifact.ArtifactChecksum));
                    var verifyResult = await destinationProvider.VerifyAsync(new StorageObjectKey(targetKey), expectation, cts.Token);
                    if (verifyResult.Status != StorageVerificationStatus.Verified)
                    {
                        logger.LogError("Destination readback verification failed for repair {OperationId}: {Status}", operationId, verifyResult.Status);
                        await TryCleanupOrRecordOrphanAsync(destinationProvider, tenantId, operationId, leaseToken, artifact, targetDestination, destinationTarget, targetKey);
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
                catch (OperationCanceledException)
                {
                    if (leaseLost)
                    {
                        logger.LogWarning("Repair operation {OperationId} was cancelled due to lost lease. Skipping cleanup and terminal cancellation.", operationId);
                        return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
                    }

                    if (heartbeatFailed)
                    {
                        logger.LogWarning("Repair operation {OperationId} was cancelled due to heartbeat failure. Skipping cleanup and terminal cancellation.", operationId);
                        return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
                    }

                    if (externalToken.IsCancellationRequested)
                    {
                        logger.LogInformation("Repair operation {OperationId} stopped due to host shutdown.", operationId);
                        return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
                    }
                    if (!cts.Token.IsCancellationRequested && !userCancellationRequested)
                    {
                        logger.LogWarning("Repair operation {OperationId} transfer timed out internally. Scheduling retry.", operationId);
                        await TryCleanupOrRecordOrphanAsync(destinationProvider, tenantId, operationId, leaseToken, artifact, targetDestination, destinationTarget, targetKey);
                        await FailOrRetryAsync(claimed, leaseToken, ReplicaRepairFailureCodes.OperationTimedOut,
                            "Transfer deadline exceeded.", CancellationToken.None);
                        return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
                    }

                    logger.LogInformation("Repair operation {OperationId} was cancelled by user request (observedInHeartbeat={InHeartbeat}).", operationId, userCancellationRequested);
                    await TryCleanupOrRecordOrphanAsync(destinationProvider, tenantId, operationId, leaseToken, artifact, targetDestination, destinationTarget, targetKey);
                    await store.CancelOperationAsync(tenantId, operationId, leaseToken, time.GetUtcNow(), CancellationToken.None);
                    return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
                }
                catch (StorageException sex)
                {
                    logger.LogError(sex, "Destination storage put/verify failed for repair {OperationId}: {Message}", operationId, sex.Message);
                    if (sex.Kind == StorageFailureKind.AlreadyExists)
                    {
                        await store.FailTargetConflictAsync(tenantId, operationId, leaseToken, time.GetUtcNow(), CancellationToken.None);
                        return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
                    }
                    var cleanupKey = sex.RemainingObjectKey?.Value ?? targetKey;
                    if (!putCompleted && !sex.ObjectMayRemain)
                    {
                        await store.RecordRepairOrphanAsync(
                            tenantId,
                            artifact.Id,
                            targetDestination.StorageConnectionId ?? Guid.Empty,
                            targetDestination.StorageProvider,
                            destinationTarget,
                            cleanupKey,
                            artifact.SizeBytes,
                            artifact.ArtifactChecksum,
                            time.GetUtcNow(),
                            CancellationToken.None,
                            quarantine: true);
                    }
                    else
                    {
                        await TryCleanupOrRecordOrphanAsync(destinationProvider, tenantId, operationId, leaseToken, artifact, targetDestination, destinationTarget, cleanupKey);
                    }
                    var code = sex.Kind switch
                    {
                        StorageFailureKind.InsufficientSpace => ReplicaRepairFailureCodes.QuotaExceeded,
                        StorageFailureKind.Timeout => ReplicaRepairFailureCodes.OperationTimedOut,
                        StorageFailureKind.NotFound or StorageFailureKind.AccessDenied or StorageFailureKind.Io => ReplicaRepairFailureCodes.StorageWriteFailed,
                        _ => ReplicaRepairFailureCodes.StorageUnavailable
                    };
                    await FailOrRetryAsync(claimed, leaseToken, code, sex.Message, CancellationToken.None);
                    return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unexpected error in repair execution {OperationId}: {Message}", operationId, ex.Message);
                    await TryCleanupOrRecordOrphanAsync(destinationProvider, tenantId, operationId, leaseToken, artifact, targetDestination, destinationTarget, targetKey, recordOrphanIfCleanupFenced: putCompleted);
                    await FailOrRetryAsync(claimed, leaseToken, ReplicaRepairFailureCodes.InternalError,
                        $"Internal error: {ex.GetType().Name}: {ex.Message}", CancellationToken.None);
                    return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
                }
                finally
                {
                    heartbeatCts.Cancel();
                    try { await heartbeatTask; } catch { /* ignore */ }
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (leaseLost || heartbeatFailed || externalToken.IsCancellationRequested)
            {
                logger.LogInformation("Repair operation {OperationId} stopped without user cancellation (leaseLost={LeaseLost}, heartbeatFailed={HeartbeatFailed}, hostShutdown={HostShutdown}).",
                    operationId, leaseLost, heartbeatFailed, externalToken.IsCancellationRequested);
                return await store.GetOperationAsync(tenantId, artifactId, operationId, CancellationToken.None);
            }

            logger.LogInformation("Repair operation {OperationId} cancelled (observedInHeartbeat={InHeartbeat}).", operationId, userCancellationRequested);
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
            _activeLocalCts.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(operationId, cts));
        }
    }

    public async Task<bool> CancelRepairAsync(
        TenantId tenantId,
        Guid artifactId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (!await store.RequestCancellationAsync(tenantId, artifactId, operationId, time.GetUtcNow(), cancellationToken))
            return false;

        if (_activeLocalCts.TryGetValue(operationId, out var cts))
        {
            try { cts.Cancel(); } catch { /* ignore */ }
        }

        return true;
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

    private async Task<bool> FailOrRetryAsync(
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
            return await store.ScheduleRetryAsync(claimed.TenantId, claimed.Id, leaseToken, failureCode, reason, nextRetryAt, now, cancellationToken);
        }
        else
        {
            logger.LogWarning("Repair operation {OperationId} failed permanently or exhausted retries: {Code} - {Reason}.",
                claimed.Id, failureCode, reason);
            return await store.FailOperationAsync(claimed.TenantId, claimed.Id, leaseToken, failureCode, reason, now, cancellationToken);
        }
    }

    private async Task TryCleanupOrRecordOrphanAsync(
        IStorageProvider destinationProvider,
        TenantId tenantId,
        Guid operationId,
        Guid leaseToken,
        BackupArtifact artifact,
        BackupDestination targetDestination,
        string destinationTarget,
        string targetKey,
        bool recordOrphanIfCleanupFenced = false)
    {
        var canCleanup = await store.CanCleanupTargetAsync(
            tenantId, operationId, leaseToken, targetKey, time.GetUtcNow(), CancellationToken.None);
        if (!canCleanup)
        {
            logger.LogWarning("Fenced cleanup skipped for operation {OperationId}, key {Key}. Lease not held or verified replica exists.",
                operationId, targetKey);
            if (recordOrphanIfCleanupFenced)
            {
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
            return;
        }

        var deleted = false;
        try
        {
            deleted = await destinationProvider.DeleteAsync(new StorageObjectKey(targetKey), CancellationToken.None);
        }
        catch (StorageException ex) when (ex.Kind == StorageFailureKind.NotFound)
        {
            deleted = true;
        }
        catch (Exception delEx)
        {
            logger.LogWarning(delEx, "Failed to delete target key {Key} during cleanup. Recording durable orphan replica.", targetKey);
        }

        if (!deleted)
        {
            logger.LogWarning("Target key {Key} deletion unconfirmed after repair failure. Recording durable orphan replica.", targetKey);
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
}
