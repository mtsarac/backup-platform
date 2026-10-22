using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;

namespace BackupPlatform.Domain.Backups;

public enum TransferPauseReason { Operator = 1, TimeRule = 2, PausedByNetworkError = 3 }

public sealed class TransferCheckpoint : ITenantOwned
{
    private TransferCheckpoint() { TenantId = null!; StorageProvider = null!; }
    public TransferCheckpoint(TenantId tenantId, Guid attemptId, string storageProvider, Guid destinationId, long verifiedByteOffset, string? providerState, DateTimeOffset recordedAt)
    {
        if (attemptId == Guid.Empty || destinationId == Guid.Empty || verifiedByteOffset < 0 || string.IsNullOrWhiteSpace(storageProvider) || providerState?.Length > 2097152)
            throw new ArgumentException("Invalid transfer checkpoint.");
        TenantId = tenantId; AttemptId = attemptId; StorageProvider = storageProvider; DestinationId = destinationId;
        VerifiedByteOffset = verifiedByteOffset; ProviderState = providerState; RecordedAt = recordedAt;
    }
    public TenantId TenantId { get; private set; }
    public Guid AttemptId { get; private set; }
    public string StorageProvider { get; private set; }
    public Guid DestinationId { get; private set; }
    public long VerifiedByteOffset { get; private set; }
    public string? ProviderState { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }
}
