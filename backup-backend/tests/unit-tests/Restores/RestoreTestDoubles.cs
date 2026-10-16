using BackupPlatform.Application.Backups;
using BackupPlatform.Application.Restores;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Restores;

namespace BackupPlatform.UnitTests;

/// <summary>A clock a restore test moves by hand; nothing in these tests ever waits for time to pass.</summary>
internal sealed class RestoreClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>An <see cref="IRestoreStore"/> whose answers are scripted by the test and whose every call is recorded.</summary>
internal sealed class FakeRestoreStore : IRestoreStore
{
    /// <summary>Every store member called, in order, by name.</summary>
    public List<string> Calls { get; } = [];

    public Func<RestoreCommand, RestoreCreateResult> OnCreate { get; set; } = _ => new RestoreCreateResult(RestoreCreateOutcome.ArtifactNotFound);
    public List<RestoreCommand> Created { get; } = [];

    public Func<TenantId, Guid, RestoreOperation?> OnGet { get; set; } = (_, _) => null;
    public List<(TenantId Tenant, Guid RestoreId)> Gets { get; } = [];

    public Func<TenantId, RestoreQuery, RestorePage> OnList { get; set; } = (_, query) => new RestorePage([], 0, query.Page, query.PageSize);
    public List<(TenantId Tenant, RestoreQuery Query)> Lists { get; } = [];

    public Func<TenantId, Guid, DateTimeOffset, RestoreOperation?> OnCancel { get; set; } = (_, _, _) => null;
    public List<(TenantId Tenant, Guid RestoreId, DateTimeOffset Now)> Cancellations { get; } = [];

    public RestoreClaim? ClaimResult { get; set; }
    public List<(TenantId Tenant, AgentId Agent, DateTimeOffset Now, TimeSpan Lease, TimeSpan UnclaimedExpiry)> Claims { get; } = [];

    public RestoreLeaseOutcome StartResult { get; set; } = new(BackupMutationResult.Accepted);
    public List<(RestoreLeaseCommand Command, TimeSpan Lease)> Starts { get; } = [];

    public RestoreLeaseOutcome RenewResult { get; set; } = new(BackupMutationResult.Accepted);
    public List<(RestoreLeaseCommand Command, TimeSpan Lease)> Renewals { get; } = [];

    public BackupMutationResult ProgressResult { get; set; } = BackupMutationResult.Accepted;
    public List<(RestoreLeaseCommand Command, RestoreProgress Progress)> Progresses { get; } = [];

    public BackupMutationResult CompleteResult { get; set; } = BackupMutationResult.Accepted;
    public List<(RestoreLeaseCommand Command, RestoreCompletion Completion)> Completions { get; } = [];

    public int ExpireStaleResult { get; set; }
    public List<DateTimeOffset> StaleSweeps { get; } = [];

    public int ExpireUnclaimedResult { get; set; }
    public List<(DateTimeOffset Now, TimeSpan MaximumWait)> UnclaimedSweeps { get; } = [];

    public Task<RestoreCreateResult> CreateAsync(RestoreCommand command, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(CreateAsync));
        Created.Add(command);
        return Task.FromResult(OnCreate(command));
    }

    public Task<RestoreOperation?> GetAsync(TenantId tenantId, Guid restoreId, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(GetAsync));
        Gets.Add((tenantId, restoreId));
        return Task.FromResult(OnGet(tenantId, restoreId));
    }

    public Task<RestorePage> ListAsync(TenantId tenantId, RestoreQuery query, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(ListAsync));
        Lists.Add((tenantId, query));
        return Task.FromResult(OnList(tenantId, query));
    }

    public Task<RestoreOperation?> RequestCancellationAsync(TenantId tenantId, Guid restoreId, DateTimeOffset now, CancellationToken cancellationToken, Guid? requestedByUserId = null, Guid? supportSessionId = null)
    {
        Calls.Add(nameof(RequestCancellationAsync));
        Cancellations.Add((tenantId, restoreId, now));
        return Task.FromResult(OnCancel(tenantId, restoreId, now));
    }

    public Task<RestoreClaim?> ClaimNextAsync(TenantId tenantId, AgentId agentId, DateTimeOffset now, TimeSpan leaseDuration, TimeSpan unclaimedExpiry, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(ClaimNextAsync));
        Claims.Add((tenantId, agentId, now, leaseDuration, unclaimedExpiry));
        return Task.FromResult(ClaimResult);
    }

    public Task<RestoreLeaseOutcome> StartAsync(RestoreLeaseCommand command, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(StartAsync));
        Starts.Add((command, leaseDuration));
        return Task.FromResult(StartResult);
    }

    public Task<RestoreLeaseOutcome> RenewLeaseAsync(RestoreLeaseCommand command, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(RenewLeaseAsync));
        Renewals.Add((command, leaseDuration));
        return Task.FromResult(RenewResult);
    }

    public Task<BackupMutationResult> ReportProgressAsync(RestoreLeaseCommand command, RestoreProgress progress, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(ReportProgressAsync));
        Progresses.Add((command, progress));
        return Task.FromResult(ProgressResult);
    }

    public Task<BackupMutationResult> CompleteAsync(RestoreLeaseCommand command, RestoreCompletion completion, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(CompleteAsync));
        Completions.Add((command, completion));
        return Task.FromResult(CompleteResult);
    }

    public RestoreCleanupClaim? CleanupClaimResult { get; set; }
    public List<(TenantId Tenant, AgentId Agent, DateTimeOffset Now, TimeSpan Lease)> CleanupClaims { get; } = [];
    public BackupMutationResult CleanupCompleteResult { get; set; } = BackupMutationResult.Accepted;
    public List<(TenantId Tenant, AgentId Agent, Guid OperationId, Guid Token, bool Removed, string? FailureCode)> CleanupCompletions { get; } = [];
    public RestoreCleanupRequestResult CleanupRequestResult { get; set; } = new(RestoreCleanupRequestOutcome.NotFound);
    public List<(TenantId Tenant, Guid RestoreId, TimeSpan Retention, Guid? User)> CleanupRequests { get; } = [];

    public Task<RestoreCleanupClaim?> ClaimCleanupAsync(TenantId tenantId, AgentId agentId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(ClaimCleanupAsync));
        CleanupClaims.Add((tenantId, agentId, now, leaseDuration));
        return Task.FromResult(CleanupClaimResult);
    }

    public Task<BackupMutationResult> CompleteCleanupAsync(TenantId tenantId, AgentId agentId, Guid operationId, Guid leaseToken, bool removed, string? failureCode, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Calls.Add(nameof(CompleteCleanupAsync));
        CleanupCompletions.Add((tenantId, agentId, operationId, leaseToken, removed, failureCode));
        return Task.FromResult(CleanupCompleteResult);
    }

    public Task<RestoreCleanupRequestResult> RequestCleanupAsync(TenantId tenantId, Guid restoreId, DateTimeOffset now, TimeSpan rollbackRetention, Guid? requestedByUserId,
        CancellationToken cancellationToken)
    {
        Calls.Add(nameof(RequestCleanupAsync));
        CleanupRequests.Add((tenantId, restoreId, rollbackRetention, requestedByUserId));
        return Task.FromResult(CleanupRequestResult);
    }

    public Task<int> ExpireStaleAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(ExpireStaleAsync));
        StaleSweeps.Add(now);
        return Task.FromResult(ExpireStaleResult);
    }

    public Task<int> ExpireUnclaimedAsync(DateTimeOffset now, TimeSpan maximumWait, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(ExpireUnclaimedAsync));
        UnclaimedSweeps.Add((now, maximumWait));
        return Task.FromResult(ExpireUnclaimedResult);
    }
}

/// <summary>Builds restore operations, artifacts and replicas through the public domain API only.</summary>
internal static class RestoreFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    public static readonly string Hash = new('b', 64);
    public static readonly Guid RequestedUserId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public static RestoreTarget Target() => new("sql01.corp.local", 1433, false, "sql-cred_1", true, false);

    /// <summary>A Pending operation with no attempt yet.</summary>
    public static RestoreOperation Operation(RestoreMode mode = RestoreMode.NewDatabase, string databaseName = "Sales", RestoreSourceAccess access = RestoreSourceAccess.CentralStorage,
        AgentId? agent = null, TenantId? tenant = null, Guid? replicaId = null, DateTimeOffset? createdAt = null, RestoreOptions? options = null, Guid? artifactId = null) =>
        new(Guid.NewGuid(), tenant ?? TenantId.New(), artifactId ?? Guid.NewGuid(), replicaId ?? Guid.NewGuid(), access, agent ?? AgentId.New(), Target(), databaseName, mode,
            mode == RestoreMode.ReplaceExistingDatabase, options, "idem-secret-key", "fingerprint-secret", RequestedUserId, createdAt ?? Now);

    /// <summary>A Pending operation with its first attempt queued at its creation time.</summary>
    public static RestoreOperation Queued(RestoreMode mode = RestoreMode.NewDatabase, RestoreSourceAccess access = RestoreSourceAccess.CentralStorage, RestoreOptions? options = null,
        DateTimeOffset? createdAt = null, AgentId? agent = null, TenantId? tenant = null, Guid? replicaId = null)
    {
        var operation = Operation(mode, access: access, options: options, createdAt: createdAt, agent: agent, tenant: tenant, replicaId: replicaId);
        operation.QueueFirstAttempt(Guid.NewGuid(), operation.CreatedAt);
        return operation;
    }

    public static Guid Claim(RestoreOperation operation, DateTimeOffset? at = null)
    {
        var token = Guid.NewGuid();
        operation.Claim(token, at ?? Now, Lease);
        return token;
    }

    /// <summary>Claims and starts the operation's attempt at <paramref name="at"/>; returns the lease token.</summary>
    public static Guid Begin(RestoreOperation operation, DateTimeOffset? at = null)
    {
        var time = at ?? Now;
        var token = Claim(operation, time);
        operation.Start(token, time, "1.0", "1.0", "Linux");
        return token;
    }

    public static RestoreOperation Running(out Guid token, RestoreMode mode = RestoreMode.NewDatabase, DateTimeOffset? at = null)
    {
        var operation = Queued(mode);
        token = Begin(operation, at);
        return operation;
    }

    public static RestoreCompletion Success(string? resultJson = null) => new(RestoreStatus.Succeeded, null, RestoreTargetState.Online, resultJson);

    public static RestoreCompletion Failure(string code = RestoreFailureCodes.SqlRestoreFailed, RestoreTargetState state = RestoreTargetState.PartiallyRestored) =>
        new(RestoreStatus.Failed, code, state, null);

    public static BackupArtifact Artifact(TenantId? tenant = null) =>
        new(Guid.NewGuid(), tenant ?? TenantId.New(), Guid.NewGuid(), 1000, Hash, Hash, "sha256", Now.AddDays(-1), "{}", "{}");

    public static BackupReplica CentralReplica(BackupArtifact artifact, string key = "tenant/backup-central.bak", DateTimeOffset? created = null, string verification = "Verified")
    {
        var at = created ?? Now.AddDays(-1);
        return new BackupReplica(Guid.NewGuid(), artifact.TenantId, artifact.Id, "central", "default", key, 1000, Hash, "sha256", verification, "ReadBack", at,
            verification == "Verified" ? at : null);
    }

    public static BackupReplica AgentReplica(BackupArtifact artifact, Guid owningAgent, string key = "tenant/backup-local.bak", DateTimeOffset? created = null, string verification = "Verified")
    {
        var at = created ?? Now.AddDays(-1);
        return new BackupReplica(Guid.NewGuid(), artifact.TenantId, artifact.Id, "local", "default", key, 1000, Hash, "sha256", verification, "ReadBack", at,
            verification == "Verified" ? at : null, BackupStorageExecutionLocation.Agent, owningAgent);
    }

    /// <summary>A "local" provider replica that nevertheless lives on the backend, which no agent can reach.</summary>
    public static BackupReplica BackendLocalReplica(BackupArtifact artifact, string key = "tenant/backup-backend-local.bak", DateTimeOffset? created = null)
    {
        var at = created ?? Now.AddDays(-1);
        return new BackupReplica(Guid.NewGuid(), artifact.TenantId, artifact.Id, "local", "default", key, 1000, Hash, "sha256", "Verified", "ReadBack", at, at,
            BackupStorageExecutionLocation.Backend);
    }

    public static BackupReplica AddTo(BackupArtifact artifact, BackupReplica replica)
    {
        artifact.AddReplica(replica);
        return replica;
    }
}
