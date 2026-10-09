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
}
