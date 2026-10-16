using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Restores;

/// <summary>One lease-fenced execution of a <see cref="RestoreOperation"/> by its target agent.</summary>
public sealed class RestoreAttempt : ITenantOwned
{
    private RestoreAttempt() { TenantId = null!; AgentId = null!; }
    internal RestoreAttempt(Guid id, TenantId tenantId, Guid operationId, AgentId agentId, int number, DateTimeOffset createdAt)
    {
        BackupGuard.Id(id);
        Id = id;
        TenantId = tenantId;
        OperationId = operationId;
        AgentId = agentId;
        Number = number;
        CreatedAt = createdAt;
        Status = RestoreStatus.Pending;
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid OperationId { get; private set; }
    public AgentId AgentId { get; private set; }
    public int Number { get; private set; }
    public RestoreStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LeasedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public string? SoftwareVersion { get; private set; }
    public string? ProtocolVersion { get; private set; }
    public string? Platform { get; private set; }
    public string? ProgressStage { get; private set; }
    public double? ProgressPercent { get; private set; }
    public DateTimeOffset? ProgressAt { get; private set; }
    public string? FailureCode { get; private set; }
    public RestoreTargetState? TargetState { get; private set; }

    public bool IsTerminal => Status is RestoreStatus.Succeeded or RestoreStatus.Failed or RestoreStatus.Cancelled or RestoreStatus.AgentLost;
    public bool HasLease(Guid token, DateTimeOffset now) => token != Guid.Empty && LeaseToken == token && LeaseExpiresAt > now && !IsTerminal;

    internal void Claim(Guid token, DateTimeOffset now, TimeSpan duration)
    {
        if (Status != RestoreStatus.Pending || duration <= TimeSpan.Zero) throw new InvalidOperationException("Attempt cannot be claimed.");
        BackupGuard.Id(token);
        Status = RestoreStatus.Leased;
        LeaseToken = token;
        LeasedAt = now;
        LeaseExpiresAt = now + duration;
    }

    internal void Start(Guid token, DateTimeOffset now, string software, string protocol, string platform)
    {
        RequireLease(token, now);
        BackupGuard.Text(software, 64);
        BackupGuard.Text(protocol, 32);
        BackupGuard.Text(platform, 32);
        if (Status == RestoreStatus.Running)
        {
            if (SoftwareVersion != software || ProtocolVersion != protocol || Platform != platform)
                throw new InvalidOperationException("Start metadata is immutable.");
            return;
        }
        Status = RestoreStatus.Running;
        StartedAt = now;
        SoftwareVersion = software;
        ProtocolVersion = protocol;
        Platform = platform;
    }

    internal void Renew(Guid token, DateTimeOffset now, TimeSpan duration)
    {
        RequireLease(token, now);
        if (duration <= TimeSpan.Zero) throw new ArgumentException("Invalid lease duration.");
        var expires = now + duration;
        if (expires > LeaseExpiresAt) LeaseExpiresAt = expires;
    }

    internal void Progress(Guid token, DateTimeOffset now, string stage, double? percent)
    {
        RequireLease(token, now);
        if (Status != RestoreStatus.Running) throw new InvalidOperationException("Attempt is not running.");
        if (!RestoreStages.IsKnown(stage) || stage is RestoreStages.Completed or RestoreStages.Failed or RestoreStages.Cancelled)
            throw new ArgumentException("Invalid restore stage.");
        if (percent is not null && (!double.IsFinite(percent.Value) || percent is < 0 or > 100))
            throw new ArgumentException("Invalid progress percentage.");
        if (ProgressAt > now) return;
        ProgressStage = stage;
        ProgressPercent = percent;
        ProgressAt = now;
    }

    /// <summary>False (and no change) when the same outcome was already recorded; throws when a terminal attempt is asked to change.</summary>
    internal bool Complete(Guid token, DateTimeOffset now, RestoreCompletion completion)
    {
        if (IsTerminal)
        {
            if (LeaseToken != token || Status != completion.Status || FailureCode != completion.FailureCode || TargetState != completion.TargetState)
                throw new InvalidOperationException("Terminal outcome is immutable.");
            return false;
        }
        RequireLease(token, now);
        completion.Validate(Status);
        Finish(completion.Status, completion.FailureCode, completion.TargetState, now);
        return true;
    }

    internal void CancelPending(DateTimeOffset now, string reason)
    {
        if (Status != RestoreStatus.Pending) throw new InvalidOperationException("Attempt is not pending.");
        Finish(RestoreStatus.Cancelled, reason, RestoreTargetState.NotModified, now);
    }

    /// <summary>Expired lease: only a claim that never started proves the target was untouched.</summary>
    internal bool Expire(DateTimeOffset now, RestorePurpose purpose = RestorePurpose.Execution)
    {
        if (Status is not (RestoreStatus.Leased or RestoreStatus.Running) || LeaseExpiresAt > now) return false;
        // Progress is advisory and may be stale or undelivered; a running agent could have reached RESTORE without reporting it.
        var state = purpose != RestorePurpose.Execution || Status == RestoreStatus.Leased ? RestoreTargetState.NotModified : RestoreTargetState.Unknown;
        Finish(RestoreStatus.AgentLost, RestoreFailureCodes.AgentLost, state, now);
        return true;
    }

    private void Finish(RestoreStatus status, string? reason, RestoreTargetState? targetState, DateTimeOffset now)
    {
        if (IsTerminal) throw new InvalidOperationException("Terminal outcome is immutable.");
        Status = status;
        FailureCode = reason;
        TargetState = targetState;
        CompletedAt = now;
        ProgressStage = status switch
        {
            RestoreStatus.Succeeded => RestoreStages.Completed,
            RestoreStatus.Cancelled => RestoreStages.Cancelled,
            _ => RestoreStages.Failed
        };
        if (status == RestoreStatus.Succeeded) ProgressPercent = 100;
        ProgressAt = now;
    }

    private void RequireLease(Guid token, DateTimeOffset now)
    {
        if (!HasLease(token, now)) throw new InvalidOperationException("The attempt lease is expired or lost.");
    }
}

/// <summary>
/// What an agent reports when it ends an attempt. <paramref name="TemporaryDatabaseRemoved"/> is the agent's statement that a verification's disposable database is gone (or
/// was never created); anything else leaves its removal owed. <paramref name="RetainedDatabaseName"/> is the rollback copy a replace restore kept, if any.
/// </summary>
public sealed record RestoreCompletion(RestoreStatus Status, string? FailureCode, RestoreTargetState TargetState, string? ResultJson,
    bool TemporaryDatabaseRemoved = false, string? RetainedDatabaseName = null)
{
    internal void Validate(RestoreStatus current)
    {
        if (Status is not (RestoreStatus.Succeeded or RestoreStatus.Failed or RestoreStatus.Cancelled))
            throw new ArgumentException("Invalid terminal outcome.");
        if (!Enum.IsDefined(TargetState)) throw new ArgumentException("Invalid target state.");
        switch (Status)
        {
            case RestoreStatus.Succeeded:
                if (current != RestoreStatus.Running || FailureCode is not null || TargetState is not (RestoreTargetState.Online or RestoreTargetState.NotModified))
                    throw new InvalidOperationException("Success requires a running attempt and a valid target state.");
                break;
            case RestoreStatus.Failed:
                if (!RestoreFailureCodes.IsAgentReportable(FailureCode)) throw new ArgumentException("Invalid failure category.");
                if (TargetState == RestoreTargetState.Online) throw new ArgumentException("A failed restore cannot leave an online restored database.");
                break;
            case RestoreStatus.Cancelled:
                if (FailureCode is not (null or RestoreFailureCodes.Cancelled)) throw new ArgumentException("Invalid cancellation category.");
                if (TargetState == RestoreTargetState.Online) throw new ArgumentException("A cancelled restore cannot leave an online restored database.");
                break;
        }
        if (ResultJson is { Length: > RestoreOperation.MaxResultJsonLength }) throw new ArgumentException("The restore result is too large.");
        if (RetainedDatabaseName is not null && (Status != RestoreStatus.Succeeded || !PlatformDatabaseNames.IsRollback(RetainedDatabaseName)))
            throw new ArgumentException("Only a successful restore can retain a platform rollback database.");
    }
}
