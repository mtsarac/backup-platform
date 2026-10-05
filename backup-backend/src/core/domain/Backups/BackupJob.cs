using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

public sealed class BackupJob : ITenantOwned
{
    private BackupJob() { TenantId = null!; AgentId = null!; Name = null!; Configuration = null!; RetryPolicy = null!; }
    public BackupJob(Guid id, TenantId tenantId, string name, AgentId agentId, BackupConfiguration configuration, DateTimeOffset createdAt,
        BackupRetryPolicy? retryPolicy = null, BackupOverlapPolicy overlapPolicy = BackupOverlapPolicy.Skip)
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
    public bool Enabled { get; private set; }
    public BackupOverlapPolicy OverlapPolicy { get; private set; }
    public void Disable() => Enabled = false;
    public void Enable() => Enabled = true;
    public void Reconfigure(BackupConfiguration configuration) => Configuration = configuration.Validate() with { };
}
