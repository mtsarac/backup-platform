using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

public sealed class BackupJob : ITenantOwned
{
    private BackupJob() { TenantId = null!; AgentId = null!; Name = null!; Configuration = null!; RetryPolicy = null!; }
    public BackupJob(Guid id, TenantId tenantId, string name, AgentId agentId, BackupConfiguration configuration, DateTimeOffset createdAt,
        BackupRetryPolicy? retryPolicy = null, BackupOverlapPolicy overlapPolicy = BackupOverlapPolicy.Skip, BackupSchedule? schedule = null)
    {
        BackupGuard.Id(id);
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(agentId);
        BackupGuard.Text(name, 200);
        if (!Enum.IsDefined(overlapPolicy)) throw new ArgumentException("Unknown overlap policy.");
        Id = id;
        TenantId = tenantId;
        AgentId = agentId;
        Name = name.Trim();
        Configuration = configuration.Validate() with { };
        RetryPolicy = (retryPolicy ?? BackupRetryPolicy.Default).Validate();
        OverlapPolicy = overlapPolicy;
        Schedule = schedule;
        CreatedAt = createdAt;
        Enabled = true;
    }
    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public AgentId AgentId { get; private set; }
    public string Name { get; private set; }
    public string BackupType { get; private set; } = "Full";
    public BackupConfiguration Configuration { get; private set; }
    public BackupRetryPolicy RetryPolicy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    /// <summary>A disabled job starts nothing: no manual run and no scheduled run. Disabling is the soft delete.</summary>
    public bool Enabled { get; private set; }
    public BackupOverlapPolicy OverlapPolicy { get; private set; }
    /// <summary>Null when the job runs only on demand.</summary>
    public BackupSchedule? Schedule { get; private set; }

    /// <summary>Whether the platform should wake up for this job: the job and its schedule are both enabled.</summary>
    public bool IsScheduled => Enabled && Schedule is { Enabled: true };

    public void Disable() => Enabled = false;

    /// <summary>Re-enabling counts schedule occurrences from <paramref name="now"/> on: the time the job was off is never caught up.</summary>
    public void Enable(DateTimeOffset now)
    {
        if (!Enabled) Schedule = Schedule?.Reanchor(now);
        Enabled = true;
    }

    public void Reconfigure(BackupConfiguration configuration) => Configuration = configuration.Validate() with { };

    /// <summary>Replaces (or, with null, removes) the schedule. Past runs are untouched: they carry their own snapshots.</summary>
    public void ChangeSchedule(BackupSchedule? schedule, BackupOverlapPolicy? overlapPolicy = null)
    {
        if (overlapPolicy is { } overlap)
        {
            if (!Enum.IsDefined(overlap)) throw new ArgumentException("Unknown overlap policy.");
            OverlapPolicy = overlap;
        }
        Schedule = schedule;
    }
}
