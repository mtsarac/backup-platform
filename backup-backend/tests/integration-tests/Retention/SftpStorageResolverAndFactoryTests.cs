using BackupPlatform.Application.Platform;
using System.Text.Json;
using BackupPlatform.Api.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.Storage;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Sftp;
using Microsoft.Extensions.DependencyInjection;

namespace BackupPlatform.IntegrationTests;

public sealed class SftpStorageResolverAndFactoryTests
{
    private static readonly TenantId TenantA = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static readonly TenantId TenantB = new(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
    private static readonly SftpNetworkOptions Network = new()
    {
        AllowedPrivateNetworks = ["127.0.0.1/32", "::1/128"],
        ConnectionTimeout = TimeSpan.FromSeconds(1),
        OperationTimeout = TimeSpan.FromSeconds(2),
        TransferTimeout = TimeSpan.FromSeconds(5)
    };

    [Fact]
    public void FactoryCreatesLazyProviderAndThrowsSafeExceptionOnCorruptedConfiguration()
    {
        var factory = new SftpConnectionProviderFactory(Network);
        var secrets = new FakeSecretProvider();
        var validConnection = CreateConnection(TenantA, Guid.NewGuid(), "password", enabled: true);

        var provider = factory.Create(validConnection, secrets);
        Assert.NotNull(provider);
        Assert.Equal(StorageProviderTypes.Sftp, provider.Id.Value);

        var brokenConnection = CreateConnection(TenantA, Guid.NewGuid(), "password", enabled: true, host: "");
        var exception = Assert.Throws<StorageException>(() => factory.Create(brokenConnection, secrets));
        Assert.Equal(StorageFailureKind.InvalidConfiguration, exception.Kind);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolverEnforcesTenantIsolationForPersistedSftpConnections()
    {
        var connectionIdA = Guid.NewGuid();
        var connectionA = CreateConnection(TenantA, connectionIdA, "password", enabled: true);
        var store = new FakeStorageConnectionStore([connectionA]);
        var secrets = new FakeSecretProvider(new Dictionary<string, string>
        {
            [connectionA.SecretReference!] = JsonSerializer.Serialize(new Dictionary<string, string> { ["password"] = "secret-pass" })
        });
        var resolver = BuildResolver(store, secrets);

        var resolvedForA = resolver.Resolve(TenantA, StorageProviderTypes.Sftp, connectionIdA.ToString());
        Assert.NotNull(resolvedForA);

        var resolvedForB = resolver.Resolve(TenantB, StorageProviderTypes.Sftp, connectionIdA.ToString());
        Assert.NotNull(resolvedForB);

        var key = new StorageObjectKey("tenants/test/backup.bin");
        var existsException = await Assert.ThrowsAsync<StorageException>(() => resolvedForB.ExistsAsync(key, CancellationToken.None));
        Assert.Equal(StorageFailureKind.InvalidConfiguration, existsException.Kind);
    }

    [Fact]
    public async Task DisabledConnectionRemainsReadableAndDeletableForReplicas()
    {
        var connectionId = Guid.NewGuid();
        var disabledConnection = CreateConnection(TenantA, connectionId, "password", enabled: false);
        var store = new FakeStorageConnectionStore([disabledConnection]);
        var secrets = new FakeSecretProvider(new Dictionary<string, string>
        {
            [disabledConnection.SecretReference!] = JsonSerializer.Serialize(new Dictionary<string, string> { ["password"] = "secret-pass" })
        });
        var resolver = BuildResolver(store, secrets);

        var provider = resolver.Resolve(TenantA, StorageProviderTypes.Sftp, connectionId.ToString());
        Assert.NotNull(provider);

        var key = new StorageObjectKey("tenants/test/backup.bin");
        var exception = await Assert.ThrowsAsync<StorageException>(() => provider.ExistsAsync(key, CancellationToken.None));
        // Passes connection & secret lookup without disabled rejection; hits network refusal since there is no running SFTP server.
        Assert.NotEqual(StorageFailureKind.InvalidConfiguration, exception.Kind);
    }

    private static ConfiguredBackupStorageResolver BuildResolver(IStorageConnectionStore store, ISecretProvider secrets)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(secrets);
        var provider = services.BuildServiceProvider();
        var factory = new SftpConnectionProviderFactory(Network);
        return new ConfiguredBackupStorageResolver(new BackupStorageOptions(), provider.GetRequiredService<IServiceScopeFactory>(), factory);
    }

    private static StorageConnection CreateConnection(
        TenantId tenantId, Guid connectionId, string authType, bool enabled, string host = "127.0.0.1")
    {
        var configuration = new Dictionary<string, string>
        {
            ["host"] = host,
            ["port"] = "22",
            ["username"] = "backupuser",
            ["authenticationType"] = authType,
            ["remoteBaseDirectory"] = "/backups",
            ["hostKeyFingerprint"] = "SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU"
        };
        var connection = new StorageConnection(
            connectionId, tenantId, "Test SFTP", StorageProviderTypes.Sftp, StorageOwnership.TenantManaged,
            StorageQuotaScopes.Tenant, configuration, isDefault: false, DateTimeOffset.UtcNow);
        connection.AttachSecret($"sc-{connectionId:N}-1234567890ab", [authType == "password" ? "password" : "privateKey"], DateTimeOffset.UtcNow);
        if (!enabled)
        {
            connection.SetEnabled(false, DateTimeOffset.UtcNow);
        }
        return connection;
    }

    private sealed class FakeSecretProvider(Dictionary<string, string>? values = null) : ISecretProvider
    {
        private readonly Dictionary<string, string> _values = values ?? [];

        public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)
        {
            if (_values.TryGetValue(reference.Name, out var value))
            {
                return ValueTask.FromResult(new SecretValue(value));
            }
            throw new KeyNotFoundException($"Secret {reference.Name} was not found.");
        }
    }

    private sealed class FakeStorageConnectionStore(IEnumerable<StorageConnection> connections) : IStorageConnectionStore
    {
        private readonly List<StorageConnection> _connections = [.. connections];

        public Task<StorageConnectionRecord?> FindAsync(Guid id, TenantId? tenantScope, CancellationToken cancellationToken)
        {
            var match = _connections.FirstOrDefault(connection => connection.Id == id && (tenantScope is null || connection.TenantId == tenantScope));
            return Task.FromResult(match is null ? null : new StorageConnectionRecord(match, "Tenant", 0, 0));
        }

        public Task<IReadOnlyList<StorageConnectionRecord>?> ListAsync(TenantId tenantId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<PagedResult<StorageConnectionRecord>> SearchAsync(StorageConnectionFilter filter, int page, int pageSize, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<StorageConnectionResult> CreateAsync(StorageConnection connection, int? maxPerTenant, StorageActor actor, IReadOnlyDictionary<string, object?> auditDetails, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<StorageConnectionResult> MutateAsync(Guid id, TenantId? tenantScope, StorageMutation mutation, StorageActor actor, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<StorageConnectionResult> DeleteAsync(Guid id, TenantId? tenantScope, StorageActor actor, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }
}
