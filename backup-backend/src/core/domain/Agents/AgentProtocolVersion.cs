namespace BackupPlatform.Domain.Agents;

/// <summary>Reported protocol data, not a wire contract or compatibility policy.</summary>
public sealed record AgentProtocolVersion
{
    public AgentProtocolVersion(int major, int minor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(major, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        Major = major;
        Minor = minor;
    }

    public int Major { get; }
    public int Minor { get; }
}
