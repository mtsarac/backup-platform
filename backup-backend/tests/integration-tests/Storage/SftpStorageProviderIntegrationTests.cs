using System.Security.Cryptography;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Sftp;
using Xunit;

namespace BackupPlatform.IntegrationTests.Storage;

public sealed class SftpStorageProviderIntegrationTests
{
    private static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_SFTP_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);
    private static readonly string[] AllowedNetworks = ["127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"];

    private static readonly string Host = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_SFTP_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.TryParse(Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_SFTP_PORT"), out var p) ? p : 2222;
    private static readonly string Username = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_SFTP_USER") ?? "testuser";
    private static readonly string Password = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_SFTP_PASSWORD") ?? "testpass";
    private static readonly string Fingerprint = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_SFTP_FINGERPRINT") ?? "";
    private static readonly string BaseDir = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_SFTP_BASE_DIR") ?? "/upload";

    [Fact]
    public async Task SftpStorageProvider_FullLifecycle_WhenConfigured()
    {
        if (!Enabled || string.IsNullOrWhiteSpace(Fingerprint))
        {
            return;
        }

        var tenantId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var options = new SftpStorageOptions(Host, Port, Username, "password", BaseDir, Fingerprint, tenantId, connectionId);
        var network = new SftpNetworkOptions(allowedPrivateNetworks: AllowedNetworks);

        var provider = new SftpStorageProvider(options, _ => ValueTask.FromResult(new SftpCredential(Password, null, null)), network);

        // A fresh connection root and all object parents are absent.
        var missingParent = new StorageObjectKey($"{tenantId:N}/missing/parent/object.bak");
        Assert.False(await provider.DeleteAsync(missingParent, CancellationToken.None));
        Assert.False(await provider.DeleteAsync(missingParent, CancellationToken.None));
        var key = new StorageObjectKey($"{tenantId:N}/job1/run1/attempt1/test_backup.bak");
        var content = RandomNumberGenerator.GetBytes(1024 * 64);
        var expectedChecksum = ContentChecksum.Sha256(SHA256.HashData(content));

        // 1. Put
        using var putStream = new MemoryStream(content);
        var meta = await provider.PutAsync(key, putStream, new StoragePutOptions(StorageOverwritePolicy.Fail, expectedChecksum, content.Length), null, CancellationToken.None);
        Assert.Equal(expectedChecksum, meta.Checksum);
        Assert.Equal(content.Length, meta.SizeBytes);

        var missingObject = new StorageObjectKey($"{tenantId:N}/job1/run1/attempt1/missing.bak");
        Assert.False(await provider.DeleteAsync(missingObject, CancellationToken.None));
        Assert.False(await provider.DeleteAsync(missingObject, CancellationToken.None));
        // 2. Exists
        var exists = await provider.ExistsAsync(key, CancellationToken.None);
        Assert.True(exists);

        // 3. Metadata
        var readMeta = await provider.GetMetadataAsync(key, CancellationToken.None);
        Assert.NotNull(readMeta);
        Assert.Equal(content.Length, readMeta!.SizeBytes);

        // 4. Verify
        var verify = await provider.VerifyAsync(key, new StorageVerificationExpectation(content.Length, expectedChecksum), CancellationToken.None);
        Assert.Equal(StorageVerificationStatus.Verified, verify.Status);

        // 5. OpenRead
        await using (var readStream = await provider.OpenReadAsync(key, CancellationToken.None))
        {
            using var ms = new MemoryStream();
            await readStream.CopyToAsync(ms);
            Assert.Equal(content, ms.ToArray());
        }

        // 6. Delete
        var deleted = await provider.DeleteAsync(key, CancellationToken.None);
        Assert.True(deleted);

        // 7. Verify absence
        var existsAfter = await provider.ExistsAsync(key, CancellationToken.None);
        Assert.False(existsAfter);
        Assert.False(await provider.DeleteAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_PermissionDeniedParent_RemainsFailure_WhenConfigured()
    {
        if (!Enabled || string.IsNullOrWhiteSpace(Fingerprint))
        {
            return;
        }

        var tenantId = Guid.NewGuid();
        var options = new SftpStorageOptions(Host, Port, Username, "password", BaseDir, Fingerprint, tenantId, Guid.NewGuid());
        var network = new SftpNetworkOptions(allowedPrivateNetworks: AllowedNetworks);
        var provider = new SftpStorageProvider(options, _ => ValueTask.FromResult(new SftpCredential(Password, null, null)), network);
        var key = new StorageObjectKey($"{tenantId:N}/denied/object.bak");
        using var content = new MemoryStream([1, 2, 3]);
        await provider.PutAsync(key, content, new StoragePutOptions(StorageOverwritePolicy.Fail), null, CancellationToken.None);
        var path = new SftpPathResolver(options).ResolveCheckedRemotePath(key);
        var parent = path[..path.LastIndexOf('/')];
        var sessions = new SftpSessionManager(options, _ => ValueTask.FromResult(new SftpCredential(Password, null, null)), network);
        using var client = await sessions.CreateConnectedClientAsync(CancellationToken.None);

        client.ChangePermissions(parent, 0);
        try
        {
            var exception = await Assert.ThrowsAsync<StorageException>(() => provider.DeleteAsync(key, CancellationToken.None));
            Assert.Equal(StorageFailureKind.AccessDenied, exception.Kind);
        }
        finally
        {
            client.ChangePermissions(parent, 700);
            await provider.DeleteAsync(key, CancellationToken.None);
        }
    }

    [Fact]
    public async Task DeleteAsync_AuthenticationFailure_ReleasesLock_WhenConfigured()
    {
        if (!Enabled || string.IsNullOrWhiteSpace(Fingerprint))
        {
            return;
        }

        var tenantId = Guid.NewGuid();
        var options = new SftpStorageOptions(Host, Port, Username, "password", BaseDir, Fingerprint, tenantId, Guid.NewGuid());
        var network = new SftpNetworkOptions(allowedPrivateNetworks: AllowedNetworks);
        var provider = new SftpStorageProvider(options, _ => ValueTask.FromResult(new SftpCredential(Guid.NewGuid().ToString("N"), null, null)), network);
        var key = new StorageObjectKey($"{tenantId:N}/missing/object.bak");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var exception = await Assert.ThrowsAsync<StorageException>(() => provider.DeleteAsync(key, deadline.Token));
            Assert.Equal(StorageFailureKind.AuthenticationFailed, exception.Kind);
            using var content = new MemoryStream([1, 2, 3]);
            var uploadFailure = await Assert.ThrowsAsync<StorageException>(() => provider.PutAsync(key, content,
                new StoragePutOptions(StorageOverwritePolicy.Fail), null, deadline.Token));
            Assert.Equal(StorageFailureKind.AuthenticationFailed, uploadFailure.Kind);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PutAsync_FailedTemporaryCleanup_RetainsExactKey_WhenConfigured(bool cancel)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(Fingerprint))
        {
            return;
        }

        var tenantId = Guid.NewGuid();
        var options = new SftpStorageOptions(Host, Port, Username, "password", BaseDir, Fingerprint, tenantId, Guid.NewGuid());
        var network = new SftpNetworkOptions(allowedPrivateNetworks: AllowedNetworks);
        var credentials = new Func<CancellationToken, ValueTask<SftpCredential>>(_ => ValueTask.FromResult(new SftpCredential(Password, null, null)));
        var provider = new SftpStorageProvider(options, credentials, network);
        var key = new StorageObjectKey($"{tenantId:N}/cleanup/object.bak");
        var remote = new SftpPathResolver(options).ResolveCheckedRemotePath(key);
        var parent = remote[..remote.LastIndexOf('/')];
        using var client = await new SftpSessionManager(options, credentials, network).CreateConnectedClientAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        using var content = new ExplodingReadStream(() =>
        {
            client.ChangePermissions(parent, 555); // Read/search allowed; removing the partial is denied.
            if (cancel) cancellation.Cancel();
        }, cancel);
        StorageException? retained = null;
        try
        {
            var exception = await Record.ExceptionAsync(() => provider.PutAsync(key, content,
                new StoragePutOptions(StorageOverwritePolicy.Fail, ExpectedLength: 3), null, cancellation.Token));
            if (cancel)
            {
                Assert.IsAssignableFrom<OperationCanceledException>(exception);
                retained = Assert.IsType<StorageException>(exception!.Data[nameof(StorageException)]);
            }
            else
            {
                retained = Assert.IsType<StorageException>(exception);
                Assert.Equal(StorageFailureKind.Io, retained.Kind);
            }
            Assert.True(retained.ObjectMayRemain);
            Assert.Equal(new StorageObjectKey($"{tenantId:N}/cleanup/.object.bak.partial"), retained.RemainingObjectKey);
            Assert.True(await provider.ExistsAsync(retained.RemainingObjectKey!, CancellationToken.None));
            Assert.False(await provider.ExistsAsync(key, CancellationToken.None));
        }
        finally
        {
            client.ChangePermissions(parent, 700);
            await provider.DeleteAsync(new StorageObjectKey($"{tenantId:N}/cleanup/.object.bak.partial"), CancellationToken.None);
        }
    }

    [Fact]
    public async Task DeleteAsync_MissingFinal_RemovesRestartPartial_WhenConfigured()
    {
        if (!Enabled || string.IsNullOrWhiteSpace(Fingerprint))
        {
            return;
        }

        var tenantId = Guid.NewGuid();
        var options = new SftpStorageOptions(Host, Port, Username, "password", BaseDir, Fingerprint, tenantId, Guid.NewGuid());
        var network = new SftpNetworkOptions(allowedPrivateNetworks: AllowedNetworks);
        var credentials = new Func<CancellationToken, ValueTask<SftpCredential>>(_ => ValueTask.FromResult(new SftpCredential(Password, null, null)));
        var provider = new SftpStorageProvider(options, credentials, network);
        var key = new StorageObjectKey($"{tenantId:N}/restart/object.bak");
        var partial = new StorageObjectKey($"{tenantId:N}/restart/.object.bak.partial");
        using var seed = new MemoryStream([1, 2, 3]);
        await provider.PutAsync(partial, seed, new StoragePutOptions(StorageOverwritePolicy.Fail), null, CancellationToken.None);
        Assert.False(await provider.ExistsAsync(key, CancellationToken.None));
        Assert.True(await provider.ExistsAsync(partial, CancellationToken.None));

        Assert.True(await provider.DeleteAsync(key, CancellationToken.None));
        Assert.False(await provider.ExistsAsync(partial, CancellationToken.None));
        Assert.False(await provider.DeleteAsync(key, CancellationToken.None));
        Assert.False(await provider.DeleteAsync(partial, CancellationToken.None));
    }

    private sealed class ExplodingReadStream(Action beforeFailure, bool cancel) : MemoryStream(new byte[3])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            beforeFailure();
            if (cancel) throw new OperationCanceledException(cancellationToken);
            throw new IOException("Injected source read failure.");
        }
    }
}
