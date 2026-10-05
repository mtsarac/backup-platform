using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

public sealed class BackupRun : ITenantOwned
{
    private readonly List<BackupRunAttempt> _attempts = [];
    private readonly List<BackupLifecycleFact> _facts = [];
    private IReadOnlyList<BackupRunAttempt>? _attemptView;
    private IReadOnlyList<BackupLifecycleFact>? _factView;
    private BackupRun() { TenantId = null!; AgentId = null!; Configuration = null!; IdempotencyKey = null!; }
    public BackupRun(Guid id, TenantId tenantId, Guid jobId, AgentId agentId, BackupConfiguration configuration, string idempotencyKey, DateTimeOffset createdAt)
    {
        BackupGuard.Id(id);
        BackupGuard.Id(jobId);
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(agentId);
        BackupGuard.Text(idempotencyKey, 128);
        Id = id;
        TenantId = tenantId;
        JobId = jobId;
        AgentId = agentId;
        Configuration = configuration.Validate() with { };
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
    public string IdempotencyKey { get; private set; }
    public BackupStatus Status { get; private set; }
    public int CurrentAttemptNumber { get; private set; }
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
    public BackupRunAttempt CreateAttempt(Guid id, DateTimeOffset now)
    {
        if (CurrentAttemptNumber != 0 && Status is not (BackupStatus.Failed or BackupStatus.AgentLost))
            throw new InvalidOperationException("Only a failed or lost run can be explicitly retried.");
        if (_attempts.Any(attempt => attempt.Id == id)) throw new ArgumentException("An attempt identifier cannot be reused.");
        var attempt = new BackupRunAttempt(id, TenantId, Id, AgentId, checked(CurrentAttemptNumber + 1), now);
        _attempts.Add(attempt);
        CurrentAttemptNumber = attempt.Number;
        Status = BackupStatus.Pending;
        CompletedAt = null;
        TerminalReason = null;
        CancellationRequestedAt = null;
        Fact("AttemptQueued", attempt.Id, now);
        return attempt;
    }
    public void Claim(Guid token, DateTimeOffset now, TimeSpan duration)
    {
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
    public void Complete(Guid token, DateTimeOffset now, BackupCompletion completion)
    {
        if (!CurrentAttempt.Complete(token, now, completion)) return;
        SynchronizeTerminal(now);
    }
    public void RequestCancellation(DateTimeOffset now)
    {
        if (IsTerminal || CancellationRequestedAt.HasValue) return;
        CancellationRequestedAt = now;
        Fact("CancellationRequested", CurrentAttempt.Id, now);
        if (Status != BackupStatus.Pending) return;
        CurrentAttempt.CancelPending(now);
        SynchronizeTerminal(now);
    }
    public bool Expire(DateTimeOffset now)
    {
        if (!CurrentAttempt.Expire(now)) return false;
        SynchronizeTerminal(now);
        return true;
    }
    private void SynchronizeTerminal(DateTimeOffset now)
    {
        Status = CurrentAttempt.Status;
        CompletedAt = CurrentAttempt.CompletedAt;
        TerminalReason = CurrentAttempt.TerminalReason;
        Fact(Status switch
        {
            BackupStatus.Succeeded => "BackupRunSucceeded",
            BackupStatus.Cancelled => "BackupRunCancelled",
            _ => "BackupRunFailed"
        }, CurrentAttempt.Id, now);
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
