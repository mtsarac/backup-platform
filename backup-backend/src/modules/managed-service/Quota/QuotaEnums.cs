namespace BackupPlatform.ManagedService.Quota;

/// <summary>How a policy turns stored replicas into usage. Persisted as a number so a mode can be added without touching existing rows.</summary>
public enum QuotaAccountingMode
{
    /// <summary>An artifact counts once, with its own size, when at least one of its occupying replicas is in scope.</summary>
    LogicalArtifactBytes = 1,
    /// <summary>Every occupying replica in scope counts with its own size.</summary>
    PhysicalReplicaBytes = 2
}

public enum QuotaEnforcementMode
{
    /// <summary>A tenant at or over its limit cannot start a new BackupRun.</summary>
    BlockNewBackups = 1,
    /// <summary>Usage and state are tracked and shown; new runs are never refused.</summary>
    WarnOnly = 2,
    /// <summary>No quota decision at all; the tenant is reported as Unlimited.</summary>
    None = 3
}

/// <summary>Where a tenant stands against its limit. Derived on every read; never stored.</summary>
public enum QuotaState
{
    Unlimited = 0,
    Normal = 1,
    Warning = 2,
    Critical = 3,
    OverQuota = 4
}
