using BackupPlatform.Agent.Core.Connectivity;

namespace BackupPlatform.Agent.Host;

/// <summary>Bound from the <c>Agent</c> configuration section (appsettings, or environment variables such as <c>Agent__Backend__BaseUri</c>). Holds no secrets.</summary>
public sealed class AgentHostOptions
{
    public const string SectionName = "Agent";

    public AgentBackendOptions Backend { get; set; } = new();

    /// <summary>Directory of the credential store; null selects the platform default.</summary>
    public string? CredentialStoreDirectory { get; set; }

    public AgentConnectivityOptions Connectivity { get; set; } = new();

    /// <summary>Optional: staging directory and reachable storages for database backups. Absent means this agent takes no database backups.</summary>
    public AgentBackupOptions? Backup { get; set; }
}

public sealed class AgentBackendOptions
{
    /// <summary>Absolute HTTPS address of the backend, for example <c>https://backup.example.com/</c>.</summary>
    public string? BaseUri { get; set; }
}
