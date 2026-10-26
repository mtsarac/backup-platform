using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Storage;

public sealed class StorageCapacityTask : ITenantOwned
{
    private StorageCapacityTask() { TenantId = null!; }

    /// <summary>One probe of a connection that several agents use is one task per online agent; <paramref name="roundId"/> ties them so the lowest free space of the round is reported.</summary>
    public StorageCapacityTask(TenantId tenantId, Guid connectionId, Guid agentId, DateTimeOffset now, Guid? roundId = null)
    {
        Id = Guid.NewGuid();
        RoundId = roundId ?? Guid.NewGuid();
        TenantId = tenantId;
        ConnectionId = connectionId;
        AgentId = agentId;
        CreatedAt = now;
        ExpiresAt = now.AddMinutes(10);
    }

    public Guid Id { get; private set; }
    public Guid RoundId { get; private set; }
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
