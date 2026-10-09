namespace BackupPlatform.StorageProviders.Sftp;

public sealed record SftpStorageOptions(
    string Host,
    int Port,
    string Username,
    string AuthenticationType,
    string RemoteBaseDirectory,
    string HostKeyFingerprint,
    Guid TenantId,
    Guid ConnectionId);

public sealed record SftpCredential(
    string? Password,
    string? PrivateKey,
    string? Passphrase)
{
    public override string ToString() => "[redacted]";
}

public sealed class SftpNetworkOptions
{
    public static TimeSpan DefaultConnectionTimeout { get; } = TimeSpan.FromSeconds(15);
    public static TimeSpan DefaultOperationTimeout { get; } = TimeSpan.FromSeconds(30);
    public static TimeSpan DefaultTransferTimeout { get; } = TimeSpan.FromHours(1);

    public string[] AllowedPrivateNetworks { get; init; }
    public TimeSpan ConnectionTimeout { get; init; }
    public TimeSpan OperationTimeout { get; init; }
    public TimeSpan TransferTimeout { get; init; }

    public SftpNetworkOptions(
        string[]? allowedPrivateNetworks = null,
        TimeSpan? connectionTimeout = null,
        TimeSpan? operationTimeout = null,
        TimeSpan? transferTimeout = null)
    {
        AllowedPrivateNetworks = allowedPrivateNetworks ?? [];
        ConnectionTimeout = connectionTimeout ?? DefaultConnectionTimeout;
        OperationTimeout = operationTimeout ?? DefaultOperationTimeout;
        TransferTimeout = transferTimeout ?? DefaultTransferTimeout;
    }

    public SftpNetworkOptions() : this(null, null, null, null) { }
}
