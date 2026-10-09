using System.Security.Cryptography;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.Storage;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.GoogleDrive;
using Xunit;

namespace BackupPlatform.UnitTests.Storage.GoogleDrive;

public sealed class GoogleDriveConnectionTesterTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _connectionId = Guid.NewGuid();

    [Fact]
    public async Task TestAsync_Succeeds_WhenProviderProbeSucceeds()
    {
        var stubProvider = new StubStorageProvider(succeeds: true);
        var tester = new GoogleDriveConnectionTester((conn, secrets) => stubProvider);

        var connection = new StorageConnection(
            _connectionId, new TenantId(_tenantId), "Google Drive Test", StorageProviderTypes.GoogleDrive,
            StorageOwnership.TenantManaged, "tenant", new Dictionary<string, string>(), isDefault: false, DateTimeOffset.UtcNow);

        connection.AttachSecret($"sc-{_connectionId:N}-123", ["refreshToken"], DateTimeOffset.UtcNow);

        var secrets = new StubSecretProvider();
        var result = await tester.TestAsync(connection, secrets, CancellationToken.None);

        Assert.Equal(StorageTestStatus.Succeeded, result.Status);
        Assert.Equal(StorageTestCodes.Ok, result.Code);
        Assert.True(stubProvider.PutCalled);
        Assert.True(stubProvider.VerifyCalled);
        Assert.True(stubProvider.DeleteCalled);
        Assert.True(stubProvider.ExistsCalled);
    }

    [Fact]
    public async Task TestAsync_Fails_WhenConnectionHasNoSecret()
    {
        var tester = new GoogleDriveConnectionTester((conn, secrets) => new StubStorageProvider(succeeds: true));

        var connection = new StorageConnection(
            _connectionId, new TenantId(_tenantId), "Google Drive Test", StorageProviderTypes.GoogleDrive,
            StorageOwnership.TenantManaged, "tenant", new Dictionary<string, string>(), isDefault: false, DateTimeOffset.UtcNow);

        var secrets = new StubSecretProvider();
        var result = await tester.TestAsync(connection, secrets, CancellationToken.None);

        Assert.Equal(StorageTestStatus.Failed, result.Status);
        Assert.Equal(StorageTestCodes.SecretUnavailable, result.Code);
    }

    [Fact]
    public async Task TestAsync_Fails_WhenAuthFails()
    {
        var tester = new GoogleDriveConnectionTester((conn, secrets) =>
            throw new StorageException(StorageFailureKind.AuthenticationFailed, "Invalid grant"));

        var connection = new StorageConnection(
            _connectionId, new TenantId(_tenantId), "Google Drive Test", StorageProviderTypes.GoogleDrive,
            StorageOwnership.TenantManaged, "tenant", new Dictionary<string, string>(), isDefault: false, DateTimeOffset.UtcNow);

        connection.AttachSecret($"sc-{_connectionId:N}-123", ["refreshToken"], DateTimeOffset.UtcNow);

        var secrets = new StubSecretProvider();
        var result = await tester.TestAsync(connection, secrets, CancellationToken.None);

        Assert.Equal(StorageTestStatus.Failed, result.Status);
        Assert.Equal(StorageTestCodes.AuthenticationFailed, result.Code);
    }

    [Fact]
    public async Task TestAsync_Fails_WhenProbeFailsVerification()
    {
        var stubProvider = new StubStorageProvider(succeeds: true, verificationFails: true);
        var tester = new GoogleDriveConnectionTester((conn, secrets) => stubProvider);

        var connection = new StorageConnection(
            _connectionId, new TenantId(_tenantId), "Google Drive Test", StorageProviderTypes.GoogleDrive,
            StorageOwnership.TenantManaged, "tenant", new Dictionary<string, string>(), isDefault: false, DateTimeOffset.UtcNow);

        connection.AttachSecret($"sc-{_connectionId:N}-123", ["refreshToken"], DateTimeOffset.UtcNow);

        var secrets = new StubSecretProvider();
        var result = await tester.TestAsync(connection, secrets, CancellationToken.None);

        Assert.Equal(StorageTestStatus.Failed, result.Status);
        Assert.Equal(StorageTestCodes.IntegrityMismatch, result.Code);
    }

    private sealed class StubStorageProvider(bool succeeds, bool verificationFails = false) : IStorageProvider
    {
        public bool PutCalled { get; private set; }
        public bool VerifyCalled { get; private set; }
        public bool DeleteCalled { get; private set; }
        public bool ExistsCalled { get; private set; }

        public StorageProviderId Id => GoogleDriveStorageProvider.ProviderId;
        public StorageTopology Topology => StorageTopology.BackendRelayed;
        public StorageCapabilities Capabilities => GoogleDriveStorageProvider.GoogleDriveCapabilities;

        public Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            PutCalled = true;
            if (!succeeds) throw new StorageException(StorageFailureKind.AccessDenied, "Write denied");
            return Task.FromResult(new StorageObjectMetadata(new StorageLocation(Id, key), options.ExpectedLength ?? 64, options.ExpectedChecksum, DateTimeOffset.UtcNow));
        }

        public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken)
        {
            VerifyCalled = true;
            var status = verificationFails ? StorageVerificationStatus.ChecksumMismatch : StorageVerificationStatus.Verified;
            return Task.FromResult(new StorageVerificationResult(new StorageLocation(Id, key), status, StorageVerificationMethod.ReadBack, expected.SizeBytes, expected.Checksum));
        }

        public Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            DeleteCalled = true;
            return Task.FromResult(true);
        }

        public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            ExistsCalled = true;
            return Task.FromResult(false); // absence verified
        }

        public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken) => Task.FromResult<StorageObjectMetadata?>(null);
        public Task<Stream> OpenReadAsync(StorageObjectKey key, CancellationToken cancellationToken) => Task.FromResult<Stream>(new MemoryStream());
        public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class StubSecretProvider : ISecretProvider
    {
        public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new SecretValue("{\"refreshToken\":\"test_refresh\"}"));

        public Task<bool> ExistsAsync(SecretReference reference, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
