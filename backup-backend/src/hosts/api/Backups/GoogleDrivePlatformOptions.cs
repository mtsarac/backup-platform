namespace BackupPlatform.Api.Backups;

public sealed class GoogleDrivePlatformOptions
{
    public const string SectionName = "GoogleDrive";

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>Origin of the frontend that opens the OAuth popup when it differs from the API origin (local development). Empty = same origin.</summary>
    public string FrontendOrigin { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string StagingDirectory { get; set; } = "";
    public long MinStagingFreeBytes { get; set; } = 100 * 1024 * 1024;
    public long StagingCapacityBytes { get; set; } = 10L * 1024 * 1024 * 1024;
    public long NodeStagingCapacityBytes { get; set; } = 10L * 1024 * 1024 * 1024;
    public string StagingVolumeId { get; set; } = "";
    public string StagingNodeId { get; set; } = "";
    public TimeSpan StagingLeaseDuration { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan StagingCleanupAge { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan StagingCleanupInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan UploadTimeout { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public int RetryMaxAttempts { get; set; } = 5;
    public TimeSpan RetryBudget { get; set; } = TimeSpan.FromMinutes(2);
    public int MaxSessionRestarts { get; set; } = 2;
}
