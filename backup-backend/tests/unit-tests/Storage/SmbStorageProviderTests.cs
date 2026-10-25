using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Smb;
using SMBLibrary;
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
    public async Task Credential_injection_is_rejected_before_connecting()
    {
        var provider = new SmbStorageProvider(new("nas.example", "backups", Guid.NewGuid(), Guid.NewGuid()),
            _ => ValueTask.FromResult(new SmbCredential("user", "secret\nusername=admin")), null);
        var error = await Assert.ThrowsAsync<StorageException>(() => provider.ExistsAsync(new("backup.bak"), default));
        Assert.Equal(StorageFailureKind.AuthenticationFailed, error.Kind);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Theory]
    [InlineData(NTStatus.STATUS_LOGON_FAILURE, StorageFailureKind.AuthenticationFailed)]
    [InlineData(NTStatus.STATUS_ACCOUNT_DISABLED, StorageFailureKind.AuthenticationFailed)]
    [InlineData(NTStatus.STATUS_BAD_NETWORK_NAME, StorageFailureKind.InvalidConfiguration)]
    [InlineData(NTStatus.STATUS_ACCESS_DENIED, StorageFailureKind.AccessDenied)]
    [InlineData(NTStatus.STATUS_OBJECT_NAME_NOT_FOUND, StorageFailureKind.NotFound)]
    [InlineData(NTStatus.STATUS_OBJECT_NAME_COLLISION, StorageFailureKind.AlreadyExists)]
    [InlineData(NTStatus.STATUS_DISK_FULL, StorageFailureKind.InsufficientSpace)]
    public void Server_status_maps_to_a_distinguishable_failure(NTStatus status, StorageFailureKind expected) =>
        Assert.Equal(expected, SmbUserSpaceStore.Map(status).Kind);
}
