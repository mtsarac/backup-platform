using BackupPlatform.Domain.Identity;

namespace BackupPlatform.Application.Backups;

/// <summary>A new BackupRun (manual or scheduled) asking to be created. <see cref="RunId"/> is the identity the run will have if it is admitted.</summary>
public sealed record BackupAdmissionRequest(TenantId TenantId, Guid JobId, Guid RunId, string TriggerType, DateTimeOffset Now);

/// <summary>Allowed, or denied with one of the stable <see cref="BackupPlatform.Domain.Backups.BackupAdmissionDenialCodes"/>.</summary>
public sealed record BackupAdmissionDecision(bool Allowed, string? DenialCode)
{
    public static BackupAdmissionDecision Admitted { get; } = new(true, null);

    public static BackupAdmissionDecision Denied(string code) => new(false, code);
}

/// <summary>
/// The only thing backup orchestration knows about plans, subscriptions, quota and usage: whether a NEW run may be created, and why not.
/// It is asked exactly where a run is created (manual start and scheduled occurrence). A retry attempt of an admitted run never asks again.
/// </summary>
/// <remarks>
/// An admitted request may hold a reservation keyed by <see cref="BackupAdmissionRequest.RunId"/>. The reservation is released by the persistence
/// layer when the run reaches a terminal state, and lapses on its own if the run is never created or never finishes; a caller whose run creation
/// fails after admission calls <see cref="ReleaseAsync"/> to give it back at once.
/// </remarks>
public interface IBackupAdmissionPolicy
{
    Task<BackupAdmissionDecision> AdmitNewRunAsync(BackupAdmissionRequest request, CancellationToken cancellationToken);

    /// <summary>Gives back what <see cref="AdmitNewRunAsync"/> reserved for a run that was not created. Idempotent.</summary>
    Task ReleaseAsync(TenantId tenantId, Guid runId, CancellationToken cancellationToken);
}
