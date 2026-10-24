using System.Buffers;
using System.Text.Json;
using BackupPlatform.Api.Security;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Downloads;
using BackupPlatform.Application.Restores;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Restores;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Storage;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Abstractions.Transfer;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.Api.Backups;
using BackupPlatform.StorageProviders.GoogleDrive;
using BackupPlatform.StorageProviders.Sftp;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Api.Endpoints;

public static class AgentStorageTransferEndpoints
{
    public static IEndpointRouteBuilder MapAgentStorageTransferEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var backup = endpoints.MapGroup("/api/agent/tasks/{attemptId:guid}/storage")
            .WithMetadata(new AgentApiEndpoint(), new RequiresAgentProtocol())
            .RequireAuthorization(AuthorizationPolicies.Agent);
        // Compatibility responses never bind or read a payload body, including calls from an already-leased legacy task.
        backup.MapPut("/content", RelayRetired).DisableAntiforgery();
        backup.MapGet("/content", RelayRetired);
        backup.MapGet("/metadata", RelayRetired);
        backup.MapPost("/verify", RelayRetired);
        backup.MapDelete("/content", RelayRetired);
        backup.MapPost("/access", GetBackupStorageAccessAsync);
        backup.MapPost("/abort-access", GetAbortAccessAsync);
        backup.MapPost("/intent", RecordDirectIntentAsync);
        backup.MapGet("/catalog", GetBackupStorageCatalogAsync);
        backup.MapPost("/catalog", SaveBackupStorageCatalogAsync);
        backup.MapDelete("/catalog", DeleteBackupStorageCatalogAsync);

        var restore = endpoints.MapGroup("/api/agent/restore-tasks/{attemptId:guid}/storage")
            .WithMetadata(new AgentApiEndpoint(), new RequiresAgentProtocol())
            .RequireAuthorization(AuthorizationPolicies.Agent);
        restore.MapGet("/content", RelayRetired);
        restore.MapGet("/metadata", RelayRetired);
        restore.MapPost("/verify", RelayRetired);
        restore.MapPost("/access", GetRestoreStorageAccessAsync);
        restore.MapGet("/catalog", GetRestoreStorageCatalogAsync);
        endpoints.MapPost("/api/agent/restore-tasks/{attemptId:guid}/artifact-key", UnwrapArtifactKeyAsync)
            .WithMetadata(new AgentApiEndpoint(), new RequiresAgentProtocol())
            .RequireAuthorization(AuthorizationPolicies.Agent);
        return endpoints;
    }

    /// <summary>
    /// No body in, nothing logged: the agent names no key and no wrapped DEK. The backend unwraps only what it stored for the operation's artifact, so a
    /// compromised agent cannot use this route as a decryption oracle for arbitrary ciphertext.
    /// </summary>
    private static async Task<IResult> UnwrapArtifactKeyAsync(Guid attemptId, HttpRequest request, ICurrentAgent agent, IRestoreStore restoreStore,
        IDownloadStore downloadStore, ITenantWrapKeyStore keys, CancellationToken cancellationToken)
    {
        if (attemptId == Guid.Empty || agent.Identity is null) return Results.Unauthorized();
        if (!Guid.TryParse(request.Headers[TaskStorageProtocol.LeaseHeader], out var lease) || lease == Guid.Empty
            || !Guid.TryParse(request.Headers[TaskStorageProtocol.RestoreHeader], out var operationId))
            return Results.BadRequest(new AgentApiError { Error = "invalid_request", Message = "Missing lease or operation header." });

        var tenant = agent.Identity.TenantId;
        var operation = await restoreStore.GetAsync(tenant, operationId, cancellationToken);
        if (operation is null || operation.AgentId != agent.Identity.AgentId
            || operation.CurrentAttempt.Id != attemptId || !operation.CurrentAttempt.HasLease(lease, DateTimeOffset.UtcNow))
            return Results.Conflict(new AgentApiError { Error = "lease_conflict", Message = "The restore attempt lease is invalid." });

        var artifact = await downloadStore.GetArtifactAsync(tenant, operation.ArtifactId, cancellationToken);
        if (artifact is not { Format: BackupArtifact.BpeFormat, EncryptionKeyId: { } keyId, EncryptionWrappedDekBase64: { } wrapped })
            return Results.Conflict(new AgentApiError { Error = "artifact_not_encrypted", Message = "The artifact has no encryption key." });

        byte[] dek;
        try { dek = await keys.UnwrapDekAsync(tenant, "restore_attempt", attemptId, keyId, Convert.FromBase64String(wrapped), cancellationToken); }
        catch (TenantWrapKeyUnavailableException) { return Results.Conflict(new AgentApiError { Error = "key_unavailable", Message = "The artifact key is unavailable." }); }
        try
        {
            request.HttpContext.Response.Headers.CacheControl = "no-store";
            return Results.Json(new ArtifactKeyUnwrapResponse { DekBase64 = Convert.ToBase64String(dek) });
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(dek); }
    }

    private static IResult RelayRetired() => Results.Conflict(new AgentApiError
    {
        Error = "agent_direct_required",
        Message = "External storage relay is retired. Upgrade the agent and start a new direct-capable operation; existing replicas remain unchanged."
    });


    private static async Task<(IResult? Result, Guid? TenantId, Guid? ConnectionId, StorageConnection? Connection, Guid? RunId, Guid? JobId)> FenceDirectBackupAccessAsync(
        Guid attemptId, HttpRequest request, ICurrentAgent agent, IBackupRunStore store,
        IStorageConnectionStore connections, string? requiredProvider, CancellationToken cancellationToken)
    {
        if (attemptId == Guid.Empty || agent.Identity is null)
            return (Results.Unauthorized(), null, null, null, null, null);
        if (!Guid.TryParse(request.Headers[TaskStorageProtocol.LeaseHeader], out var lease) || lease == Guid.Empty)
            return (Results.BadRequest(new AgentApiError { Error = "invalid_request", Message = "Missing lease header." }), null, null, null, null, null);
        if (!Guid.TryParse(request.Headers[TaskStorageProtocol.DestinationHeader].FirstOrDefault(), out var named) || named == Guid.Empty)
            return (Results.BadRequest(new AgentApiError { Error = "invalid_request", Message = "The destination header is missing or invalid." }), null, null, null, null, null);

        var claim = await store.GetAttemptAsync(agent.Identity.TenantId, agent.Identity.AgentId, attemptId, cancellationToken);
        if (claim is null || claim.Run.CurrentAttempt.Id != attemptId || !claim.Attempt.HasLease(lease, DateTimeOffset.UtcNow))
            return (Results.Conflict(new AgentApiError { Error = "lease_conflict", Message = "Invalid lease or storage target." }), null, null, null, null, null);

        var destination = claim.Run.Configuration.Destinations.SingleOrDefault(candidate => candidate.StorageConnectionId == named);
        if (destination is not { StorageProvider: "sftp" or "google-drive" or "s3" or "smb", Direct: true, StorageConnectionId: { } connectionId }
            || (requiredProvider is not null && destination.StorageProvider != requiredProvider))
            return (Results.Conflict(new AgentApiError { Error = "destination_not_authorized", Message = "The destination is not part of this run or not direct mode." }), null, null, null, null, null);

        var connection = (await connections.FindAsync(connectionId, agent.Identity.TenantId, cancellationToken))?.Connection;
        if (connection is null || connection.TenantId != agent.Identity.TenantId || connection.ProviderType != destination.StorageProvider || !connection.EffectiveEnabled)
            return (Results.Conflict(new AgentApiError { Error = "destination_unavailable", Message = "The destination storage connection is not available." }), null, null, null, null, null);

        return (null, agent.Identity.TenantId.Value, connectionId, connection, claim.Run.Id, claim.Run.JobId);
    }

    private static async Task<(IResult? Result, Guid? TenantId, Guid? ConnectionId, StorageConnection? Connection, string? StorageKey)> FenceDirectRestoreAccessAsync(
        Guid attemptId, HttpRequest request, ICurrentAgent agent, IRestoreStore store,
        IDownloadStore downloadStore, IStorageConnectionStore connections, string? requiredProvider, CancellationToken cancellationToken)
    {
        if (attemptId == Guid.Empty || agent.Identity is null)
            return (Results.Unauthorized(), null, null, null, null);
        if (!Guid.TryParse(request.Headers[TaskStorageProtocol.LeaseHeader], out var lease) || lease == Guid.Empty
            || !Guid.TryParse(request.Headers[TaskStorageProtocol.RestoreHeader], out var operationId))
            return (Results.BadRequest(new AgentApiError { Error = "invalid_request", Message = "Missing lease or operation header." }), null, null, null, null);

        var operation = await store.GetAsync(agent.Identity.TenantId, operationId, cancellationToken);
        if (operation is null || operation.AgentId != agent.Identity.AgentId
            || operation.CurrentAttempt.Id != attemptId || !operation.CurrentAttempt.HasLease(lease, DateTimeOffset.UtcNow))
            return (Results.Conflict(new AgentApiError { Error = "lease_conflict", Message = "The restore attempt lease is invalid." }), null, null, null, null);

        var artifact = await downloadStore.GetArtifactAsync(agent.Identity.TenantId, operation.ArtifactId, cancellationToken);
        var replica = artifact?.Replicas.SingleOrDefault(r => r.Id == operation.ReplicaId);
        if (replica is null || replica.StorageProvider is not ("sftp" or "google-drive" or "s3" or "smb") || operation.SourceAccess != RestoreSourceAccess.AgentDirect
            || !Guid.TryParse(replica.StorageTarget, out var connectionId)
            || (requiredProvider is not null && replica.StorageProvider != requiredProvider))
            return (Results.Conflict(new AgentApiError { Error = "source_inaccessible", Message = "The replica storage is inaccessible or not direct mode." }), null, null, null, null);

        var connection = (await connections.FindAsync(connectionId, agent.Identity.TenantId, cancellationToken))?.Connection;
        if (connection is null || connection.TenantId != agent.Identity.TenantId || connection.ProviderType != replica.StorageProvider || !connection.EffectiveEnabled)
            return (Results.Conflict(new AgentApiError { Error = "source_unavailable", Message = "The storage connection is unavailable." }), null, null, null, null);

        return (null, agent.Identity.TenantId.Value, connectionId, connection, replica.Key);
    }

    private static async Task<IResult> RecordDirectIntentAsync(Guid attemptId, TaskStorageVerifyRequest body, HttpRequest request,
        ICurrentAgent agent, IBackupRunStore store, IStorageConnectionStore connections, CancellationToken cancellationToken)
    {
        var fence = await FenceDirectBackupAccessAsync(attemptId, request, agent, store, connections, null, cancellationToken);
        if (fence.Result is not null) return fence.Result;
        var claim = await store.GetAttemptAsync(agent.Identity!.TenantId, agent.Identity.AgentId, attemptId, cancellationToken);
        var key = request.Headers[TaskStorageProtocol.KeyHeader].ToString();
        if (claim is null || body.SizeBytes <= 0 || body.Checksum is not { Length: 64 } || body.Checksum.Any(c => !char.IsAsciiHexDigit(c))
            || !ManagedBackupLayout.TryParseStorageKey(key, out var layout) || layout is null || layout.IsPartial
            || layout.TenantId != fence.TenantId || layout.AttemptId != attemptId || layout.JobId != fence.JobId || layout.RunId != fence.RunId)
            return Results.BadRequest(new AgentApiError { Error = "invalid_request", Message = "A bounded attempt-owned upload intent is required." });
        if (claim.MaxArtifactBytes is { } maximum && body.SizeBytes > maximum)
            return Results.Conflict(new AgentApiError { Error = "quota_exceeded", Message = "The artifact exceeds its reserved storage capacity." });
        var command = new BackupLeaseCommand(agent.Identity.TenantId, agent.Identity.AgentId, attemptId,
            Guid.Parse(request.Headers[TaskStorageProtocol.LeaseHeader]!), DateTimeOffset.UtcNow);
        return await store.RecordTransferAsync(command, new(fence.ConnectionId!.Value, key, body.SizeBytes, body.Checksum), cancellationToken)
            == BackupMutationResult.Accepted ? Results.Ok() : Results.Conflict(new AgentApiError { Error = "lease_conflict", Message = "The upload intent could not be recorded." });
    }

    /// <summary>
    /// The one route a TERMINAL attempt keeps, because its lease (and the lease-bound <c>/access</c>) is gone while remote multipart parts may remain. Served only to the agent
    /// that owns the attempt, only for a direct S3 destination of the run's own snapshot, and only once the attempt is terminal: a live attempt is fenced by its lease and gets
    /// no second path. The agent uses the credential for one thing, aborting its own journaled upload.
    /// </summary>
    private static async Task<IResult> GetAbortAccessAsync(
        Guid attemptId, HttpRequest request, ICurrentAgent agent, IBackupRunStore store,
        IStorageConnectionStore connections, ISecretProvider secrets,
        GoogleDriveConnectionProviderFactory driveFactory, CancellationToken cancellationToken)
    {
        if (attemptId == Guid.Empty || agent.Identity is null) return Results.Unauthorized();
        if (!Guid.TryParse(request.Headers[TaskStorageProtocol.DestinationHeader].FirstOrDefault(), out var named) || named == Guid.Empty)
            return Results.BadRequest(new AgentApiError { Error = "invalid_request", Message = "The destination header is missing or invalid." });

        var claim = await store.GetAttemptAsync(agent.Identity.TenantId, agent.Identity.AgentId, attemptId, cancellationToken);
        if (claim is null || !claim.Attempt.IsTerminal)
            return Results.Conflict(new AgentApiError { Error = "attempt_not_terminal", Message = "Abort access is only available for a finished attempt." });

        var destination = claim.Run.Configuration.Destinations.SingleOrDefault(candidate => candidate.StorageConnectionId == named);
        if (destination is not { StorageProvider: "s3", Direct: true, StorageConnectionId: { } connectionId })
            return Results.Conflict(new AgentApiError { Error = "destination_not_authorized", Message = "The destination is not a direct S3 destination of this run." });

        // Not required to be enabled: aborting is clean-up, and a disabled connection must not keep an upload's parts alive.
        var connection = (await connections.FindAsync(connectionId, agent.Identity.TenantId, cancellationToken))?.Connection;
        if (connection is null || connection.TenantId != agent.Identity.TenantId || connection.ProviderType != destination.StorageProvider)
            return Results.Conflict(new AgentApiError { Error = "destination_unavailable", Message = "The destination storage connection is not available." });

        return await ResolveAccessResponseAsync(request, connection, secrets, driveFactory, cancellationToken);
    }

    private static async Task<IResult> GetBackupStorageAccessAsync(
        Guid attemptId, HttpRequest request, ICurrentAgent agent, IBackupRunStore store,
        IStorageConnectionStore connections, ISecretProvider secrets,
        GoogleDriveConnectionProviderFactory driveFactory, CancellationToken cancellationToken)
    {
        var fence = await FenceDirectBackupAccessAsync(attemptId, request, agent, store, connections, null, cancellationToken);
        if (fence.Result is not null) return fence.Result;
        return await ResolveAccessResponseAsync(request, fence.Connection!, secrets, driveFactory, cancellationToken);
    }

    private static async Task<IResult> GetRestoreStorageAccessAsync(
        Guid attemptId, HttpRequest request, ICurrentAgent agent, IRestoreStore store,
        IDownloadStore downloadStore, IStorageConnectionStore connections, ISecretProvider secrets,
        GoogleDriveConnectionProviderFactory driveFactory, CancellationToken cancellationToken)
    {
        var fence = await FenceDirectRestoreAccessAsync(attemptId, request, agent, store, downloadStore, connections, null, cancellationToken);
        if (fence.Result is not null) return fence.Result;
        return await ResolveAccessResponseAsync(request, fence.Connection!, secrets, driveFactory, cancellationToken);
    }

    /// <summary>Conflict unless the key is a managed key of exactly this tenant, job, run and attempt; shared by catalog get, save and delete.</summary>
    private static IResult? CatalogKeyConflict(string key, Guid attemptId, Guid tenantId, Guid runId, Guid jobId) =>
        ManagedBackupLayout.TryParseStorageKey(key, out var layout) && layout is not null
        && layout.TenantId == tenantId && layout.AttemptId == attemptId && layout.RunId == runId && layout.JobId == jobId
            ? null
            : Results.Conflict(new AgentApiError { Error = "invalid_key", Message = "The key layout does not match the attempt." });

    private static async Task<IResult> GetBackupStorageCatalogAsync(
        Guid attemptId, HttpRequest request, ICurrentAgent agent, IBackupRunStore store,
        IStorageConnectionStore connections, IGoogleDriveFileCatalog catalog, CancellationToken cancellationToken)
    {
        var fence = await FenceDirectBackupAccessAsync(attemptId, request, agent, store, connections, "google-drive", cancellationToken);
        if (fence.Result is not null) return fence.Result;
        var key = request.Query["key"].ToString();
        if (string.IsNullOrWhiteSpace(key)) return Results.BadRequest(new AgentApiError { Error = "invalid_request", Message = "Missing key query parameter." });
        if (CatalogKeyConflict(key, attemptId, fence.TenantId!.Value, fence.RunId!.Value, fence.JobId!.Value) is { } conflict) return conflict;
        var record = await catalog.FindAsync(fence.TenantId!.Value, fence.ConnectionId!.Value, key, cancellationToken);
        return record is null
            ? Results.NotFound()
            : Results.Ok(new TaskStorageCatalogRecord(record.FileId, record.ParentFolderId, record.FileName, record.SizeBytes, record.Checksum, record.Md5Checksum));
    }

    private static async Task<IResult> SaveBackupStorageCatalogAsync(
        Guid attemptId, HttpRequest request, ICurrentAgent agent, IBackupRunStore store,
        IStorageConnectionStore connections, IGoogleDriveFileCatalog catalog, TaskStorageCatalogSaveRequest body, CancellationToken cancellationToken)
    {
        var fence = await FenceDirectBackupAccessAsync(attemptId, request, agent, store, connections, "google-drive", cancellationToken);
        if (fence.Result is not null) return fence.Result;
        if (string.IsNullOrWhiteSpace(body?.StorageKey) || body.Record is null || string.IsNullOrWhiteSpace(body.Record.FileId))
            return Results.BadRequest(new AgentApiError { Error = "invalid_request", Message = "Invalid catalog save payload." });
        if (CatalogKeyConflict(body.StorageKey, attemptId, fence.TenantId!.Value, fence.RunId!.Value, fence.JobId!.Value) is { } conflict) return conflict;
        var record = new GoogleDriveFileRecord(body.Record.FileId, body.Record.ParentFolderId, body.Record.FileName, body.Record.SizeBytes, body.Record.Checksum, body.Record.Md5Checksum);
        await catalog.SaveAsync(fence.TenantId!.Value, fence.ConnectionId!.Value, body.StorageKey, record, cancellationToken);
        return Results.Ok();
    }

    private static async Task<IResult> DeleteBackupStorageCatalogAsync(
        Guid attemptId, HttpRequest request, ICurrentAgent agent, IBackupRunStore store,
        IStorageConnectionStore connections, IGoogleDriveFileCatalog catalog, CancellationToken cancellationToken)
    {
        var fence = await FenceDirectBackupAccessAsync(attemptId, request, agent, store, connections, "google-drive", cancellationToken);
        if (fence.Result is not null) return fence.Result;
        var key = request.Query["key"].ToString();
        var expectedFileId = request.Query["expectedFileId"].ToString();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(expectedFileId))
            return Results.BadRequest(new AgentApiError { Error = "invalid_request", Message = "Missing key or expectedFileId query parameter." });
        if (CatalogKeyConflict(key, attemptId, fence.TenantId!.Value, fence.RunId!.Value, fence.JobId!.Value) is { } conflict) return conflict;
        await catalog.DeleteAsync(fence.TenantId!.Value, fence.ConnectionId!.Value, key, expectedFileId, cancellationToken);
        return Results.Ok();
    }

    private static async Task<IResult> GetRestoreStorageCatalogAsync(
        Guid attemptId, HttpRequest request, ICurrentAgent agent, IRestoreStore store,
        IDownloadStore downloadStore, IStorageConnectionStore connections, IGoogleDriveFileCatalog catalog, CancellationToken cancellationToken)
    {
        var fence = await FenceDirectRestoreAccessAsync(attemptId, request, agent, store, downloadStore, connections, "google-drive", cancellationToken);
        if (fence.Result is not null) return fence.Result;
        var key = request.Query["key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key != fence.StorageKey)
            return Results.Conflict(new AgentApiError { Error = "invalid_key", Message = "The key does not match the restored replica." });
        var record = await catalog.FindAsync(fence.TenantId!.Value, fence.ConnectionId!.Value, key, cancellationToken);
        return record is null
            ? Results.NotFound()
            : Results.Ok(new TaskStorageCatalogRecord(record.FileId, record.ParentFolderId, record.FileName, record.SizeBytes, record.Checksum, record.Md5Checksum));
    }


    internal static async Task<IResult> ResolveAccessResponseAsync(
        HttpRequest request, StorageConnection connection, ISecretProvider secrets,
        GoogleDriveConnectionProviderFactory driveFactory, CancellationToken cancellationToken)
    {
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        if (connection.ProviderType is StorageProviderTypes.S3 or StorageProviderTypes.Smb)
        {
            try
            {
                var fields = await NetworkStorageConnectionProviderFactory.ReadFieldsAsync(connection, secrets, cancellationToken);
                return Results.Ok(new TaskStorageAccessResponse
                {
                    ProviderType = connection.ProviderType, Configuration = connection.Configuration,
                    AccessKey = fields.GetValueOrDefault("accessKey"), SecretKey = fields.GetValueOrDefault("secretKey"),
                    SessionToken = fields.GetValueOrDefault("sessionToken"), Username = fields.GetValueOrDefault("username"),
                    Password = fields.GetValueOrDefault("password"), Domain = fields.GetValueOrDefault("domain")
                });
            }
            catch (StorageException)
            {
                return Results.Conflict(new AgentApiError { Error = "authentication_failed", Message = "The storage credentials are unavailable." });
            }
        }
        if (connection.ProviderType == StorageProviderTypes.GoogleDrive)
        {
            var tokenResult = await driveFactory.GetAccessTokenWithExpiryAsync(connection, secrets, cancellationToken);
            return Results.Ok(new TaskStorageAccessResponse
            {
                ProviderType = StorageProviderTypes.GoogleDrive,
                AccessToken = tokenResult.AccessToken,
                ExpiresAt = tokenResult.ExpiresAt,
                FolderId = connection.Configuration.GetValueOrDefault("folderId")
            });
        }

        if (connection.ProviderType == StorageProviderTypes.Sftp)
        {
            var config = connection.Configuration;
            int.TryParse(config.GetValueOrDefault("port"), out var port);
            if (port <= 0) port = 22;

            var authType = config.GetValueOrDefault("authenticationType");
            if (string.IsNullOrWhiteSpace(authType) || authType is not (SftpConfiguration.AuthTypePassword or SftpConfiguration.AuthTypePrivateKey))
            {
                return Results.Conflict(new AgentApiError { Error = "invalid_configuration", Message = "SFTP connection authenticationType is required." });
            }

            var remoteBaseDir = config.GetValueOrDefault("remoteBaseDirectory");
            if (string.IsNullOrWhiteSpace(remoteBaseDir))
            {
                return Results.Conflict(new AgentApiError { Error = "invalid_configuration", Message = "SFTP connection remoteBaseDirectory is required." });
            }

            var reference = connection.SecretReference;
            if (reference is null || !reference.StartsWith($"sc-{connection.Id:N}-", StringComparison.Ordinal))
            {
                return Results.Conflict(new AgentApiError { Error = "authentication_failed", Message = "SFTP connection has no valid credentials configured." });
            }

            SftpCredential credential;
            try
            {
                credential = await SftpConnectionProviderFactory.ReadCredentialAsync(secrets, new SecretReference(reference), authType, cancellationToken);
            }
            catch (StorageException)
            {
                return Results.Conflict(new AgentApiError { Error = "authentication_failed", Message = "Failed to load SFTP credentials." });
            }

            return Results.Ok(new TaskStorageAccessResponse
            {
                ProviderType = StorageProviderTypes.Sftp,
                Host = config.GetValueOrDefault("host"),
                Port = port,
                Username = config.GetValueOrDefault("username"),
                AuthenticationType = authType,
                RemoteBaseDirectory = remoteBaseDir,
                HostKeyFingerprint = config.GetValueOrDefault("hostKeyFingerprint"),
                Password = credential.Password,
                PrivateKey = credential.PrivateKey,
                Passphrase = credential.Passphrase
            });
        }

        return Results.BadRequest(new AgentApiError { Error = "unsupported_provider", Message = "Direct access is not supported for this provider." });
    }
}
