using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Smb;
using Xunit;

namespace BackupPlatform.UnitTests.Storage;

public sealed class SmbStorageProviderTests
{
    [Theory]
    [InlineData("host/share", "backup")]
    [InlineData("host", "../backup")]
    [InlineData("host", "backup;delete")]
    public void Invalid_network_targets_fail_without_authentication(string host, string share)
    {
        var error = Assert.Throws<StorageException>(() => new SmbStorageProvider(new(host, share, Guid.NewGuid(), Guid.NewGuid()),
            _ => throw new InvalidOperationException("Credentials must not be loaded."), null));
        Assert.Equal(StorageFailureKind.InvalidConfiguration, error.Kind);
    }

    [Fact]
    public async Task Credential_file_injection_is_rejected_before_mounting()
    {
        var provider = new SmbStorageProvider(new("nas.example", "backups", Guid.NewGuid(), Guid.NewGuid()),
            _ => ValueTask.FromResult(new SmbCredential("user", "secret\nusername=admin")), null);
        var error = await Assert.ThrowsAsync<StorageException>(() => provider.ExistsAsync(new("backup.bak"), default));
        Assert.Equal(StorageFailureKind.AuthenticationFailed, error.Kind);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public async Task Failed_unmount_after_successful_put_reports_the_written_object_as_remaining()
    {
        if (!OperatingSystem.IsLinux()) return;
        string? root = null;
        var provider = new SmbStorageProvider(new("nas.example", "backups", Guid.NewGuid(), Guid.NewGuid()),
            _ => ValueTask.FromResult(new SmbCredential("user", "secret")), null,
            (executable, arguments, _) =>
            {
                if (executable != "umount") return Task.CompletedTask;
                root = arguments[0];
                throw new StorageException(StorageFailureKind.AccessDenied, "umount failed");
            });
        var key = new StorageObjectKey("backup.bak");
        try
        {
            var error = await Assert.ThrowsAsync<StorageException>(() =>
                provider.PutAsync(key, new MemoryStream([1, 2, 3]), new(StorageOverwritePolicy.Fail), null, default));
            Assert.True(error.ObjectMayRemain);
            Assert.Equal(key, error.RemainingObjectKey);
            Assert.IsType<StorageException>(error.InnerException);
        }
        finally
        {
            if (root is not null) Directory.Delete(root, recursive: true);
        }
    }
}
