using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

public sealed class BackupRunAttempt : ITenantOwned
{
    private readonly List<BackupArtifact> _artifacts = [];
    private IReadOnlyList<BackupArtifact>? _artifactView;
    private readonly List<BackupDestinationOutcome> _outcomes = [];
    private IReadOnlyList<BackupDestinationOutcome>? _outcomeView;
    private BackupRunAttempt() { TenantId = null!; AgentId = null!; }
    internal BackupRunAttempt(Guid id, TenantId tenantId, Guid runId, AgentId agentId, int number, DateTimeOffset createdAt, DateTimeOffset? eligibleAt = null)
    {
        BackupGuard.Id(id);
        Id = id;
        TenantId = tenantId;
        RunId = runId;
        AgentId = agentId;
        Number = number;
        CreatedAt = createdAt;
        EligibleAt = eligibleAt is { } due && due < createdAt ? due : createdAt;
        Status = BackupStatus.Pending;
    }
    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid RunId { get; private set; }
    public AgentId AgentId { get; private set; }
    public int Number { get; private set; }
    public BackupStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    /// <summary>When this attempt became eligible to run: its creation for a first or manual attempt, the scheduled due time for an automatic retry. Claim order is EligibleAt, run creation, run id.</summary>
    public DateTimeOffset EligibleAt { get; private set; }
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
    public string? TerminalReason { get; private set; }
    public IReadOnlyList<BackupArtifact> Artifacts => _artifactView ??= _artifacts.AsReadOnly();
    /// <summary>How each snapshot destination ended in this attempt. Empty for attempts that never reached a transfer, and for attempts that predate multi-destination runs.</summary>
    public IReadOnlyList<BackupDestinationOutcome> DestinationOutcomes => _outcomeView ??= _outcomes.AsReadOnly();
    public bool IsTerminal => Status is BackupStatus.Succeeded or BackupStatus.Failed or BackupStatus.Cancelled or BackupStatus.AgentLost;
    public bool HasLease(Guid token, DateTimeOffset now) => token != Guid.Empty && LeaseToken == token && LeaseExpiresAt > now && !IsTerminal;
    internal void Claim(Guid token, DateTimeOffset now, TimeSpan duration)
    {
        if (Status != BackupStatus.Pending || duration <= TimeSpan.Zero) throw new InvalidOperationException("Attempt cannot be claimed.");
        BackupGuard.Id(token);
        Status = BackupStatus.Leased;
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
        if (Status == BackupStatus.Running)
        {
            if (SoftwareVersion != software || ProtocolVersion != protocol || Platform != platform)
                throw new InvalidOperationException("Start metadata is immutable.");
            return;
        }
        Status = BackupStatus.Running;
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
        if (Status != BackupStatus.Running) throw new InvalidOperationException("Attempt is not running.");
        BackupGuard.Text(stage, 64);
        if (percent is not null && (!double.IsFinite(percent.Value) || percent is < 0 or > 100))
            throw new ArgumentException("Invalid progress percentage.");
        if (ProgressAt > now) return;
        ProgressStage = stage;
        ProgressPercent = percent;
        ProgressAt = now;
    }
    internal bool Complete(Guid token, DateTimeOffset now, BackupCompletion completion, BackupRetentionPolicy retention)
    {
        if (IsTerminal)
        {
            if (LeaseToken != token || !Matches(completion)) throw new InvalidOperationException("Terminal outcome is immutable.");
            return false;
        }
        RequireLease(token, now);
        if (completion.Status is not (BackupStatus.Succeeded or BackupStatus.Failed or BackupStatus.Cancelled))
            throw new ArgumentException("Invalid terminal outcome.");
        if (completion.Status == BackupStatus.Failed && !BackupFailureCodes.IsAgentReportable(completion.FailureCode))
            throw new ArgumentException("Invalid failure category.");
        if (completion.Status == BackupStatus.Cancelled && completion.FailureCode is not (null or "Cancelled"))
            throw new ArgumentException("Invalid cancellation category.");
        if (completion.Status == BackupStatus.Succeeded)
        {
            if (Status != BackupStatus.Running || completion.Artifact is null || completion.FailureCode is not null)
                throw new InvalidOperationException("Success requires a running attempt and artifact.");
            if (completion.Artifact.AttemptId != Id || completion.Artifact.TenantId != TenantId)
                throw new ArgumentException("Artifact ownership mismatch.");
            completion.Artifact.Seal(now, retention);
            _artifacts.Add(completion.Artifact);
        }
        else if (completion.Artifact is not null) throw new ArgumentException("Failed attempts cannot publish artifacts.");
        foreach (var outcome in completion.Outcomes ?? [])
        {
            if (outcome.AttemptId != Id || outcome.TenantId != TenantId || outcome.RunId != RunId ||
                _outcomes.Any(existing => existing.StorageProvider == outcome.StorageProvider && existing.StorageConnectionId == outcome.StorageConnectionId))
                throw new ArgumentException("Destination outcome does not belong to this attempt.");
            _outcomes.Add(outcome);
        }
        Finish(completion.Status, completion.FailureCode, now);
        return true;
    }
    internal void CancelPending(DateTimeOffset now)
    {
        if (Status != BackupStatus.Pending) throw new InvalidOperationException("Attempt is not pending.");
        Finish(BackupStatus.Cancelled, "Cancelled", now);
    }
    internal bool Expire(DateTimeOffset now)
    {
        if (Status is not (BackupStatus.Leased or BackupStatus.Running) || LeaseExpiresAt > now) return false;
        Finish(BackupStatus.AgentLost, "AgentLost", now);
        return true;
    }
    private bool Matches(BackupCompletion completion) => Status == completion.Status && TerminalReason == completion.FailureCode &&
        (completion.Artifact is null ? _artifacts.Count == 0 : _artifacts.Count == 1 && _artifacts[0].SameResult(completion.Artifact)) &&
        _outcomes.Count == (completion.Outcomes?.Count ?? 0) && (completion.Outcomes ?? []).All(incoming => _outcomes.Any(stored => stored.SameResult(incoming)));
    private void Finish(BackupStatus status, string? reason, DateTimeOffset now)
    {
        if (IsTerminal) throw new InvalidOperationException("Terminal outcome is immutable.");
        if (reason is not null) BackupGuard.Reference(reason);
        Status = status;
        TerminalReason = reason;
        CompletedAt = now;
        ProgressStage = status switch
        {
            BackupStatus.Succeeded => "Completed",
            BackupStatus.Cancelled => "Cancelled",
            _ => "Failed"
        };
        if (status == BackupStatus.Succeeded) ProgressPercent = 100;
        ProgressAt = now;
    }
    private void RequireLease(Guid token, DateTimeOffset now)
    {
        if (!HasLease(token, now)) throw new InvalidOperationException("The attempt lease is expired or lost.");
    }
}
