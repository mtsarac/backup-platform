namespace BackupPlatform.StorageProviders.Abstractions;

/// <summary>The agent-side copy of the domain's StorageProviderTypes (the domain depends on nothing, so the agent cannot share it). A unit test keeps the two in step.</summary>
public static class StorageProviderKinds
{
    public const string Local = "local";
    public const string Central = "central";
    public const string Sftp = "sftp";
    public const string GoogleDrive = "google-drive";
    public const string S3 = "s3";
    public const string Smb = "smb";

    /// <summary>External providers whose physical removal and repair only an agent performs; the backend holds no credential for them.</summary>
    public static bool AgentExecuted([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? provider) => provider is Sftp or GoogleDrive;

    /// <summary>Destinations outside the platform's own disks; the backend stores no payload for them.</summary>
    public static bool External([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? provider) => provider is Sftp or GoogleDrive or S3 or Smb;
}
