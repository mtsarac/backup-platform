using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Domain.Discovery;

public sealed record DatabaseObservation(string Name, bool IsSystem, string State, string FullBackup, string? FullBackupReasonCode);

/// <summary>A database of a discovered instance. One that disappears from a successful enumeration is kept as <see cref="DiscoveryPresence.Missing"/>.</summary>
public sealed class DiscoveredDatabase : ITenantOwned
{
    public const int MaximumNameLength = 256;
    /// <summary>How long a database that has not been seen stays listed before it is purged.</summary>
    public static readonly TimeSpan MissingRetention = TimeSpan.FromDays(30);

    private DiscoveredDatabase() { TenantId = null!; Name = null!; State = null!; FullBackup = null!; }

    public DiscoveredDatabase(Guid id, TenantId tenantId, Guid instanceId, DatabaseObservation observation, DateTimeOffset now)
    {
        BackupGuard.Id(id);
        BackupGuard.Id(instanceId);
        ArgumentNullException.ThrowIfNull(tenantId);
        BackupGuard.Text(observation.Name, MaximumNameLength);
        Id = id;
        TenantId = tenantId;
        InstanceId = instanceId;
        Name = observation.Name;
        FirstSeenAt = now;
        Assign(observation, now);
    }

    public Guid Id { get; private set; }
    public TenantId TenantId { get; private set; }
    public Guid InstanceId { get; private set; }
    public string Name { get; private set; }
    public bool IsSystem { get; private set; }
    public string State { get; private set; }
    public string FullBackup { get; private set; }
    public string? FullBackupReasonCode { get; private set; }
    public DiscoveryPresence Presence { get; private set; }
    public DateTimeOffset FirstSeenAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }

    public void Observe(DatabaseObservation observation, DateTimeOffset now) => Assign(observation, now);

    public void MarkMissing() => Presence = DiscoveryPresence.Missing;

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(State), nameof(FullBackup))]
    private void Assign(DatabaseObservation observation, DateTimeOffset now)
    {
        IsSystem = observation.IsSystem;
        State = observation.State;
        FullBackup = observation.FullBackup;
        FullBackupReasonCode = observation.FullBackupReasonCode;
        Presence = DiscoveryPresence.Present;
        LastSeenAt = now;
    }
}
