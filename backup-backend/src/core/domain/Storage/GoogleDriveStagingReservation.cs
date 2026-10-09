namespace BackupPlatform.Domain.Storage;

/// <summary>Global physical-byte admission; expired leases remain admitted until their tracked files are removed.</summary>
public sealed class GoogleDriveStagingReservation
{
    private GoogleDriveStagingReservation()
    {
        VolumeId = null!;
        NodeId = null!;
        DirectoryPath = null!;
        FileName = null!;
    }

    public GoogleDriveStagingReservation(Guid id, Guid leaseToken, string volumeId, string nodeId, bool sharedVolume,
        string directoryPath, long expectedBytes, DateTimeOffset createdAt, DateTimeOffset leaseExpiresAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("ID must not be empty.", nameof(id));
        if (leaseToken == Guid.Empty) throw new ArgumentException("Lease token must not be empty.", nameof(leaseToken));
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (expectedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(expectedBytes));
        Id = id;
        LeaseToken = leaseToken;
        VolumeId = volumeId;
        NodeId = nodeId;
        SharedVolume = sharedVolume;
        DirectoryPath = directoryPath;
        FileName = $"{id:N}.stage";
        ExpectedBytes = expectedBytes;
        CreatedAt = createdAt;
        LeaseExpiresAt = leaseExpiresAt;
    }

    public Guid Id { get; private set; }
    public Guid LeaseToken { get; private set; }
    public string VolumeId { get; private set; }
    public string NodeId { get; private set; }
    public bool SharedVolume { get; private set; }
    public string DirectoryPath { get; private set; }
    public string FileName { get; private set; }
    public long ExpectedBytes { get; private set; }
    public long WrittenBytes { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset LeaseExpiresAt { get; private set; }
}
