using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.Storage;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.UnitTests.Backup;

namespace BackupPlatform.UnitTests.Storage.Connections;

internal sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>An ordered log shared by the fakes, so a test can prove which side effect happened before which.</summary>
internal sealed class EventLog
{
    public List<string> Entries { get; } = [];

    public void Add(string entry) => Entries.Add(entry);
}

internal sealed record CapturedAudit(Guid ConnectionId, TenantId Tenant, string Action, IReadOnlyDictionary<string, object?> Details, AuditActorKind ActorKind, bool AffectsQuota);

internal sealed class InMemorySecretStore(EventLog? log = null) : ISecretStore
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public bool ThrowOnDelete { get; set; }

    public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken cancellationToken) =>
        Values.TryGetValue(reference.Name, out var value) ? new(new SecretValue(value)) : throw new SecretUnavailableException();

    public Task SetAsync(SecretReference reference, SecretValue value, CancellationToken cancellationToken)
    {
        Values[reference.Name] = value.Reveal();
        log?.Add($"secret.set:{reference.Name}");
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        if (ThrowOnDelete) throw new InvalidOperationException("delete failed");
        log?.Add($"secret.delete:{reference.Name}");
        return Task.FromResult(Values.Remove(reference.Name));
    }

    public Task<bool> ExistsAsync(SecretReference reference, CancellationToken cancellationToken) => Task.FromResult(Values.ContainsKey(reference.Name));
}

/// <summary>
/// A faithful in-memory store: case-insensitive name uniqueness per tenant, per-provider limit, tenant-scoped lookups, audit captured next to the
/// change, and the mutation result handled the way the real store handles it (Rejected / Unchanged / Changed).
/// </summary>
internal sealed class InMemoryStorageConnectionStore(EventLog? log = null) : IStorageConnectionStore
{
    private readonly Dictionary<Guid, StorageConnection> _connections = [];

    public Dictionary<TenantId, string> TenantNames { get; } = [];

    public List<CapturedAudit> Audit { get; } = [];

    public HashSet<Guid> InUse { get; } = [];

    /// <summary>Throws from <see cref="MutateAsync"/> before applying anything.</summary>
    public bool ThrowOnMutate { get; set; }

    public bool ThrowOnCreate { get; set; }

    /// <summary>Makes <see cref="MutateAsync"/> report this outcome without applying the mutation (a concurrent delete, a lost race).</summary>
    public StorageConnectionOutcome? MutateOutcomeOverride { get; set; }

    public IReadOnlyCollection<StorageConnection> All => _connections.Values;

    public void Add(StorageConnection connection) => _connections[connection.Id] = connection;

    public Task<IReadOnlyList<StorageConnectionRecord>?> ListAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        IReadOnlyList<StorageConnectionRecord>? list = TenantNames.Count > 0 && !TenantNames.ContainsKey(tenantId)
            ? null
            : [.. _connections.Values.Where(item => item.TenantId == tenantId).OrderBy(item => item.Name, StringComparer.Ordinal).Select(Record)];
        return Task.FromResult(list);
    }

    public Task<PagedResult<StorageConnectionRecord>> SearchAsync(StorageConnectionFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var matches = _connections.Values
            .Where(item => filter.Ownership is null || item.Ownership == filter.Ownership)
            .Where(item => filter.ProviderType is null || item.ProviderType == filter.ProviderType)
            .Where(item => filter.TenantId is null || item.TenantId == filter.TenantId)
            .Where(item => filter.Enabled is null || item.Enabled == filter.Enabled)
            .OrderBy(item => item.Name, StringComparer.Ordinal).ToList();
        return Task.FromResult(new PagedResult<StorageConnectionRecord>([.. matches.Skip((page - 1) * pageSize).Take(pageSize).Select(Record)], matches.Count, page, pageSize));
    }

    public Task<StorageConnectionRecord?> FindAsync(Guid id, TenantId? tenantScope, CancellationToken cancellationToken) =>
        Task.FromResult(Locate(id, tenantScope) is { } connection ? Record(connection) : null);

    public Task<StorageConnectionResult> CreateAsync(
        StorageConnection connection, int? maxPerTenant, StorageActor actor, IReadOnlyDictionary<string, object?> auditDetails, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (ThrowOnCreate) throw new InvalidOperationException("database unavailable");
        var siblings = _connections.Values.Where(item => item.TenantId == connection.TenantId).ToList();
        if (siblings.Any(item => item.NormalizedName == connection.NormalizedName))
        {
            return Task.FromResult(new StorageConnectionResult(StorageConnectionOutcome.NameTaken));
        }
        if (maxPerTenant is { } max && siblings.Count(item => item.ProviderType == connection.ProviderType) >= max)
        {
            return Task.FromResult(new StorageConnectionResult(StorageConnectionOutcome.LimitReached));
        }
        _connections[connection.Id] = connection;
        log?.Add("store.create");
        Audit.Add(new CapturedAudit(connection.Id, connection.TenantId, StorageConnectionAuditActions.Created, auditDetails, actor.Kind, false));
        return Task.FromResult(new StorageConnectionResult(StorageConnectionOutcome.Changed, Record(connection)));
    }

    public Task<StorageConnectionResult> MutateAsync(Guid id, TenantId? tenantScope, StorageMutation mutation, StorageActor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (ThrowOnMutate) throw new InvalidOperationException("database unavailable");
        if (Locate(id, tenantScope) is not { } connection)
        {
            return Task.FromResult(new StorageConnectionResult(StorageConnectionOutcome.NotFound));
        }
        if (MutateOutcomeOverride is { } forced)
        {
            return Task.FromResult(new StorageConnectionResult(forced, Code: forced == StorageConnectionOutcome.Rejected ? "concurrent_rejection" : null));
        }
        // Evaluate on an isolated entity so a rejected write never changes the stored entity.
        var candidate = Copy(connection);
        var result = mutation(candidate, now);
        if (result.RejectionCode is not null)
        {
            return Task.FromResult(new StorageConnectionResult(StorageConnectionOutcome.Rejected, Record(connection), result.RejectionCode));
        }
        if (!result.Changed)
        {
            return Task.FromResult(new StorageConnectionResult(StorageConnectionOutcome.Unchanged, Record(connection)));
        }
        if (_connections.Values.Any(item => item.Id != id && item.TenantId == connection.TenantId && item.NormalizedName == candidate.NormalizedName))
        {
            return Task.FromResult(new StorageConnectionResult(StorageConnectionOutcome.NameTaken));
        }
        result = mutation(connection, now);
        log?.Add("store.mutate");
        foreach (var entry in result.Audit ?? [])
        {
            Audit.Add(new CapturedAudit(connection.Id, connection.TenantId, entry.Action, entry.Details, actor.Kind, result.AffectsQuota));
        }
        return Task.FromResult(new StorageConnectionResult(StorageConnectionOutcome.Changed, Record(connection)));
    }

    public Task<StorageConnectionResult> DeleteAsync(Guid id, TenantId? tenantScope, StorageActor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (Locate(id, tenantScope) is not { } connection)
        {
            return Task.FromResult(new StorageConnectionResult(StorageConnectionOutcome.NotFound));
        }
        if (InUse.Contains(id))
        {
            return Task.FromResult(new StorageConnectionResult(StorageConnectionOutcome.InUse, Record(connection)));
        }
        _connections.Remove(id);
        Audit.Add(new CapturedAudit(id, connection.TenantId, StorageConnectionAuditActions.Deleted, new Dictionary<string, object?>(), actor.Kind, false));
        return Task.FromResult(new StorageConnectionResult(StorageConnectionOutcome.Changed, Record(connection)));
    }

    private StorageConnection? Locate(Guid id, TenantId? tenantScope) =>
        _connections.TryGetValue(id, out var connection) && (tenantScope is null || connection.TenantId == tenantScope) ? connection : null;

    private StorageConnectionRecord Record(StorageConnection connection) =>
        new(connection, TenantNames.GetValueOrDefault(connection.TenantId), 0, 0);

    private static StorageConnection Copy(StorageConnection source)
    {
        var copy = new StorageConnection(source.Id, source.TenantId, source.Name, source.ProviderType, source.Ownership,
            source.QuotaScope, source.Configuration, source.IsDefault, source.CreatedAt);
        if (source.SecretReference is { } reference)
        {
            copy.AttachSecret(reference, source.SecretFieldNames, source.SecretUpdatedAt!.Value);
        }
        if (!source.Enabled) copy.SetEnabled(false, source.StateChangedAt!.Value);
        if (source.LastTestStatus is { } status) copy.RecordTest(status, source.LastTestCode, source.LastTestedAt!.Value);
        return copy;
    }
}

internal sealed class ScriptedTester(string providerType, Func<StorageConnection, ISecretProvider, Task<StorageTestResult>> test) : IStorageConnectionTester
{
    public string ProviderType { get; } = providerType;

    public int Calls { get; private set; }

    public Task<StorageTestResult> TestAsync(StorageConnection connection, ISecretProvider secrets, CancellationToken cancellationToken)
    {
        Calls++;
        return test(connection, secrets);
    }
}

/// <summary>Custom provider descriptors the service tests run against; nothing here is a built-in provider.</summary>
internal static class TestProviders
{
    public const string Fake = "fake";
    public const string PlatformOnly = "platform-only";
    public const string Planned = "planned-x";
    public const string Plain = "plain";

    public static StorageProviderDescriptor FakeDescriptor { get; } = new(
        Fake, "Fake provider", StorageProviderAvailability.Available, StorageTopology.AgentDirect, null, ["storage.read"], [StorageOwnership.TenantManaged],
        tenantCreatable: true, platformCreatable: false, maxConnectionsPerTenant: 2,
        [
            new StorageFieldDefinition("host", "Host", StorageFieldKind.Text, Required: true, MaxLength: 20),
            new StorageFieldDefinition("port", "Port", StorageFieldKind.Number, Required: false),
            new StorageFieldDefinition("useTls", "TLS", StorageFieldKind.Boolean, Required: false)
        ],
        [
            new StorageSecretFieldDefinition("password", "Password", Required: true),
            new StorageSecretFieldDefinition("token", "Token", Required: false)
        ],
        StorageTestMode.BackendRoundTrip);

    public static StorageProviderDescriptor PlatformOnlyDescriptor { get; } = new(
        PlatformOnly, "Platform only", StorageProviderAvailability.Available, StorageTopology.AgentDirect, null, ["storage.read"], [StorageOwnership.PlatformManaged],
        tenantCreatable: false, platformCreatable: true, maxConnectionsPerTenant: null, [],
        [new StorageSecretFieldDefinition("apiKey", "API key", Required: true)], StorageTestMode.BackendRoundTrip);

    public static StorageProviderDescriptor PlannedDescriptor { get; } = new(
        Planned, "Planned", StorageProviderAvailability.Planned, null, null, [], [StorageOwnership.TenantManaged],
        tenantCreatable: true, platformCreatable: true, maxConnectionsPerTenant: null, [], [], StorageTestMode.BackendRoundTrip);

    public static StorageProviderDescriptor PlainDescriptor { get; } = new(
        Plain, "Plain", StorageProviderAvailability.Available, StorageTopology.AgentDirect, null, [], [StorageOwnership.TenantManaged],
        tenantCreatable: true, platformCreatable: false, maxConnectionsPerTenant: null, [], [], StorageTestMode.AgentSide);

    public static StorageProviderCatalog Catalog() => new([FakeDescriptor, PlatformOnlyDescriptor, PlannedDescriptor, PlainDescriptor, BuiltInStorageProviders.Central]);
}

/// <summary>Everything a service test needs, wired with the fakes.</summary>
internal sealed class ServiceHarness
{
    public static readonly DateTimeOffset Start = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    public ServiceHarness(params IStorageConnectionTester[] testers)
    {
        Store = new InMemoryStorageConnectionStore(Log);
        Secrets = new InMemorySecretStore(Log);
        Clock = new FakeClock(Start);
        Service = new StorageConnectionService(
            Store, TestProviders.Catalog(), Secrets, testers, Clock, Logger);
    }

    public EventLog Log { get; } = new();
    public RecordingLogger<StorageConnectionService> Logger { get; } = new();
    public InMemoryStorageConnectionStore Store { get; }
    public InMemorySecretStore Secrets { get; }
    public FakeClock Clock { get; }
    public StorageConnectionService Service { get; }

    public TenantId Tenant { get; } = TenantId.New();
    public TenantId OtherTenant { get; } = TenantId.New();
    public UserId Actor { get; } = UserId.New();

    public StorageScope TenantScope(bool canManage = true) => StorageScope.Tenant(Tenant, canManage);

    public StorageScope OtherTenantScope => StorageScope.Tenant(OtherTenant, true);

    public StorageConnection Seed(
        string providerType, StorageOwnership ownership, string name = "Seeded", TenantId? tenant = null, string? quotaScope = null,
        IReadOnlyDictionary<string, string>? configuration = null)
    {
        var connection = new StorageConnection(Guid.NewGuid(), tenant ?? Tenant, name, providerType, ownership, quotaScope, configuration, isDefault: false, Start);
        Store.Add(connection);
        return connection;
    }

    /// <summary>Creates a fake-provider connection with a secret as the tenant and returns the stored entity.</summary>
    public async Task<StorageConnection> CreateFakeAsync(string name = "Archive", string password = "pw-initial")
    {
        var result = await Service.CreateAsync(
            TenantScope(), Tenant, new CreateStorageConnectionCommand(TestProviders.Fake, name, null, null,
                new Dictionary<string, string> { ["host"] = "backup.example" },
                new Dictionary<string, string> { ["password"] = password }), Actor, CancellationToken.None);
        Assert.Equal(StorageConnectionOutcome.Changed, result.Outcome);
        Log.Entries.Clear();
        Store.Audit.Clear();
        return Store.All.Single(item => item.Id == result.Value!.Id);
    }
}
