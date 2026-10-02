namespace BackupPlatform.Contracts.Protocol;

/// <summary>
/// Advertises a wire protocol version and its declared compatibility window.
/// This contract carries data only; it does not select a negotiated version or implement runtime policy.
/// </summary>
public sealed record ProtocolNegotiationContract
{
    public ProtocolNegotiationContract(ProtocolVersion version, ProtocolVersionRange supportedVersions)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(supportedVersions);
        if (!supportedVersions.Contains(version))
        {
            throw new ArgumentException("The advertised protocol version must be in its supported window.", nameof(version));
        }
        Version = version;
        SupportedVersions = supportedVersions;
    }

    public ProtocolVersion Version { get; }

    public ProtocolVersionRange SupportedVersions { get; }
}
