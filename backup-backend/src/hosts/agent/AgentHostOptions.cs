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
}

public sealed class AgentBackendOptions
{
    /// <summary>Absolute HTTPS address of the backend, for example <c>https://backup.example.com/</c>.</summary>
    public string? BaseUri { get; set; }
}
