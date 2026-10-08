using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Persistence;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>
/// An admission policy that can decide inside the transaction its caller already holds. Admission, its reservation and the creation of the run then
/// commit or roll back together: a run that cannot be created leaves no reservation behind, and no second connection is held while the first waits.
/// Persistence-internal: backup orchestration only ever sees <see cref="IBackupAdmissionPolicy"/>, and this contract mentions no plan, subscription or quota.
/// </summary>
internal interface ITransactionBoundBackupAdmission
{
    /// <summary>Decides on <paramref name="context"/>'s open transaction. Neither commits nor rolls back; the caller owns the transaction.</summary>
    Task<BackupAdmissionDecision> AdmitAsync(PlatformDbContext context, BackupAdmissionRequest request, CancellationToken cancellationToken);
}

/// <summary>The one way the run and schedule stores ask for admission, so both treat every policy implementation the same way.</summary>
internal static class BackupAdmissionGate
{
    public static Task<BackupAdmissionDecision> AdmitAsync(
        IBackupAdmissionPolicy policy, PlatformDbContext context, BackupAdmissionRequest request, CancellationToken cancellationToken) =>
        policy is ITransactionBoundBackupAdmission bound
            ? bound.AdmitAsync(context, request, cancellationToken)
            : policy.AdmitNewRunAsync(request, cancellationToken);

    /// <summary>Gives a reservation back after a failure. A transaction-bound policy needs nothing: its reservation rolls back with the caller's transaction.</summary>
    public static Task ReleaseAfterFailureAsync(IBackupAdmissionPolicy policy, TenantId tenantId, Guid runId) =>
        policy is ITransactionBoundBackupAdmission ? Task.CompletedTask : policy.ReleaseAsync(tenantId, runId, CancellationToken.None);
}
