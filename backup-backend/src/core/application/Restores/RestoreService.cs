using System.Text;
using BackupPlatform.Application.Agents;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;

namespace BackupPlatform.Application.Restores;

public enum RestoreStartOutcome
{
    Started,
    Replayed,
    ArtifactNotFound,
    AgentNotFound,
    /// <summary>The agent exists but is not Active (disabled or revoked).</summary>
    AgentNotActive,
    /// <summary>The agent has not synced the restore capability as Supported.</summary>
    AgentCannotRestore,
    SourceRejected,
    IdempotencyConflict,
    PrecheckRejected
}

/// <summary>
/// <see cref="Code"/> is the stable machine code of a refusal (snake case); <see cref="Detail"/> narrows a code that has more than one cause;
/// <see cref="Message"/> is a fixed sentence safe to show. All three are null for Started and Replayed.
/// </summary>
public sealed record RestoreStartResult(RestoreStartOutcome Outcome, RestoreView? Restore = null, string? Code = null, string? Detail = null, string? Message = null);

/// <summary>
/// What a user may do with restores and what the target agent does with them. Every operation is scoped to one tenant (another tenant's artifact or restore is
/// reported as not found), and every eligibility and lifecycle decision is the store's, made in the database. The backend clock owns every lease and expiry.
/// </summary>
public sealed class RestoreService(IRestoreStore store, RestoreExecutionOptions options, TimeProvider time)
{
    public const string StagingReference = "default";
    private const int MaxListPageSize = 100;

    /// <summary>
    /// Validates the request, then asks the store to create the restore. The caller maps <see cref="RestoreStartResult.Outcome"/> to a response.
    /// </summary>
    /// <exception cref="InvalidRestoreRequestException">A request field is invalid.</exception>
    /// <exception cref="ArgumentException">The Idempotency-Key is not a valid bounded key.</exception>
    public Task<RestoreStartResult> StartAsync(TenantId tenantId, Guid artifactId, string idempotencyKey, Guid? requestedByUserId, StartRestoreRequest request, CancellationToken cancellationToken) =>
        CreateAsync(tenantId, artifactId, idempotencyKey, requestedByUserId, request, RestorePurpose.Execution, cancellationToken);

    public Task<RestoreStartResult> PrecheckAsync(TenantId tenantId, Guid artifactId, string idempotencyKey, Guid? requestedByUserId, StartRestorePrecheckRequest request, CancellationToken cancellationToken) =>
        CreateAsync(tenantId, artifactId, idempotencyKey, requestedByUserId,
            new StartRestoreRequest(request.AgentId, request.Target, request.DatabaseName, request.ReplicaId, request.Mode, Options: request.Options), RestorePurpose.Precheck, cancellationToken);

    public Task<RestoreStartResult> DiscoverAsync(TenantId tenantId, Guid artifactId, string idempotencyKey, Guid? requestedByUserId, StartRestoreDiscoveryRequest request, CancellationToken cancellationToken) =>
        CreateAsync(tenantId, artifactId, idempotencyKey, requestedByUserId,
            new StartRestoreRequest(request.AgentId, request.Target, string.Empty, request.ReplicaId), RestorePurpose.Discovery, cancellationToken);

    private async Task<RestoreStartResult> CreateAsync(TenantId tenantId, Guid artifactId, string idempotencyKey, Guid? requestedByUserId, StartRestoreRequest request,
        RestorePurpose purpose, CancellationToken cancellationToken)
    {
        if (!BackupInputValidation.IsValidKey(idempotencyKey)) throw new ArgumentException("A bounded Idempotency-Key is required.");
        if (artifactId == Guid.Empty) throw new InvalidRestoreRequestException("artifactId");
        var validated = RestoreInputValidation.Validate(artifactId, request, purpose);
        var command = new RestoreCommand(tenantId, artifactId, request.ReplicaId, new AgentId(request.AgentId), validated.Target, validated.DatabaseName, validated.Mode,
            validated.OverwriteConfirmed, validated.Options, idempotencyKey, validated.Fingerprint, requestedByUserId, time.GetUtcNow(), purpose, request.PrecheckId);
        var result = await store.CreateAsync(command, cancellationToken);
        return result.Outcome switch
        {
            RestoreCreateOutcome.Created => new RestoreStartResult(RestoreStartOutcome.Started, ToView(result.Operation!, includeResult: false)),
            RestoreCreateOutcome.Replayed => new RestoreStartResult(RestoreStartOutcome.Replayed, ToView(result.Operation!, includeResult: false)),
            RestoreCreateOutcome.ArtifactNotFound => new RestoreStartResult(RestoreStartOutcome.ArtifactNotFound),
            RestoreCreateOutcome.AgentNotFound => new RestoreStartResult(RestoreStartOutcome.AgentNotFound),
            RestoreCreateOutcome.AgentNotActive => new RestoreStartResult(RestoreStartOutcome.AgentNotActive, Code: "agent_cannot_restore", Detail: "agent_not_active",
                Message: "The selected agent is not active, so it cannot run a restore."),
            RestoreCreateOutcome.AgentCannotRestore => new RestoreStartResult(RestoreStartOutcome.AgentCannotRestore, Code: "agent_cannot_restore", Detail: "capability_not_supported",
                Message: "The selected agent has not reported that it can restore SQL Server databases."),
            RestoreCreateOutcome.IdempotencyConflict => new RestoreStartResult(RestoreStartOutcome.IdempotencyConflict, Code: "idempotency_conflict",
                Message: "This Idempotency-Key was already used for a different restore request."),
            RestoreCreateOutcome.PrecheckRejected => new RestoreStartResult(RestoreStartOutcome.PrecheckRejected, Code: "precheck_rejected",
                Message: "A successful precheck matching this restore request is required."),
            _ => new RestoreStartResult(RestoreStartOutcome.SourceRejected, Code: RejectionCode(result.Rejection!.Value), Message: RejectionMessage(result.Rejection.Value))
        };
    }

    public async Task<RestoreView?> GetAsync(TenantId tenantId, Guid restoreId, CancellationToken cancellationToken)
    {
        var operation = await store.GetAsync(tenantId, restoreId, cancellationToken);
        return operation is null ? null : ToView(operation, includeResult: true);
    }

    /// <summary>Newest first. The result of each restore is left out of the list: it can be large, and the detail view has it.</summary>
    public async Task<RestorePageView> ListAsync(TenantId tenantId, RestoreQuery query, CancellationToken cancellationToken)
    {
        var page = await store.ListAsync(tenantId, query with { Page = Math.Max(1, query.Page), PageSize = Math.Clamp(query.PageSize, 1, MaxListPageSize) }, cancellationToken);
        return new RestorePageView([.. page.Items.Select(operation => ToView(operation, includeResult: false))], page.TotalCount, page.Page, page.PageSize);
    }

    public async Task<RestoreView?> CancelAsync(TenantId tenantId, Guid restoreId, CancellationToken cancellationToken, Guid? requestedByUserId = null)
    {
        var operation = await store.RequestCancellationAsync(tenantId, restoreId, time.GetUtcNow(), cancellationToken, requestedByUserId);
        return operation is null ? null : ToView(operation, includeResult: true);
    }

    // ---- the target agent ----

    public async Task<RestoreTaskSnapshot?> ClaimAsync(AgentIdentity identity, CancellationToken cancellationToken)
    {
        var claim = await store.ClaimNextAsync(identity.TenantId, identity.AgentId, time.GetUtcNow(), options.LeaseDuration, options.UnclaimedExpiry, cancellationToken);
        return claim is null ? null : ToSnapshot(claim);
    }

    /// <exception cref="ArgumentException">The request is malformed.</exception>
    public Task<RestoreLeaseOutcome> StartAttemptAsync(AgentIdentity identity, Guid attemptId, BackupTaskStartRequest request, CancellationToken cancellationToken)
    {
        if (!RestoreInputValidation.IsValidStart(request)) throw new ArgumentException("Invalid start request.");
        var command = Command(identity, attemptId, request.LeaseToken) with
        {
            SoftwareVersion = request.SoftwareVersion, ProtocolVersion = request.ProtocolVersion, Platform = request.Platform
        };
        return store.StartAsync(command, options.LeaseDuration, cancellationToken);
    }

    /// <exception cref="ArgumentException">The request is malformed.</exception>
    public Task<RestoreLeaseOutcome> RenewLeaseAsync(AgentIdentity identity, Guid attemptId, BackupTaskLeaseRequest request, CancellationToken cancellationToken)
    {
        if (request.LeaseToken == Guid.Empty) throw new ArgumentException("A lease token is required.");
        return store.RenewLeaseAsync(Command(identity, attemptId, request.LeaseToken), options.LeaseDuration, cancellationToken);
    }

    /// <exception cref="ArgumentException">The request is malformed.</exception>
    public Task<BackupMutationResult> ReportProgressAsync(AgentIdentity identity, Guid attemptId, BackupTaskProgressRequest request, CancellationToken cancellationToken)
    {
        if (!RestoreInputValidation.IsValidProgress(request)) throw new ArgumentException("Invalid progress report.");
        return store.ReportProgressAsync(Command(identity, attemptId, request.LeaseToken), new RestoreProgress(request.Stage, request.Percent), cancellationToken);
    }

    /// <exception cref="ArgumentException">The completion is not a well-formed report.</exception>
    public Task<BackupMutationResult> CompleteAsync(AgentIdentity identity, Guid attemptId, RestoreTaskCompletionRequest request, CancellationToken cancellationToken)
    {
        var completion = RestoreInputValidation.ToCompletion(request);
        return store.CompleteAsync(Command(identity, attemptId, request.LeaseToken), completion, cancellationToken);
    }

    private RestoreLeaseCommand Command(AgentIdentity identity, Guid attemptId, Guid leaseToken) =>
        new(identity.TenantId, identity.AgentId, attemptId, leaseToken, time.GetUtcNow());

    // ---- mapping ----

    public static RestoreTaskSnapshot ToSnapshot(RestoreClaim claim)
    {
        var operation = claim.Operation;
        var attempt = claim.Attempt;
        var replica = claim.Replica;
        return new RestoreTaskSnapshot
        {
            OperationId = operation.Id, AttemptId = attempt.Id, AttemptNumber = attempt.Number, TenantId = operation.TenantId.Value, Purpose = operation.Purpose.ToString(),
            LeaseToken = attempt.LeaseToken!.Value, LeaseExpiresAt = attempt.LeaseExpiresAt!.Value,
            ArtifactId = operation.ArtifactId, ReplicaId = operation.ReplicaId,
            Source = new RestoreTaskSource
            {
                StorageProvider = replica.StorageProvider, StorageTarget = replica.StorageTarget, StorageKey = replica.Key,
                StorageExecutionLocation = replica.ExecutionLocation.ToString(), SizeBytes = replica.SizeBytes,
                Checksum = replica.Checksum, ChecksumAlgorithm = replica.ChecksumAlgorithm, Access = operation.SourceAccess.ToString()
            },
            Target = new RestoreTaskTarget
            {
                Host = operation.Target.Host, Port = operation.Target.Port, IntegratedSecurity = operation.Target.IntegratedSecurity,
                DatabaseCredentialReference = operation.Target.DatabaseCredentialReference, Encrypt = operation.Target.Encrypt,
                TrustServerCertificate = operation.Target.TrustServerCertificate
            },
            DatabaseName = operation.DatabaseName,
            Mode = operation.Mode.ToString(),
            Options = new RestoreTaskOptions
            {
                TerminateActiveConnections = operation.Options.TerminateActiveConnections, VerifyChecksum = operation.Options.VerifyChecksum,
                VerifyBackup = operation.Options.VerifyBackup
            },
            StagingReference = StagingReference,
            CancellationRequested = operation.CancellationRequestedAt is not null
        };
    }

    public static RestoreView ToView(RestoreOperation operation, bool includeResult)
    {
        var attempts = operation.Attempts.OrderBy(attempt => attempt.Number).ToArray();
        var current = attempts.LastOrDefault();
        var target = operation.Target;
        var options = operation.Options;
        return new RestoreView(operation.Id, operation.ArtifactId, operation.ReplicaId, operation.AgentId.Value, operation.Status.ToString(), operation.Mode.ToString(),
            operation.OverwritePolicy.ToString(), operation.SourceAccess.ToString(), operation.DatabaseName,
            new RestoreTargetView(target.Host, target.Port, target.IntegratedSecurity, target.DatabaseCredentialReference, target.Encrypt, target.TrustServerCertificate),
            new RestoreOptionsView(options.TerminateActiveConnections, options.VerifyChecksum, options.VerifyBackup),
            operation.CreatedAt, operation.StartedAt, operation.CompletedAt, operation.CancellationRequestedAt,
            operation.FailureCode, Reason(operation.FailureCode), operation.TargetState?.ToString(), TargetStateSummary(operation.TargetState),
            new RestoreProgressView(current?.ProgressStage, current?.ProgressPercent, current?.ProgressAt),
            operation.Pin.IsActive, includeResult ? RestoreInputValidation.ReadResult(operation.ResultJson) : null,
            [.. attempts.Select(attempt => new RestoreAttemptView(attempt.Id, attempt.Number, attempt.Status.ToString(), attempt.CreatedAt, attempt.LeasedAt, attempt.StartedAt,
                attempt.CompletedAt, attempt.FailureCode, Reason(attempt.FailureCode), attempt.TargetState?.ToString(), attempt.ProgressStage, attempt.ProgressPercent, attempt.ProgressAt))],
            operation.Purpose.ToString(), operation.PrecheckId);
    }

    /// <summary>The fixed sentence for a failure code; every code in the closed vocabulary has its own, anything else is reported as an internal error.</summary>
    public static string? Reason(string? code) => code switch
    {
        null => null,
        RestoreFailureCodes.ReplicaUnavailable => "The backup file could not be found or read in storage.",
        RestoreFailureCodes.ArtifactTransferFailed => "The backup file could not be copied to the agent's staging directory.",
        RestoreFailureCodes.ChecksumMismatch => "The backup file does not match its recorded checksum; nothing was restored.",
        RestoreFailureCodes.StorageUnavailable => "The backup storage was temporarily unavailable.",
        RestoreFailureCodes.BackupInvalid => "SQL Server reported the backup as unreadable or invalid.",
        RestoreFailureCodes.BackupTypeUnsupported => "Only a single full database backup can be restored.",
        RestoreFailureCodes.IncompatibleVersion => "The target SQL Server version cannot restore this backup.",
        RestoreFailureCodes.FileMappingFailed => "The database files could not be mapped to locations on the target server.",
        RestoreFailureCodes.PreCheckFailed => "The restore pre-check failed; the target database was not touched.",
        RestoreFailureCodes.TargetDatabaseExists => "The target database already exists and this restore is not allowed to replace it.",
        RestoreFailureCodes.TargetDatabaseNotFound => "The database to replace does not exist on the target server.",
        RestoreFailureCodes.TargetInUse => "The target database has active connections.",
        RestoreFailureCodes.TargetProtected => "The target database is protected and cannot be restored over.",
        RestoreFailureCodes.InsufficientDiskSpace => "The target server does not have enough disk space for the restore.",
        RestoreFailureCodes.SqlRestoreFailed => "SQL Server could not complete the restore.",
        RestoreFailureCodes.DatabaseUnavailable => "The database server could not be reached.",
        RestoreFailureCodes.OperationTimedOut => "A restore operation timed out.",
        RestoreFailureCodes.AuthenticationFailed => "The database credentials were rejected.",
        RestoreFailureCodes.PermissionDenied => "A required permission is missing.",
        RestoreFailureCodes.InvalidConfiguration => "The restore configuration is invalid.",
        RestoreFailureCodes.UnsupportedCapability => "The agent or the database does not support a requested restore capability.",
        RestoreFailureCodes.StagingInvalid => "The staging directory is not usable.",
        RestoreFailureCodes.Cancelled => "The restore was cancelled.",
        RestoreFailureCodes.AgentLost => "The agent lease expired before the restore completed.",
        RestoreFailureCodes.NotClaimed => "No agent picked up the restore in time, so it was withdrawn without touching the target.",
        _ => "The restore could not complete because of an internal error."
    };

    /// <summary>What was left on the target, in a fixed sentence. Null while the restore is not finished.</summary>
    public static string? TargetStateSummary(RestoreTargetState? state) => state switch
    {
        null => null,
        RestoreTargetState.NotModified => "The target server was not modified.",
        RestoreTargetState.Online => "The restored database is online.",
        RestoreTargetState.PartiallyRestored => "The restore did not complete: the target database may be in the RESTORING state, damaged or missing, and the previous contents of a replaced database are gone.",
        _ => "The condition of the target database could not be established; check it before using it."
    };

    /// <summary>The stable machine code of a source rejection: the enum name in snake case.</summary>
    public static string RejectionCode(RestoreSourceRejection rejection)
    {
        var name = rejection.ToString();
        var code = new StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            if (index > 0 && char.IsUpper(name[index])) code.Append('_');
            code.Append(char.ToLowerInvariant(name[index]));
        }
        return code.ToString();
    }

    public static string RejectionMessage(RestoreSourceRejection rejection) => rejection switch
    {
        RestoreSourceRejection.ReplicaNotFound => "The requested replica does not belong to this backup.",
        RestoreSourceRejection.ReplicaNotRestorable => "The replica can no longer be restored from: it was deleted, is being deleted, or its deletion grace period is over.",
        RestoreSourceRejection.ReplicaNotVerified => "The replica has not been verified, so it is not used for a restore.",
        RestoreSourceRejection.ChecksumUnavailable => "The replica has no recorded SHA-256 checksum to verify its bytes against.",
        RestoreSourceRejection.BackupTypeUnsupported => "Only a full database backup can be restored.",
        RestoreSourceRejection.AgentTransferRequired => "The only usable replica is stored on another agent's local disk. Transferring a backup between agents is not supported: " +
            "restore with the agent that owns the replica, or use a backup stored in central storage.",
        _ => "No replica of this backup is reachable by the selected agent."
    };
}
