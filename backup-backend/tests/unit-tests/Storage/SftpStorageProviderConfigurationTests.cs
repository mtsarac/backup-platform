using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Sftp;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using Xunit;

namespace BackupPlatform.UnitTests.Storage;

public sealed class SftpStorageProviderConfigurationTests
{
    private static readonly string ValidFingerprint = Convert.ToBase64String(SHA256.HashData([1, 2, 3, 4, 5]));

    [Fact]
    public void Validate_Rejects_InvalidHost()
    {
        var options = new SftpStorageOptions("", 22, "backupuser", "password", "/backups", ValidFingerprint, Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => SftpConfiguration.Validate(options));
    }

    [Fact]
    public void Validate_Rejects_InvalidPort()
    {
        var options = new SftpStorageOptions("sftp.example.internal", 0, "backupuser", "password", "/backups", ValidFingerprint, Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => SftpConfiguration.Validate(options));
    }

    [Fact]
    public void Validate_Rejects_InvalidAuthType()
    {
        var options = new SftpStorageOptions("sftp.example.internal", 22, "backupuser", "oauth", "/backups", ValidFingerprint, Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => SftpConfiguration.Validate(options));
    }

    [Fact]
    public void Validate_Rejects_RelativeOrEscapePath()
    {
        var relative = new SftpStorageOptions("sftp.example.internal", 22, "backupuser", "password", "backups", ValidFingerprint, Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => SftpConfiguration.Validate(relative));

        var traversal = new SftpStorageOptions("sftp.example.internal", 22, "backupuser", "password", "/backups/../escaped", ValidFingerprint, Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => SftpConfiguration.Validate(traversal));

        var backslash = new SftpStorageOptions("sftp.example.internal", 22, "backupuser", "password", "/backups\\sub", ValidFingerprint, Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => SftpConfiguration.Validate(backslash));
    }

    [Fact]
    public void Validate_Rejects_MalformedHostKeyFingerprint()
    {
        var invalidBase64 = new SftpStorageOptions("sftp.example.internal", 22, "backupuser", "password", "/backups", "not-base64!", Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => SftpConfiguration.Validate(invalidBase64));

        var wrongLength = new SftpStorageOptions("sftp.example.internal", 22, "backupuser", "password", "/backups", Convert.ToBase64String([1, 2, 3]), Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => SftpConfiguration.Validate(wrongLength));
    }

    [Fact]
    public void Validate_Accepts_StandardSha256Fingerprint()
    {
        var options = new SftpStorageOptions("sftp.example.internal", 2222, "backupuser", "private-key", "/data/storage", "SHA256:" + ValidFingerprint, Guid.NewGuid(), Guid.NewGuid());
        SftpConfiguration.Validate(options);
        Assert.Equal(ValidFingerprint, SftpConfiguration.NormalizeHostKeyFingerprint(options.HostKeyFingerprint));
    }

    [Fact]
    public void NetworkValidator_Rejects_LoopbackAndPrivateIp_WhenNotAllowed()
    {
        var loopback = IPAddress.Loopback;
        var ex = Assert.Throws<StorageException>(() => SftpNetworkValidator.AssertIpPermitted(loopback, []));
        Assert.Equal(StorageFailureKind.NetworkDenied, ex.Kind);

        var privateIp = IPAddress.Parse("10.1.2.3");
        var ex2 = Assert.Throws<StorageException>(() => SftpNetworkValidator.AssertIpPermitted(privateIp, []));
        Assert.Equal(StorageFailureKind.NetworkDenied, ex2.Kind);
    }

    [Fact]
    public void NetworkValidator_Allows_PrivateIp_WhenInOperatorCidr()
    {
        var privateIp = IPAddress.Parse("10.1.2.3");
        SftpNetworkValidator.AssertIpPermitted(privateIp, ["10.0.0.0/8"]);
    }

    [Fact]
    public void PathResolver_Enforces_TenantScope()
    {
        var tenantId = Guid.NewGuid();
        var options = new SftpStorageOptions("sftp.example.internal", 22, "user", "password", "/base", ValidFingerprint, tenantId, Guid.NewGuid());
        var resolver = new SftpPathResolver(options);

        // Attempt scope key (ManagedBackupLayout)
        var attemptKey = new StorageObjectKey($"{tenantId:N}/job1/run1/attempt1/test.bak");
        var resolved = resolver.ResolveCheckedRemotePath(attemptKey);
        Assert.Equal($"/base/backup-platform/tenants/{tenantId:N}/connections/{options.ConnectionId:N}/{tenantId:N}/job1/run1/attempt1/test.bak", resolved);

        // Probe key
        var probeKey = new StorageObjectKey($"tenants/{tenantId:N}/connection-tests/{Guid.NewGuid():N}.probe");
        var probeResolved = resolver.ResolveCheckedRemotePath(probeKey);
        Assert.Equal($"/base/backup-platform/tenants/{tenantId:N}/connections/{options.ConnectionId:N}/{probeKey.Value}", probeResolved);

        // Foreign tenant rejected
        var foreignKey = new StorageObjectKey($"{Guid.NewGuid():N}/job/run/att/test.bak");
        var ex = Assert.Throws<StorageException>(() => resolver.ResolveCheckedRemotePath(foreignKey));
        Assert.Equal(StorageFailureKind.AccessDenied, ex.Kind);
    }

    [Fact]
    public void ExceptionMapper_Preserves_DeletionFailureClasses()
    {
        (Exception Exception, StorageFailureKind Kind)[] cases =
        [
            (new SftpPathNotFoundException("Missing"), StorageFailureKind.NotFound),
            (new SftpPermissionDeniedException("Denied"), StorageFailureKind.AccessDenied),
            (new SshAuthenticationException("Rejected"), StorageFailureKind.AuthenticationFailed),
            (new SshConnectionException("Disconnected", DisconnectReason.ConnectionLost), StorageFailureKind.NetworkUnavailable),
            (new SshConnectionException("Denied", DisconnectReason.HostNotAllowedToConnect), StorageFailureKind.AccessDenied),
            (new SocketException((int)SocketError.ConnectionRefused), StorageFailureKind.NetworkUnavailable),
            (new SocketException((int)SocketError.TimedOut), StorageFailureKind.Timeout),
            (new SshOperationTimeoutException("Timed out"), StorageFailureKind.Timeout),
            (new SshException("Failure"), StorageFailureKind.Io)
        ];

        foreach (var (exception, kind) in cases)
        {
            var mapped = SftpExceptionMapper.Map(exception);
            Assert.Equal(kind, mapped.Kind);
            Assert.Same(exception, mapped.InnerException);
        }
    }
}
