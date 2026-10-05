using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

public enum BackupManualRetryResult
{
    Started,
    /// <summary>The run succeeded; a successful execution is never repeated.</summary>
    Succeeded,
    /// <summary>The run was cancelled; cancelling is a decision, not a failure, so it is not retried.</summary>
    Cancelled,
    /// <summary>An attempt is queued or executing; a second one would duplicate it.</summary>
    InProgress,
    /// <summary>Every attempt the policy allows has been used.</summary>
    AttemptsExhausted
}

/// <summary>
/// One logical execution. Retrying never creates another run: it appends a <see cref="BackupRunAttempt"/> with the next number.
/// </summary>
/// <remarks>
/// <code>
/// Pending ──claim──▶ Leased ──start──▶ Running ──┬─ success ─────────────────────────────▶ Succeeded
///   ▲                                            ├─ failure/AgentLost, retryable, budget ─▶ RetryPending ──due / retry-now──▶ Pending (attempt n+1)
///   │                                            ├─ failure/AgentLost otherwise ─────────▶ Failed / AgentLost
///   │                                            └─ cancelled ───────────────────────────▶ Cancelled
///   └──── manual retry (Failed / AgentLost, budget left) ────────────────────────────────────┘
/// Pending / RetryPending ──cancel──▶ Cancelled      Leased / Running ──cancel──▶ CancellationRequested (cooperative)
/// A failure that completes after cancellation was requested ends the run as Cancelled: cancellation outranks retry.
/// </code>
/// Attempt outcomes are immutable. The run itself is reopened only by an explicit manual retry of a Failed / AgentLost run.
/// </remarks>
public sealed class BackupRun : ITenantOwned
{
    private readonly List<BackupRunAttempt> _attempts = [];
    private readonly List<BackupLifecycleFact> _facts = [];
    private IReadOnlyList<BackupRunAttempt>? _attemptView;
    private IReadOnlyList<BackupLifecycleFact>? _factView;
    private BackupRun() { TenantId = null!; AgentId = null!; Configuration = null!; IdempotencyKey = null!; RetryPolicy = null!; }
    public BackupRun(Guid id, TenantId tenantId, Guid jobId, AgentId agentId, BackupConfiguration configuration, string idempotencyKey, DateTimeOffset createdAt,
        BackupRetryPolicy? retryPolicy = null, BackupOverlapPolicy overlapPolicy = BackupOverlapPolicy.Skip)
    {
        BackupGuard.Id(id);
        BackupGuard.Id(jobId);
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(agentId);
        BackupGuard.Text(idempotencyKey, 128);
        if (!Enum.IsDefined(overlapPolicy)) throw new ArgumentException("Unknown overlap policy.");
        Id = id;
        TenantId = tenantId;
        JobId = jobId;
        AgentId = agentId;
        Configuration = configuration.Validate() with { };
        RetryPolicy = (retryPolicy ?? BackupRetryPolicy.Default).Validate();
        OverlapPolicy = overlapPolicy;
        IdempotencyKey = idempotencyKey;
        CreatedAt = createdAt;
        Status = BackupStatus.Pending;
        Fact("BackupRunCreated", null, createdAt);
    }
    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid JobId { get; private set; }
    public AgentId AgentId { get; private set; }
    public BackupConfiguration Configuration { get; private set; }
    /// <summary>Snapshot of the job's policy at creation; a later job edit does not alter this execution.</summary>
    public BackupRetryPolicy RetryPolicy { get; private set; }
    public BackupOverlapPolicy OverlapPolicy { get; private set; }
    public string IdempotencyKey { get; private set; }
    public BackupStatus Status { get; private set; }
    public int CurrentAttemptNumber { get; private set; }
    public BackupRetryState RetryState { get; private set; }
    /// <summary>Due time of the scheduled automatic retry; set exactly while <see cref="Status"/> is RetryPending.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public string BackupType { get; private set; } = "Full";
    public string TriggerType { get; private set; } = "Manual";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? CancellationRequestedAt { get; private set; }
    public string? TerminalReason { get; private set; }
    public IReadOnlyList<BackupRunAttempt> Attempts => _attemptView ??= _attempts.AsReadOnly();
    public IReadOnlyList<BackupLifecycleFact> Facts => _factView ??= _facts.AsReadOnly();
    public BackupRunAttempt CurrentAttempt => _attempts.Single(attempt => attempt.Number == CurrentAttemptNumber);
    public bool IsTerminal => Status is BackupStatus.Succeeded or BackupStatus.Failed or BackupStatus.Cancelled or BackupStatus.AgentLost;
    /// <summary>Queued, retrying or executing: the states in which another run of the same job counts as overlapping.</summary>
    public bool IsActive => !IsTerminal;

    public BackupRunAttempt QueueFirstAttempt(Guid id, DateTimeOffset now)
    {
        if (CurrentAttemptNumber != 0) throw new InvalidOperationException("The run already has its first attempt.");
        return AddAttempt(id, now, now, "AttemptQueued");
    }

    /// <summary>
    /// Creates the next attempt of an automatic retry once its backoff has elapsed. A no-op (false) unless the run is
    /// RetryPending and due, so a second worker, a manual retry or a cancellation that got there first leaves nothing to do.
    /// </summary>
    public bool StartScheduledRetry(Guid attemptId, DateTimeOffset now)
    {
        if (Status != BackupStatus.RetryPending || NextAttemptAt is not { } due || due > now) return false;
        AddAttempt(attemptId, now, due, "BackupRetryStarted");
        return true;
    }

    /// <summary>
    /// An operator's explicit retry. It never exceeds the attempt budget and never duplicates an attempt that is already queued or
    /// running. On a RetryPending run it means "retry now": the scheduled attempt is created immediately instead of at NextAttemptAt.
    /// </summary>
    public BackupManualRetryResult RequestManualRetry(Guid attemptId, DateTimeOffset now)
    {
        switch (Status)
        {
            case BackupStatus.Succeeded: return BackupManualRetryResult.Succeeded;
            case BackupStatus.Cancelled: return BackupManualRetryResult.Cancelled;
            case BackupStatus.Pending or BackupStatus.Leased or BackupStatus.Running: return BackupManualRetryResult.InProgress;
        }
        if (!RetryPolicy.HasAttemptsRemaining(CurrentAttemptNumber)) return BackupManualRetryResult.AttemptsExhausted;
        AddAttempt(attemptId, now, now, "BackupRetryStarted");
        return BackupManualRetryResult.Started;
    }

    public void Claim(Guid token, DateTimeOffset now, TimeSpan duration)
    {
        if (Status != BackupStatus.Pending) throw new InvalidOperationException("Only a pending run can be claimed.");
        CurrentAttempt.Claim(token, now, duration);
        Status = BackupStatus.Leased;
        Fact("AttemptLeased", CurrentAttempt.Id, now);
    }
    public void Start(Guid token, DateTimeOffset now, string software, string protocol, string platform)
    {
        var wasRunning = Status == BackupStatus.Running;
        CurrentAttempt.Start(token, now, software, protocol, platform);
        Status = BackupStatus.Running;
        StartedAt ??= now;
        if (!wasRunning) Fact("BackupRunStarted", CurrentAttempt.Id, now);
    }
    public void Renew(Guid token, DateTimeOffset now, TimeSpan duration) => CurrentAttempt.Renew(token, now, duration);
    public void Progress(Guid token, DateTimeOffset now, string stage, double? percent) => CurrentAttempt.Progress(token, now, stage, percent);

    /// <param name="jitter">A sample in [0,1) used to shorten a retry delay; the caller owns randomness so the domain stays deterministic.</param>
    public void Complete(Guid token, DateTimeOffset now, BackupCompletion completion, double jitter = 0)
    {
        if (!CurrentAttempt.Complete(token, now, completion)) return;
        AfterAttemptEnded(now, jitter);
    }

    public void RequestCancellation(DateTimeOffset now)
    {
        if (IsTerminal || CancellationRequestedAt.HasValue) return;
        CancellationRequestedAt = now;
        Fact("BackupCancellationRequested", CurrentAttempt.Id, now);
        switch (Status)
        {
            case BackupStatus.Pending:
                CurrentAttempt.CancelPending(now);
                EndCancelled(now);
                break;
            case BackupStatus.RetryPending:
                // The failed attempt is already terminal; cancelling only withdraws the scheduled retry.
                EndCancelled(now);
                break;
        }
    }

    public bool Expire(DateTimeOffset now, double jitter = 0)
    {
        if (!CurrentAttempt.Expire(now)) return false;
        AfterAttemptEnded(now, jitter);
        return true;
    }

    private void AfterAttemptEnded(DateTimeOffset now, double jitter)
    {
        var attempt = CurrentAttempt;
        if (attempt.Status == BackupStatus.Succeeded)
        {
            EndTerminal(BackupStatus.Succeeded, null, BackupRetryState.None, now, "BackupRunSucceeded");
            return;
        }
        if (attempt.Status == BackupStatus.Cancelled || CancellationRequestedAt.HasValue)
        {
            // Cancellation outranks retry: a failure that races a cancel request never schedules another attempt.
            EndCancelled(now);
            return;
        }
        var retryable = BackupFailureCodes.IsRetryable(attempt.TerminalReason);
        if (retryable && RetryPolicy.HasAttemptsRemaining(attempt.Number))
        {
            Status = BackupStatus.RetryPending;
            RetryState = BackupRetryState.Scheduled;
            NextAttemptAt = BackupGuard.Timestamp(now + RetryPolicy.DelayAfter(attempt.Number, jitter));
            CompletedAt = null;
            TerminalReason = null;
            Fact("BackupRetryScheduled", attempt.Id, now);
            return;
        }
        if (retryable) Fact("BackupRetryExhausted", attempt.Id, now);
        EndTerminal(attempt.Status, attempt.TerminalReason, retryable ? BackupRetryState.Exhausted : BackupRetryState.NotRetryable, now, "BackupRunFailed");
    }

    private void EndCancelled(DateTimeOffset now) => EndTerminal(BackupStatus.Cancelled, BackupFailureCodes.Cancelled, BackupRetryState.None, now, "BackupRunCancelled");

    private void EndTerminal(BackupStatus status, string? reason, BackupRetryState retryState, DateTimeOffset now, string fact)
    {
        Status = status;
        TerminalReason = reason;
        RetryState = retryState;
        NextAttemptAt = null;
        CompletedAt = now;
        Fact(fact, CurrentAttempt.Id, now);
    }

    private BackupRunAttempt AddAttempt(Guid id, DateTimeOffset now, DateTimeOffset eligibleAt, string fact)
    {
        if (_attempts.Any(attempt => attempt.Id == id)) throw new ArgumentException("An attempt identifier cannot be reused.");
        if (!RetryPolicy.HasAttemptsRemaining(CurrentAttemptNumber)) throw new InvalidOperationException("The attempt budget is spent.");
        var attempt = new BackupRunAttempt(id, TenantId, Id, AgentId, checked(CurrentAttemptNumber + 1), now, eligibleAt);
        _attempts.Add(attempt);
        CurrentAttemptNumber = attempt.Number;
        Status = BackupStatus.Pending;
        RetryState = BackupRetryState.None;
        NextAttemptAt = null;
        CompletedAt = null;
        TerminalReason = null;
        Fact(fact, attempt.Id, now);
        return attempt;
    }

    private void Fact(string kind, Guid? attemptId, DateTimeOffset now) =>
        _facts.Add(new BackupLifecycleFact(Guid.NewGuid(), TenantId, Id, attemptId, kind, now));
}

/// <summary>Durable lifecycle facts, shaped for later audit without introducing a dispatcher or event bus.</summary>
public sealed class BackupLifecycleFact : ITenantOwned
{
    private BackupLifecycleFact() { TenantId = null!; Kind = null!; }
    internal BackupLifecycleFact(Guid id, TenantId tenantId, Guid runId, Guid? attemptId, string kind, DateTimeOffset at)
    { Id = id; TenantId = tenantId; RunId = runId; AttemptId = attemptId; Kind = kind; At = at; }
    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid RunId { get; private set; }
    public Guid? AttemptId { get; private set; }
    public string Kind { get; private set; }
    public DateTimeOffset At { get; private set; }
}
