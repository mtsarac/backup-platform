using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Storage;

public sealed class StorageCapacityTask : ITenantOwned
{
    private StorageCapacityTask() { TenantId = null!; }

    public StorageCapacityTask(TenantId tenantId, Guid connectionId, Guid agentId, DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        TenantId = tenantId;
        ConnectionId = connectionId;
        AgentId = agentId;
        CreatedAt = now;
        ExpiresAt = now.AddMinutes(10);
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid ConnectionId { get; private set; }
    public Guid AgentId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public DateTimeOffset? ConnectionUpdatedAt { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? CompletionJson { get; private set; }

    public void Claim(DateTimeOffset now, DateTimeOffset connectionUpdatedAt)
    {
        LeaseToken = Guid.NewGuid();
        LeaseExpiresAt = now.AddSeconds(120) < ExpiresAt ? now.AddSeconds(120) : ExpiresAt;
        ConnectionUpdatedAt = connectionUpdatedAt;
        Attempts++;
    }

    public void Complete(DateTimeOffset now, string? completionJson)
    {
        CompletedAt = now;
        CompletionJson = completionJson;
    }
}
