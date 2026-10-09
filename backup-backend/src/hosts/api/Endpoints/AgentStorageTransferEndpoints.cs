using System.Text;
using System.Buffers;
using System.Security.Cryptography;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Application.Restores;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BackupPlatform.Api.Endpoints;

public static class AgentStorageTransferEndpoints
{
    public static IEndpointRouteBuilder MapAgentStorageTransferEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var backup = endpoints.MapGroup("/api/agent/tasks/{attemptId:guid}/storage")
            .WithMetadata(new AgentApiEndpoint(), new RequiresAgentProtocol())
            .RequireAuthorization(AuthorizationPolicies.Agent);
        backup.MapPut("/content", UploadAsync).DisableAntiforgery();
        backup.MapGet("/content", ReadBackupAsync);
        backup.MapGet("/metadata", GetBackupMetadataAsync);
        backup.MapPost("/verify", VerifyBackupAsync);
        backup.MapDelete("/content", DeleteBackupAsync);

        var restore = endpoints.MapGroup("/api/agent/restore-tasks/{attemptId:guid}/storage")
            .WithMetadata(new AgentApiEndpoint(), new RequiresAgentProtocol())
            .RequireAuthorization(AuthorizationPolicies.Agent);
        restore.MapGet("/content", ReadRestoreAsync);
        restore.MapGet("/metadata", GetRestoreMetadataAsync);
        restore.MapPost("/verify", VerifyRestoreAsync);
        return endpoints;
    }

    private static async Task<IResult> UploadAsync(Guid attemptId, HttpRequest request, ICurrentAgent agent,
        IBackupRunStore runStore, IBackupStorageResolver resolver, PlatformDbContext db, CancellationToken cancellationToken)
    {
        var fence = await FenceBackupAsync(attemptId, request, agent, runStore, cancellationToken);
        if (fence.Failure is { } failure) return failure;
        if (request.ContentLength is not > 0 || request.ContentType != "application/octet-stream"
            || request.Headers[TaskStorageProtocol.ChecksumHeader].FirstOrDefault() is not { Length: 64 } expectedChecksum)
            return Results.BadRequest(new AgentApiError { Error = "invalid_request", Message = "A validated stream upload is required." });

        var provider = resolver.Resolve(agent.Identity!.TenantId, "sftp", fence.ConnectionId!.Value.ToString("D"));
        if (provider is null) return Results.Conflict(new AgentApiError { Error = "storage_not_configured", Message = "The storage connection is unavailable." });

        return await WithAdvisoryLockAsync(db, fence.ConnectionId.Value, fence.Key!, async () =>
        {
            var verified = await FenceBackupAsync(attemptId, request, agent, runStore, cancellationToken);
            if (verified.Failure is { } failedAgain) return failedAgain;
            try
            {
                var options = new StoragePutOptions(StorageOverwritePolicy.Fail, new ContentChecksum(ChecksumAlgorithm.Sha256, expectedChecksum), request.ContentLength);
                var meta = await provider.PutAsync(new StorageObjectKey(fence.Key!), request.Body, options, null, cancellationToken);
                return Results.Ok(new TaskStorageResponse
                {
                    Exists = true, SizeBytes = meta.SizeBytes, Checksum = meta.Checksum?.Hex, LastModifiedUtc = meta.LastModifiedUtc
                });
            }
            catch (StorageException ex) { return StorageError(ex); }
        }, cancellationToken);
    }

    private static async Task<IResult> ReadBackupAsync(Guid attemptId, HttpRequest request, ICurrentAgent agent,
        IBackupRunStore runStore, IBackupStorageResolver resolver, CancellationToken cancellationToken)
    {
        var fence = await FenceBackupAsync(attemptId, request, agent, runStore, cancellationToken);
        if (fence.Failure is { } failure) return failure;
        var provider = resolver.Resolve(agent.Identity!.TenantId, "sftp", fence.ConnectionId!.Value.ToString("D"));
        if (provider is null) return Results.Conflict(new AgentApiError { Error = "storage_not_configured", Message = "Storage not configured." });
        try
        {
            var stream = await provider.OpenReadAsync(new StorageObjectKey(fence.Key!), cancellationToken);
            return Results.Stream(stream, "application/octet-stream");
        }
        catch (StorageException ex) { return StorageError(ex); }
    }

    private static async Task<IResult> GetBackupMetadataAsync(Guid attemptId, HttpRequest request, ICurrentAgent agent,
        IBackupRunStore runStore, IBackupStorageResolver resolver, CancellationToken cancellationToken)
    {
        var fence = await FenceBackupAsync(attemptId, request, agent, runStore, cancellationToken);
        if (fence.Failure is { } failure) return failure;
        var provider = resolver.Resolve(agent.Identity!.TenantId, "sftp", fence.ConnectionId!.Value.ToString("D"));
        if (provider is null) return Results.Conflict(new AgentApiError { Error = "storage_not_configured", Message = "Storage not configured." });
        try
        {
            var meta = await provider.GetMetadataAsync(new StorageObjectKey(fence.Key!), cancellationToken);
            return Results.Ok(new TaskStorageResponse
            {
                Exists = meta is not null, SizeBytes = meta?.SizeBytes ?? 0, Checksum = meta?.Checksum?.Hex,
                LastModifiedUtc = meta?.LastModifiedUtc ?? default
            });
        }
        catch (StorageException ex) { return StorageError(ex); }
    }

    private static async Task<IResult> VerifyBackupAsync(Guid attemptId, TaskStorageVerifyRequest body, HttpRequest request, ICurrentAgent agent,
        IBackupRunStore runStore, IBackupStorageResolver resolver, PlatformDbContext db, CancellationToken cancellationToken)
    {
        var fence = await FenceBackupAsync(attemptId, request, agent, runStore, cancellationToken);
        if (fence.Failure is { } failure) return failure;
        var provider = resolver.Resolve(agent.Identity!.TenantId, "sftp", fence.ConnectionId!.Value.ToString("D"));
        if (provider is null) return Results.Conflict(new AgentApiError { Error = "storage_not_configured", Message = "Storage not configured." });
        return await WithAdvisoryLockAsync(db, fence.ConnectionId.Value, fence.Key!, async () =>
        {
            try
            {
                var expectation = new StorageVerificationExpectation(body.SizeBytes, new ContentChecksum(ChecksumAlgorithm.Sha256, body.Checksum));
                var result = await provider.VerifyAsync(new StorageObjectKey(fence.Key!), expectation, cancellationToken);
                return Results.Ok(new TaskStorageResponse
                {
                    Exists = result.Status != StorageVerificationStatus.Missing,
                    SizeBytes = result.ActualSizeBytes ?? 0,
                    Checksum = result.ActualChecksum?.Hex,
                    VerificationStatus = result.Status.ToString(),
                    VerificationMethod = result.Method.ToString()
                });
            }
            catch (StorageException ex) { return StorageError(ex); }
        }, cancellationToken);
    }

    private static async Task<IResult> DeleteBackupAsync(Guid attemptId, HttpRequest request, ICurrentAgent agent,
        IBackupRunStore runStore, IBackupStorageResolver resolver, PlatformDbContext db, CancellationToken cancellationToken)
    {
        var fence = await FenceBackupAsync(attemptId, request, agent, runStore, cancellationToken);
        if (fence.Failure is { } failure) return failure;
        var provider = resolver.Resolve(agent.Identity!.TenantId, "sftp", fence.ConnectionId!.Value.ToString("D"));
        if (provider is null) return Results.Conflict(new AgentApiError { Error = "storage_not_configured", Message = "Storage not configured." });
        return await WithAdvisoryLockAsync(db, fence.ConnectionId.Value, fence.Key!, async () =>
        {
            try
            {
                var existed = await provider.DeleteAsync(new StorageObjectKey(fence.Key!), cancellationToken);
                return Results.Ok(new TaskStorageResponse { Exists = existed });
            }
            catch (StorageException ex) { return StorageError(ex); }
        }, cancellationToken);
    }

    private static async Task<IResult> ReadRestoreAsync(Guid attemptId, HttpRequest request, ICurrentAgent agent,
        IRestoreStore restoreStore, IDownloadStore downloadStore, IBackupStorageResolver resolver, CancellationToken cancellationToken)
    {
        var fence = await FenceRestoreAsync(attemptId, request, agent, restoreStore, downloadStore, cancellationToken);
        if (fence.Failure is { } failure) return failure;
        var provider = resolver.Resolve(agent.Identity!.TenantId, "sftp", fence.ConnectionId!.Value.ToString("D"));
        if (provider is null) return Results.Conflict(new AgentApiError { Error = "storage_not_configured", Message = "Storage not configured." });
        try
        {
            var stream = await provider.OpenReadAsync(new StorageObjectKey(fence.Key!), cancellationToken);
            return Results.Stream(stream, "application/octet-stream");
        }
        catch (StorageException ex) { return StorageError(ex); }
    }

    private static async Task<IResult> GetRestoreMetadataAsync(Guid attemptId, HttpRequest request, ICurrentAgent agent,
        IRestoreStore restoreStore, IDownloadStore downloadStore, IBackupStorageResolver resolver, CancellationToken cancellationToken)
    {
        var fence = await FenceRestoreAsync(attemptId, request, agent, restoreStore, downloadStore, cancellationToken);
        if (fence.Failure is { } failure) return failure;
        var provider = resolver.Resolve(agent.Identity!.TenantId, "sftp", fence.ConnectionId!.Value.ToString("D"));
        if (provider is null) return Results.Conflict(new AgentApiError { Error = "storage_not_configured", Message = "Storage not configured." });
        try
        {
            var meta = await provider.GetMetadataAsync(new StorageObjectKey(fence.Key!), cancellationToken);
            return Results.Ok(new TaskStorageResponse
            {
                Exists = meta is not null, SizeBytes = meta?.SizeBytes ?? 0, Checksum = meta?.Checksum?.Hex,
                LastModifiedUtc = meta?.LastModifiedUtc ?? default
            });
        }
        catch (StorageException ex) { return StorageError(ex); }
    }

    private static async Task<IResult> VerifyRestoreAsync(Guid attemptId, TaskStorageVerifyRequest body, HttpRequest request, ICurrentAgent agent,
        IRestoreStore restoreStore, IDownloadStore downloadStore, IBackupStorageResolver resolver, PlatformDbContext db, CancellationToken cancellationToken)
    {
        var fence = await FenceRestoreAsync(attemptId, request, agent, restoreStore, downloadStore, cancellationToken);
        if (fence.Failure is { } failure) return failure;
        var provider = resolver.Resolve(agent.Identity!.TenantId, "sftp", fence.ConnectionId!.Value.ToString("D"));
        if (provider is null) return Results.Conflict(new AgentApiError { Error = "storage_not_configured", Message = "Storage not configured." });
        return await WithAdvisoryLockAsync(db, fence.ConnectionId.Value, fence.Key!, async () =>
        {
            try
            {
                var expectation = new StorageVerificationExpectation(body.SizeBytes, new ContentChecksum(ChecksumAlgorithm.Sha256, body.Checksum));
                var result = await provider.VerifyAsync(new StorageObjectKey(fence.Key!), expectation, cancellationToken);
                return Results.Ok(new TaskStorageResponse
                {
                    Exists = result.Status != StorageVerificationStatus.Missing,
                    SizeBytes = result.ActualSizeBytes ?? 0,
                    Checksum = result.ActualChecksum?.Hex,
                    VerificationStatus = result.Status.ToString(),
                    VerificationMethod = result.Method.ToString()
                });
            }
            catch (StorageException ex) { return StorageError(ex); }
        }, cancellationToken);
    }

    private static async Task<(IResult? Failure, Guid? ConnectionId, string? Key)> FenceBackupAsync(
        Guid attemptId, HttpRequest request, ICurrentAgent agent, IBackupRunStore store, CancellationToken cancellationToken)
    {
        if (attemptId == Guid.Empty || agent.Identity is null)
            return (Results.Unauthorized(), null, null);
        if (!Guid.TryParse(request.Headers[TaskStorageProtocol.LeaseHeader], out var lease) || lease == Guid.Empty
            || request.Headers[TaskStorageProtocol.KeyHeader].FirstOrDefault() is not { Length: > 0 } key)
            return (Results.BadRequest(new AgentApiError { Error = "invalid_request", Message = "Missing lease or key headers." }), null, null);

        var claim = await store.GetAttemptAsync(agent.Identity.TenantId, agent.Identity.AgentId, attemptId, cancellationToken);
        if (claim is null || !claim.Attempt.HasLease(lease, DateTimeOffset.UtcNow)
            || claim.Run.Configuration.StorageProvider != "sftp" || claim.Run.Configuration.StorageConnectionId is not { } connectionId)
            return (Results.Conflict(new AgentApiError { Error = "lease_conflict", Message = "Invalid lease or storage target." }), null, null);

        if (!ManagedBackupLayout.TryParseStorageKey(key, out var layout) || layout is null
            || layout.TenantId != agent.Identity.TenantId.Value || layout.AttemptId != attemptId)
            return (Results.Conflict(new AgentApiError { Error = "invalid_key", Message = "The key layout does not match the attempt." }), null, null);

        return (null, connectionId, key);
    }

    private static async Task<(IResult? Failure, Guid? ConnectionId, string? Key)> FenceRestoreAsync(
        Guid attemptId, HttpRequest request, ICurrentAgent agent, IRestoreStore store, IDownloadStore downloadStore, CancellationToken cancellationToken)
    {
        if (attemptId == Guid.Empty || agent.Identity is null)
            return (Results.Unauthorized(), null, null);
        if (!Guid.TryParse(request.Headers[TaskStorageProtocol.LeaseHeader], out var lease) || lease == Guid.Empty
            || request.Headers[TaskStorageProtocol.KeyHeader].FirstOrDefault() is not { Length: > 0 } key
            || !Guid.TryParse(request.Headers[TaskStorageProtocol.RestoreHeader], out var operationId))
            return (Results.BadRequest(new AgentApiError { Error = "invalid_request", Message = "Missing lease, key or operation header." }), null, null);

        var operation = await store.GetAsync(agent.Identity.TenantId, operationId, cancellationToken);
        if (operation is null || operation.CurrentAttempt.Id != attemptId || !operation.CurrentAttempt.HasLease(lease, DateTimeOffset.UtcNow))
            return (Results.Conflict(new AgentApiError { Error = "lease_conflict", Message = "The restore attempt lease is invalid." }), null, null);

        var artifact = await downloadStore.GetArtifactAsync(agent.Identity.TenantId, operation.ArtifactId, cancellationToken);
        var replica = artifact?.Replicas.SingleOrDefault(r => r.Id == operation.ReplicaId);
        if (replica is null || replica.StorageProvider != "sftp" || !Guid.TryParse(replica.StorageTarget, out var connectionId))
            return (Results.Conflict(new AgentApiError { Error = "source_inaccessible", Message = "The replica storage is inaccessible." }), null, null);

        if (replica.Key != key)
            return (Results.Conflict(new AgentApiError { Error = "invalid_key", Message = "The requested key differs from the replica." }), null, null);

        return (null, connectionId, key);
    }

    private static IResult StorageError(StorageException exception) =>
        Results.UnprocessableEntity(new TaskStorageResponse { FailureKind = exception.Kind.ToString() });

    private static async Task<IResult> WithAdvisoryLockAsync(PlatformDbContext db, Guid connectionId, string key, Func<Task<IResult>> action, CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(cancellationToken);
        var lockId = ComputeLockId(connectionId, key);
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(@id);", connection))
        {
            acquire.Parameters.AddWithValue("id", lockId);
            await acquire.ExecuteNonQueryAsync(cancellationToken);
        }
        try
        {
            return await action();
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@id);", connection);
            release.Parameters.AddWithValue("id", lockId);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static long ComputeLockId(Guid connectionId, string key)
    {
        var bytes = Encoding.UTF8.GetBytes($"{connectionId:N}:{key}");
        var hash = SHA256.HashData(bytes);
        return BitConverter.ToInt64(hash, 0);
    }
}
