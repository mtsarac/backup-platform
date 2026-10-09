using System.Security.Cryptography;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Sftp;
using Xunit;

namespace BackupPlatform.IntegrationTests.Storage;

public sealed class SftpStorageProviderIntegrationTests
{
    private static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_SFTP_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);

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
        var network = new SftpNetworkOptions(allowedPrivateNetworks: ["127.0.0.0/8", "10.0.0.0/8", "192.168.0.0/16"]);

        var provider = new SftpStorageProvider(options, _ => ValueTask.FromResult(new SftpCredential(Password, null, null)), network);

        var key = new StorageObjectKey($"{tenantId:N}/job1/run1/attempt1/test_backup.bak");
        var content = RandomNumberGenerator.GetBytes(1024 * 64);
        var expectedChecksum = ContentChecksum.Sha256(SHA256.HashData(content));

        // 1. Put
        using var putStream = new MemoryStream(content);
        var meta = await provider.PutAsync(key, putStream, new StoragePutOptions(StorageOverwritePolicy.Fail, expectedChecksum, content.Length), null, CancellationToken.None);
        Assert.Equal(expectedChecksum, meta.Checksum);
        Assert.Equal(content.Length, meta.SizeBytes);

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
    }
}
