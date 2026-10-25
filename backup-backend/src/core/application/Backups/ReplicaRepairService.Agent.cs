using BackupPlatform.Domain.Storage;
using System.Reflection;
using BackupPlatform.Application.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Application.Backups;

/// <summary>The connection a lease-fenced agent call may be given access to: the operation's own target, or the pinned source replica's connection.</summary>
public readonly record struct ReplicaRepairAccessTarget(Guid ConnectionId, string StorageProvider);

/// <summary>
/// Repair of an SFTP or Google Drive target, and of any external target whose source is an agent's local replica, runs on an agent. The backend keeps every decision (admission, quota reservation, source pin, orphan ledger,
/// the replica itself); the agent only moves bytes, and each of its calls is fenced by the operation's lease token and the lease owner recorded at claim.
/// </summary>
public sealed partial class ReplicaRepairService
{
    private const int MaxFailureReasonLength = 512;

    private static readonly HashSet<string> KnownFailureCodes = typeof(ReplicaRepairFailureCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (string)field.GetRawConstantValue()!)
        .ToHashSet(StringComparer.Ordinal);

    public async Task<ReplicaRepairTaskSnapshot?> ClaimForAgentAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var claimed = await store.ClaimAgentRepairAsync(identity, now, DefaultLeaseDuration, cancellationToken);
        if (claimed is null) return null;
        var leaseToken = claimed.LeaseToken!.Value;

        try
        {
            if (claimed.StorageConnectionId == Guid.Empty)
            {
                await FailOrRetryAsync(claimed, leaseToken, ReplicaRepairFailureCodes.DestinationNotConfigured,
                    "Destination storage connection is not configured.", CancellationToken.None);
                return null;
            }

            var admission = await store.AdmitRepairAsync(claimed.TenantId, claimed.ArtifactId, claimed.StorageConnectionId, claimed.StorageProvider,
                now, cancellationToken, excludeOperationId: claimed.Id);
            if (!admission.Allowed || admission.Artifact is null || admission.TargetDestination is null)
            {
                await FailOrRetryAsync(claimed, leaseToken, admission.DenialCode ?? ReplicaRepairFailureCodes.InternalError,
                    admission.DenialReason ?? "Admission re-evaluation failed.", CancellationToken.None);
                return null;
            }

            // The agent reads a source straight from its provider or from its own local store: a central replica, or another agent's local replica, has no such path,
            // and nothing is relayed or copied through the backend.
            var candidates = admission.CandidateSources ?? [];
            var sources = candidates
                .Where(c => IsExternalSource(c) || IsLocalSourceOf(c, identity.AgentId.Value))
                .Select(c => IsExternalSource(c)
                    ? new ReplicaRepairSourceContract { ReplicaId = c.Id, StorageProvider = c.StorageProvider, StorageConnectionId = c.StorageConnectionId, StorageKey = c.Key }
                    : new ReplicaRepairSourceContract { ReplicaId = c.Id, StorageProvider = c.StorageProvider, StorageTarget = c.StorageTarget, StorageKey = c.Key })
                .ToList();
            if (sources.Count == 0)
            {
                await FailOrRetryAsync(claimed, leaseToken,
                    candidates.Count > 0 ? ReplicaRepairFailureCodes.UnsupportedSource : ReplicaRepairFailureCodes.NoVerifiedSourceAvailable,
                    "No verified source replica is readable directly by an agent.", CancellationToken.None);
                return null;
            }

            return new ReplicaRepairTaskSnapshot
            {
                OperationId = claimed.Id,
                TenantId = claimed.TenantId.Value,
                AgentId = identity.AgentId.Value,
                ArtifactId = claimed.ArtifactId,
                LeaseToken = leaseToken,
                LeaseExpiresAt = claimed.LeaseExpiresAt!.Value,
                SizeBytes = admission.Artifact.SizeBytes,
                Checksum = admission.Artifact.ArtifactChecksum,
                Target = new ReplicaRepairTargetContract
                {
                    StorageProvider = admission.TargetDestination.StorageProvider,
                    StorageConnectionId = admission.TargetDestination.StorageConnectionId ?? claimed.StorageConnectionId
                },
                Sources = sources
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Agent repair claim {OperationId} failed unexpectedly.", claimed.Id);
            await FailOrRetryAsync(claimed, leaseToken, ReplicaRepairFailureCodes.InternalError, "Repair claim failed unexpectedly.", CancellationToken.None);
            return null;
        }
    }

    public async Task<ReplicaRepairLeaseResponse?> RenewForAgentAsync(AgentIdentity identity, Guid operationId, Guid leaseToken, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (await LiveOperationAsync(identity, operationId, leaseToken, now, cancellationToken) is null) return null;
        var renewal = await store.RenewLeaseAsync(identity.TenantId, operationId, leaseToken, now, DefaultLeaseDuration, cancellationToken);
        return renewal.Success
            ? new ReplicaRepairLeaseResponse { LeaseExpiresAt = now + DefaultLeaseDuration, CancellationRequested = renewal.CancellationRequested }
            : null;
    }

    /// <summary>The agent proposes a source and target key; the key is accepted only if it is the lease-specific repair name next to the source object.</summary>
    public async Task<BackupMutationResult> PinForAgentAsync(AgentIdentity identity, Guid operationId, ReplicaRepairPinRequest request, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var operation = await LiveOperationAsync(identity, operationId, request.LeaseToken, now, cancellationToken);
        if (operation is null) return BackupMutationResult.Conflict;
        var source = (await store.FindArtifactAsync(operation.TenantId, operation.ArtifactId, cancellationToken))?.Replicas
            .FirstOrDefault(replica => replica.Id == request.SourceReplicaId);
        if (source is null || !IsExternalSource(source) && !IsLocalSourceOf(source, identity.AgentId.Value))
            return BackupMutationResult.Conflict;

        var expectedKey = AgentTargetKey(source.Key, operationId, request.LeaseToken);
        if (!string.Equals(request.TargetKey, expectedKey, StringComparison.Ordinal))
            throw new ArgumentException("The target key is not this repair's lease-specific key.", nameof(request));

        return await store.PinSourceAsync(identity.TenantId, operationId, request.LeaseToken, source.Id, expectedKey, now, cancellationToken)
            ? BackupMutationResult.Accepted
            : BackupMutationResult.Conflict;
    }

    public async Task<BackupMutationResult> ProgressForAgentAsync(AgentIdentity identity, Guid operationId, ReplicaRepairProgressRequest request, CancellationToken cancellationToken)
    {
        if (request.TransferredBytes < 0) throw new ArgumentException("Transferred bytes must not be negative.", nameof(request));
        var now = time.GetUtcNow();
        var operation = await LiveOperationAsync(identity, operationId, request.LeaseToken, now, cancellationToken);
        if (operation is null) return BackupMutationResult.Conflict;
        var transferred = operation.TotalBytes > 0 ? Math.Min(request.TransferredBytes, operation.TotalBytes) : request.TransferredBytes;
        return await store.UpdateProgressAsync(identity.TenantId, operationId, request.LeaseToken, transferred, operation.TotalBytes, now, cancellationToken)
            ? BackupMutationResult.Accepted
            : BackupMutationResult.Conflict;
    }

    public async Task<bool> CanCleanupForAgentAsync(AgentIdentity identity, Guid operationId, ReplicaRepairCleanupRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.TargetKey)) throw new ArgumentException("A target key is required.", nameof(request));
        var now = time.GetUtcNow();
        return await LiveOperationAsync(identity, operationId, request.LeaseToken, now, cancellationToken) is not null
            && await store.CanCleanupTargetAsync(identity.TenantId, operationId, request.LeaseToken, request.TargetKey, now, cancellationToken);
    }

    /// <summary>
    /// Applies the agent's report with the semantics of the backend's own execution. A Drive target's object record is saved through <paramref name="saveDriveRecord"/>
    /// (connection, key, record) before the replica is published, so a replica never exists without the record that can find its file.
    /// </summary>
    public async Task<BackupMutationResult> CompleteForAgentAsync(
        AgentIdentity identity,
        Guid operationId,
        ReplicaRepairCompletionRequest request,
        Func<Guid, string, TaskStorageCatalogRecord, CancellationToken, Task> saveDriveRecord,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var leaseToken = request.LeaseToken;
        var operation = await LiveOperationAsync(identity, operationId, leaseToken, now, cancellationToken);
        if (operation is null) return BackupMutationResult.Conflict;

        switch (request.Outcome)
        {
            case ReplicaRepairOutcomes.Succeeded:
                return await SucceedForAgentAsync(operation, request, saveDriveRecord, now, cancellationToken);

            case ReplicaRepairOutcomes.Failed:
                var code = request.FailureCode is { } reported && KnownFailureCodes.Contains(reported) ? reported : ReplicaRepairFailureCodes.InternalError;
                if (!await RecordAgentOrphanAsync(operation, request, now, cancellationToken)) return BackupMutationResult.Conflict;
                return await FailOrRetryAsync(operation, leaseToken, code, Truncate(request.Reason), cancellationToken)
                    ? BackupMutationResult.Accepted
                    : BackupMutationResult.Conflict;

            case ReplicaRepairOutcomes.Cancelled:
                if (!await RecordAgentOrphanAsync(operation, request, now, cancellationToken)) return BackupMutationResult.Conflict;
                return await store.CancelOperationAsync(operation.TenantId, operationId, leaseToken, time.GetUtcNow(), cancellationToken)
                    ? BackupMutationResult.Accepted
                    : BackupMutationResult.Conflict;

            case ReplicaRepairOutcomes.TargetConflict:
                return await store.FailTargetConflictAsync(operation.TenantId, operationId, leaseToken, now, cancellationToken)
                    ? BackupMutationResult.Accepted
                    : BackupMutationResult.Conflict;

            default:
                throw new ArgumentException("Unknown repair outcome.", nameof(request));
        }
    }

    /// <summary>Which connection an agent may be handed credentials for; null unless the caller holds this operation's live lease.</summary>
    public async Task<ReplicaRepairAccessTarget?> ResolveAccessAsync(AgentIdentity identity, Guid operationId, Guid leaseToken, bool source, CancellationToken cancellationToken)
    {
        var operation = await LiveOperationAsync(identity, operationId, leaseToken, time.GetUtcNow(), cancellationToken);
        if (operation is null) return null;
        if (!source)
            return StorageProviderTypes.External(operation.StorageProvider) && operation.StorageConnectionId != Guid.Empty
                ? new ReplicaRepairAccessTarget(operation.StorageConnectionId, operation.StorageProvider)
                : null;

        // The source is only reachable once the repair pinned it: the agent cannot ask for a replica it was never given.
        if (operation.TargetKey is null || operation.SourceReplicaId is not { } sourceId) return null;
        var replica = (await store.FindArtifactAsync(operation.TenantId, operation.ArtifactId, cancellationToken))?.Replicas.FirstOrDefault(r => r.Id == sourceId);
        return replica is { StorageConnectionId: var connectionId } && connectionId != Guid.Empty
            && replica.ExecutionLocation == BackupStorageExecutionLocation.Backend && StorageProviderTypes.External(replica.StorageProvider)
            && operation.TargetKey == AgentTargetKey(replica.Key, operation.Id, leaseToken)
                ? new ReplicaRepairAccessTarget(connectionId, replica.StorageProvider)
                : null;
    }

    private async Task<BackupMutationResult> SucceedForAgentAsync(
        ReplicaRepairOperation operation,
        ReplicaRepairCompletionRequest request,
        Func<Guid, string, TaskStorageCatalogRecord, CancellationToken, Task> saveDriveRecord,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<StorageVerificationMethod>(request.VerificationMethod, out var method) || !Enum.IsDefined(method))
            throw new ArgumentException("A verification method is required.", nameof(request));
        var isDrive = operation.StorageProvider == StorageProviderTypes.GoogleDrive;
        if (isDrive && string.IsNullOrWhiteSpace(request.DriveRecord?.FileId))
            throw new ArgumentException("A Google Drive target requires its object record.", nameof(request));
        if (string.IsNullOrEmpty(operation.TargetKey) || operation.StorageConnectionId == Guid.Empty) return BackupMutationResult.Conflict;
        var artifact = await store.FindArtifactAsync(operation.TenantId, operation.ArtifactId, cancellationToken);
        if (artifact is null) return BackupMutationResult.Conflict;
        var source = artifact.Replicas.FirstOrDefault(replica => replica.Id == operation.SourceReplicaId);
        if (source is null || operation.TargetKey != AgentTargetKey(source.Key, operation.Id, request.LeaseToken))
            return BackupMutationResult.Conflict;

        if (isDrive) await saveDriveRecord(operation.StorageConnectionId, operation.TargetKey, request.DriveRecord!, cancellationToken);

        var newReplica = new BackupReplica(
            Guid.NewGuid(),
            operation.TenantId,
            artifact.Id,
            operation.StorageProvider,
            operation.StorageConnectionId.ToString("D"),
            operation.TargetKey,
            artifact.SizeBytes,
            artifact.ArtifactChecksum,
            artifact.ChecksumAlgorithm,
            "Verified",
            method.ToString(),
            now,
            now,
            BackupStorageExecutionLocation.Backend,
            owningAgentId: null,
            storageConnectionId: operation.StorageConnectionId);
        try
        {
            await store.CompleteOperationAsync(operation.TenantId, operation.Id, operation.LeaseToken!.Value, newReplica, time.GetUtcNow(), cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return BackupMutationResult.Conflict;
        }
        logger.LogInformation("Agent repair operation {OperationId} succeeded. Created replica {ReplicaId} on {Provider}:{Target}.",
            operation.Id, newReplica.Id, newReplica.StorageProvider, newReplica.StorageTarget);
        return BackupMutationResult.Accepted;
    }

    /// <summary>Records the target object the agent could not prove removed. Only the operation's own target (or a partial sibling of it) is accepted: an orphan is later physically deleted.</summary>
    private async Task<bool> RecordAgentOrphanAsync(ReplicaRepairOperation operation, ReplicaRepairCompletionRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (request.OrphanKey is not { } orphanKey) return true;
        var orphanName = orphanKey[(orphanKey.LastIndexOf('/') + 1)..];
        if (operation.TargetKey is null || (orphanKey != operation.TargetKey
            && (!ManagedBackupLayout.IsPartialSibling(operation.TargetKey, orphanKey) || !ManagedBackupLayout.IsManagedPartialName(orphanName))))
            throw new ArgumentException("The orphan key is not this repair's target.", nameof(request));
        var artifact = await store.FindArtifactAsync(operation.TenantId, operation.ArtifactId, cancellationToken);
        if (artifact is null) return false;
        await store.RecordRepairOrphanAsync(operation.TenantId, artifact.Id, operation.StorageConnectionId, operation.StorageProvider,
            operation.StorageConnectionId.ToString("D"), orphanKey, artifact.SizeBytes, artifact.ArtifactChecksum, now, cancellationToken,
            quarantine: request.OrphanQuarantine);
        return true;
    }

    /// <summary>The operation when it is in progress under this lease, held by this very agent; one read serves every fenced call.</summary>
    private async Task<ReplicaRepairOperation?> LiveOperationAsync(AgentIdentity identity, Guid operationId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var operation = await store.FindOperationAsync(identity.TenantId, operationId, cancellationToken);
        return operation is { Status: ReplicaRepairStatus.InProgress } && operation.TenantId == identity.TenantId
            && operation.LeaseOwner == ReplicaRepairOperation.AgentLeaseOwner(identity.AgentId.Value) && operation.HasLease(leaseToken, now)
                ? operation
                : null;
    }

    private static bool IsExternalSource(BackupReplica replica) =>
        replica.ExecutionLocation == BackupStorageExecutionLocation.Backend && replica.StorageConnectionId != Guid.Empty && StorageProviderTypes.External(replica.StorageProvider);

    /// <summary>A replica on the local store of this very agent: only it can read the bytes.</summary>
    private static bool IsLocalSourceOf(BackupReplica replica, Guid agentId) =>
        replica.ExecutionLocation == BackupStorageExecutionLocation.Agent && replica.OwningAgentId == agentId;

    private static string AgentTargetKey(string sourceKey, Guid operationId, Guid leaseToken)
    {
        var slash = sourceKey.LastIndexOf('/');
        var fileName = ManagedBackupLayout.RepairFileName(operationId, leaseToken);
        return slash >= 0 ? $"{sourceKey[..slash]}/{fileName}" : fileName;
    }

    private static string? Truncate(string? reason) =>
        reason is { Length: > MaxFailureReasonLength } ? reason[..MaxFailureReasonLength] : reason;
}
