using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.ManagedService.Quota;

/// <summary>
/// A tenant's stored-bytes projection: one row per tenant. It is a read model, never a source of truth: the artifacts and replicas
/// (counted by the recorded policy) and the unreleased reservations are. Incremental updates keep it current inside the transaction that changes
/// the source; reconcile and rebuild recompute it from the sources. <see cref="PolicyId"/> and <see cref="PolicyVersion"/> say which definition of
/// "usage" it was computed under, so a projection computed under another definition is recognised as stale instead of trusted.
/// </summary>
public sealed class TenantStorageUsage : ITenantOwned
{
    private TenantStorageUsage()
    {
        TenantId = null!;
        PolicyId = null!;
    }

    public TenantId TenantId { get; private set; }
    public long UsedBytes { get; private set; }
    public long ReservedBytes { get; private set; }
    /// <summary>When the usage was last computed from the source tables (not merely adjusted).</summary>
    public DateTimeOffset CalculatedAt { get; private set; }
    public QuotaAccountingMode AccountingMode { get; private set; }
    public QuotaPolicyId PolicyId { get; private set; }
    public int PolicyVersion { get; private set; }
    /// <summary>Incremented by every write; lets a reader notice that a projection changed under it.</summary>
    public long Revision { get; private set; }
}

/// <summary>
/// Bytes set aside for one admitted run until it ends. Keyed by the run's identity, so a duplicate admission or a duplicate release can never
/// double-count. Unreleased rows are the source of truth for <see cref="TenantStorageUsage.ReservedBytes"/>.
/// </summary>
public sealed class QuotaReservation : ITenantOwned
{
    public const string ReasonRunFinished = "run_finished";
    public const string ReasonExpired = "expired";
    public const string ReasonNotCreated = "run_not_created";
    public const string ReasonOrphaned = "orphaned";

    private QuotaReservation()
    {
        TenantId = null!;
    }

    public Guid RunId { get; private set; }
    public TenantId TenantId { get; private set; }
    public long Bytes { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    /// <summary>After this instant an unreleased reservation is swept: a run that never finishes cannot hold capacity forever.</summary>
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }
    public string? ReleaseReason { get; private set; }
}
