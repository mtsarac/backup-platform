using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.StorageProviders.Sftp;

public sealed class SftpPathResolver
{
    private readonly string _remoteBase;
    private readonly Guid _tenantId;
    private readonly Guid _connectionId;
    private readonly string _connectionRoot;

    public SftpPathResolver(SftpStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _remoteBase = SftpConfiguration.NormalizeRemoteBaseDirectory(options.RemoteBaseDirectory);
        _tenantId = options.TenantId;
        _connectionId = options.ConnectionId;
        _connectionRoot = $"{_remoteBase}/backup-platform/tenants/{_tenantId:N}/connections/{_connectionId:N}";
    }

    public string ConnectionRoot => _connectionRoot;

    public string ResolveCheckedRemotePath(StorageObjectKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var segments = key.Segments;
        if (segments.Count == 0)
        {
            throw new StorageException(StorageFailureKind.AccessDenied, "Invalid key: empty key.");
        }

        // Must match either:
        // 1) {tenant:N}/... (pipeline ManagedBackupLayout attempt key)
        // 2) tenants/{tenant:N}/... (parent probe key)
        if (segments[0].Equals(_tenantId.ToString("N"), StringComparison.OrdinalIgnoreCase))
        {
            // Valid tenant-scoped attempt key
        }
        else if (segments.Count >= 2 &&
                 segments[0].Equals("tenants", StringComparison.OrdinalIgnoreCase) &&
                 segments[1].Equals(_tenantId.ToString("N"), StringComparison.OrdinalIgnoreCase))
        {
            // Valid tenants/{tenantId:N}/... key
        }
        else
        {
            throw new StorageException(
                StorageFailureKind.AccessDenied,
                $"Storage key violates tenant isolation: key does not match tenant {_tenantId:N}.");
        }

        var fullRemotePath = $"{_connectionRoot}/{key.Value}";
        if (!fullRemotePath.StartsWith(_connectionRoot + "/", StringComparison.Ordinal))
        {
            throw new StorageException(StorageFailureKind.AccessDenied, "Key escapes storage connection root.");
        }

        return fullRemotePath;
    }

    public static string DeterministicTempPath(string finalRemotePath)
    {
        var lastSlash = finalRemotePath.LastIndexOf('/');
        var dir = finalRemotePath[..lastSlash];
        var file = finalRemotePath[(lastSlash + 1)..];
        return $"{dir}/.{file}.partial";
    }
}
