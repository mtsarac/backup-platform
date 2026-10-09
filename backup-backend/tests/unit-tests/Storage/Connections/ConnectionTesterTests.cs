using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.Storage;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Central;

namespace BackupPlatform.UnitTests.Storage.Connections;

public sealed class ConnectionTesterTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bp_conn_tester_" + Guid.NewGuid().ToString("N"));
    private readonly TenantId _tenant = TenantId.New();
    private readonly InMemorySecretStore _secrets = new();

    public ConnectionTesterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
            {
                if (!OperatingSystem.IsWindows() && Directory.Exists(path)) File.SetUnixFileMode(path, (UnixFileMode)0b111_111_111);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_root, (UnixFileMode)0b111_111_111);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FixedResolver(IStorageProvider? provider) : IBackupStorageResolver
    {
        public List<(TenantId Tenant, string Provider, string Target)> Requests { get; } = [];

        public IStorageProvider? Resolve(TenantId tenantId, string storageProvider, string storageTarget)
        {
            Requests.Add((tenantId, storageProvider, storageTarget));
            return provider;
        }

        public IReadOnlyList<BackupManagedStorage> ManagedStorages => [];
    }

    /// <summary>A provider whose every operation is scripted; unscripted operations fail the test so nothing is silently skipped.</summary>
    private sealed class ScriptedProvider(IEnumerable<StorageCapabilityId> capabilities) : IStorageProvider
    {
        public StorageProviderId Id => StorageProviderId.Central;
        public StorageTopology Topology => StorageTopology.AgentDirect;
        public StorageCapabilities Capabilities { get; } = new(capabilities);

        public Func<StorageObjectKey, Task>? OnPut { get; set; }
        public StorageVerificationStatus VerifyStatus { get; set; } = StorageVerificationStatus.Verified;
        public bool ExistsAfterDelete { get; set; }
        public List<string> Calls { get; } = [];
        public HashSet<string> Present { get; } = [];

        public async Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            Calls.Add("put");
            if (OnPut is not null) await OnPut(key);
            Present.Add(key.Value);
            return new StorageObjectMetadata(new StorageLocation(Id, key), content.Length, null, Now);
        }

        public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            Calls.Add("exists");
            return Task.FromResult(Present.Contains(key.Value));
        }

        public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken)
        {
            Calls.Add("verify");
            return Task.FromResult(new StorageVerificationResult(new StorageLocation(Id, key), VerifyStatus, StorageVerificationMethod.ReadBack, null, null));
        }

        public Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken)
        {
            Calls.Add("delete");
            if (ExistsAfterDelete) return Task.FromResult(true);
            return Task.FromResult(Present.Remove(key.Value));
        }

        public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private StorageConnection Connection() =>
        new(Guid.NewGuid(), _tenant, "Managed Storage", StorageProviderTypes.Central, StorageOwnership.PlatformManaged, null, null, isDefault: true, Now);

    private CentralStorageProvider RealProvider(string? root = null) => new(new CentralStorageOptions(root ?? _root, "scope"));

    private Task<StorageTestResult> Run(IStorageProvider? provider) =>
        new CentralConnectionTester(new FixedResolver(provider)).TestAsync(Connection(), _secrets, CancellationToken.None);

    private static async Task<List<StorageEntry>> Entries(IStorageProvider provider)
    {
        var entries = new List<StorageEntry>();
        await foreach (var entry in provider.EnumerateAsync(null, DateTimeOffset.MaxValue, CancellationToken.None)) entries.Add(entry);
        return entries;
    }

    // ---- Central, real file-system provider --------------------------------------------------------------------------------

    [Fact]
    public async Task ARealRoundTripSucceedsAndLeavesNoProbeBehind()
    {
        var provider = RealProvider();

        var result = await Run(provider);

        Assert.Equal(StorageTestStatus.Succeeded, result.Status);
        Assert.Equal(StorageTestCodes.Ok, result.Code);
        Assert.Empty(await Entries(provider));
    }

    [Fact]
    public async Task TheTesterAsksTheResolverForTheConnectionsTenantAndTheCentralStorage()
    {
        var resolver = new FixedResolver(RealProvider());
        var connection = Connection();

        await new CentralConnectionTester(resolver).TestAsync(connection, _secrets, CancellationToken.None);

        var request = Assert.Single(resolver.Requests);
        Assert.Equal(connection.TenantId, request.Tenant);
        Assert.Equal("central", request.Provider);
        Assert.Equal("central", request.Target);
    }

    [Fact]
    public async Task WhenNoCentralStorageIsConfiguredTheTestFailsAsNotConfigured()
    {
        var result = await Run(null);

        Assert.Equal(StorageTestStatus.Failed, result.Status);
        Assert.Equal(StorageTestCodes.StorageNotConfigured, result.Code);
    }

    [Fact]
    public async Task AMissingRootDirectoryFailsAsInvalidConfiguration()
    {
        var provider = RealProvider(Path.Combine(_root, "does-not-exist"));

        var result = await Run(provider);

        Assert.Equal(StorageTestStatus.Failed, result.Status);
        Assert.Equal(StorageTestCodes.InvalidConfiguration, result.Code);
    }

    [Fact]
    public async Task AFailureMessageNeverContainsTheFileSystemPath()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        var result = await Run(RealProvider(missing));

        Assert.DoesNotContain(missing, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_root, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnwritableRootFailsAsAccessDeniedAndLeavesNothingBehind()
    {
        if (OperatingSystem.IsWindows()) return;
        var locked = Path.Combine(_root, "locked");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            // Running as root (or with CAP_DAC_OVERRIDE) the mode is not enforced: nothing to prove then.
            try
            {
                File.WriteAllText(Path.Combine(locked, "probe"), "x");
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }

            var result = await Run(RealProvider(locked));

            Assert.Equal(StorageTestStatus.Failed, result.Status);
            Assert.Equal(StorageTestCodes.AccessDenied, result.Code);
            Assert.Empty(Directory.EnumerateFileSystemEntries(locked));
        }
        finally
        {
            File.SetUnixFileMode(locked, (UnixFileMode)0b111_111_111);
        }
    }

    // ---- scripted providers ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AProviderThatCannotDeleteFailsAsNotSupportedWithoutWritingAProbe()
    {
        var provider = new ScriptedProvider([StorageCapabilityIds.Read]);

        var result = await Run(provider);

        Assert.Equal(StorageTestStatus.Failed, result.Status);
        Assert.Equal(StorageTestCodes.NotSupported, result.Code);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task AFullRoundTripWritesVerifiesDeletesAndChecksAbsenceInThatOrder()
    {
        var provider = new ScriptedProvider([StorageCapabilityIds.Delete]);

        var result = await Run(provider);

        Assert.Equal(StorageTestStatus.Succeeded, result.Status);
        Assert.Equal(["put", "verify", "delete", "exists"], provider.Calls);
        Assert.Empty(provider.Present);
    }

    [Theory]
    [InlineData(StorageVerificationStatus.Missing)]
    [InlineData(StorageVerificationStatus.SizeMismatch)]
    [InlineData(StorageVerificationStatus.ChecksumMismatch)]
    [InlineData(StorageVerificationStatus.Inconclusive)]
    public async Task AnythingOtherThanVerifiedIsAnIntegrityFailureAndTheProbeIsStillRemoved(StorageVerificationStatus status)
    {
        var provider = new ScriptedProvider([StorageCapabilityIds.Delete]) { VerifyStatus = status };

        var result = await Run(provider);

        Assert.Equal(StorageTestStatus.Failed, result.Status);
        Assert.Equal(StorageTestCodes.IntegrityMismatch, result.Code);
        Assert.Equal(["put", "verify", "delete"], provider.Calls);
        Assert.Empty(provider.Present);
    }

    [Fact]
    public async Task AProbeThatSurvivesItsDeletionIsReportedAsAnIoFailure()
    {
        var provider = new ScriptedProvider([StorageCapabilityIds.Delete]) { ExistsAfterDelete = true };
        provider.Present.Add("placeholder");

        var result = await Run(provider);

        Assert.Equal(StorageTestStatus.Failed, result.Status);
        Assert.Equal(StorageTestCodes.IoError, result.Code);
    }

    [Theory]
    [InlineData(StorageFailureKind.AccessDenied, StorageTestCodes.AccessDenied)]
    [InlineData(StorageFailureKind.InvalidConfiguration, StorageTestCodes.InvalidConfiguration)]
    [InlineData(StorageFailureKind.InsufficientSpace, StorageTestCodes.InsufficientSpace)]
    [InlineData(StorageFailureKind.IntegrityMismatch, StorageTestCodes.IntegrityMismatch)]
    [InlineData(StorageFailureKind.NotSupported, StorageTestCodes.NotSupported)]
    [InlineData(StorageFailureKind.Io, StorageTestCodes.IoError)]
    [InlineData(StorageFailureKind.NotFound, StorageTestCodes.IoError)]
    [InlineData(StorageFailureKind.AlreadyExists, StorageTestCodes.IoError)]
    [InlineData(StorageFailureKind.Unexpected, StorageTestCodes.Unexpected)]
    public async Task StorageFailuresMapToStableCodesAndTheSafeMessageIsKept(StorageFailureKind kind, string expectedCode)
    {
        var provider = new ScriptedProvider([StorageCapabilityIds.Delete]) { OnPut = _ => throw new StorageException(kind, "safe message") };

        var result = await Run(provider);

        Assert.Equal(StorageTestStatus.Failed, result.Status);
        Assert.Equal(expectedCode, result.Code);
        Assert.Equal("safe message", result.Message);
    }

    [Fact]
    public async Task CancellationIsNotReportedAsAFailedTest()
    {
        var provider = new ScriptedProvider([StorageCapabilityIds.Delete]) { OnPut = _ => throw new OperationCanceledException() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(provider));
    }

    [Fact]
    public async Task ANullConnectionIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => new CentralConnectionTester(new FixedResolver(null)).TestAsync(null!, _secrets, CancellationToken.None));
    }

    // ---- agent side --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("local")]
    [InlineData("other-agent-storage")]
    public async Task AgentSideStorageIsNotTestableAndNeverSucceeds(string providerType)
    {
        var tester = new AgentSideConnectionTester(providerType);
        var connection = new StorageConnection(Guid.NewGuid(), _tenant, "Local", providerType, StorageOwnership.TenantManaged, null, null, true, Now);

        var result = await tester.TestAsync(connection, _secrets, CancellationToken.None);

        Assert.Equal(providerType, tester.ProviderType);
        Assert.Equal(StorageTestStatus.NotTestable, result.Status);
        Assert.Equal(StorageTestCodes.AgentSideStorage, result.Code);
        Assert.Equal(StorageTestMessages.For(StorageTestCodes.AgentSideStorage), result.Message);
    }

}
