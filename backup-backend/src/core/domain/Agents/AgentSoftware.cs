namespace BackupPlatform.Domain.Agents;

public sealed record AgentSoftware
{
    public AgentSoftware(AgentPlatform platform, Version version, AgentProtocolVersion protocolVersion)
    {
        if (!Enum.IsDefined(platform))
        {
            throw new ArgumentOutOfRangeException(nameof(platform));
        }
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(protocolVersion);
        Platform = platform;
        Version = version;
        ProtocolVersion = protocolVersion;
    }

    public AgentPlatform Platform { get; }
    public Version Version { get; }
    public AgentProtocolVersion ProtocolVersion { get; }
}
