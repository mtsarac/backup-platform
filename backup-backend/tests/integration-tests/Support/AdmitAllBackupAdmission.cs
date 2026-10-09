using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Identity;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// For suites that exercise run mechanics (leases, retries, overlap, retention) rather than the commercial layer: every new run is admitted and nothing
/// is reserved. The real gate has its own suites; the production composition never uses this class.
/// </summary>
internal sealed class AdmitAllBackupAdmission : IBackupAdmissionPolicy
{
    public static AdmitAllBackupAdmission Instance { get; } = new();

    public Task<BackupAdmissionDecision> AdmitNewRunAsync(BackupAdmissionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(BackupAdmissionDecision.Admitted);

    public Task ReleaseAsync(TenantId tenantId, Guid runId, CancellationToken cancellationToken) => Task.CompletedTask;
}
