using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.Local;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BackupPlatform.IntegrationTests;

/// <summary>A backup of a fresh tenant, agent and job, with the agent that produced it so more can be added.</summary>
internal sealed record SeededWorld(StoreAgent Agent, BackupJob Job, SeededBackup Backup);

/// <summary>
/// Everything the retention service and sweeper tests share: an isolated database (so a cycle only ever sees this test's backups), real stores
/// that behave like separate backend processes, a real Local storage on a temp directory behind counting decorators, and seeding through the
/// real store. Every instant is explicit; nothing sleeps.
/// </summary>
internal sealed class RetentionServiceWorld : IDisposable
{
    /// <summary>When the seeded backups were accepted by the backend.</summary>
    public static readonly DateTimeOffset Accepted = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private const int MaxStoreConnections = 16;
    private readonly List<string> _directories = [];

    private RetentionServiceWorld(BackupStoreHarness harness)
    {
        Harness = harness;
        Root = NewDirectory();
        Local = new LocalStorageProvider(new LocalStorageOptions(Root));
        Storage = new CountingStorage(Local);
        Resolver = new TestStorageResolver();
        Resolver.Add(StorageProviderId.Local, "local", null, Storage);
    }

    public BackupStoreHarness Harness { get; }
    public string Root { get; }
    public LocalStorageProvider Local { get; }
    public CountingStorage Storage { get; }
    public TestStorageResolver Resolver { get; }

    /// <summary>Runs <paramref name="test"/> in a database nobody else uses, with its own temp directories, removed afterwards.</summary>
    public static Task RunAsync(Func<RetentionServiceWorld, Task> test) => BackupStoreHarness.WithIsolatedDatabaseAsync(async harness =>
    {
        using var world = new RetentionServiceWorld(harness);
        await test(world);
    });

    public DbContextOptions<PlatformDbContext> DbOptions() => new DbContextOptionsBuilder<PlatformDbContext>()
        .UseNpgsql(new NpgsqlConnectionStringBuilder(Harness.Database.ConnectionString) { MaxPoolSize = MaxStoreConnections }.ConnectionString).Options;

    /// <summary>A store with its own options and pool: what another backend node would hold.</summary>
    public PostgreSqlBackupRetentionStore Store(BackupRetentionOptions? options = null) => new(DbOptions(), options);

    public BackupRetentionService Service(string node, TimeProvider clock, IBackupRetentionStore? store = null, BackupRetentionOptions? options = null) =>
        new(store ?? Store(options), Resolver, options ?? new BackupRetentionOptions(), new BackupNodeIdentity(node), clock, NullLogger<BackupRetentionService>.Instance);

    public BackupStorageSweeper Sweeper(TimeProvider clock, BackupRetentionOptions? options = null) =>
        new(Store(), Resolver, options ?? new BackupRetentionOptions(), clock, NullLogger<BackupStorageSweeper>.Instance);

    public BackupArtifactService Artifacts(TimeProvider clock, BackupRetentionOptions? options = null) =>
        new(Store(), new PostgreSqlBackupScheduleStore(DbOptions()), options ?? new BackupRetentionOptions(), clock);

    public static void SetClock(MutableClock clock, DateTimeOffset at) => clock.Advance(at - clock.GetUtcNow());

    // ---- seeding ----

    /// <summary>A backup of a new job (default policy unless given) accepted at <paramref name="acceptedAt"/>, its file on the Local storage.</summary>
    public async Task<SeededWorld> SeedAsync(BackupRetentionPolicy? policy = null, DateTimeOffset? acceptedAt = null, bool writeFile = true)
    {
        var agent = await Harness.AddAgentAsync();
        var job = await RetentionKit.AddJobAsync(Harness, agent, policy);
        var backup = await RetentionKit.SeedAsync(Harness, agent, job, acceptedAt ?? Accepted);
        if (writeFile) Place(backup.Key);
        return new SeededWorld(agent, job, backup);
    }

    /// <summary>One more backup of the same job, accepted at <paramref name="acceptedAt"/>.</summary>
    public async Task<SeededBackup> SeedMoreAsync(SeededWorld world, DateTimeOffset acceptedAt, bool writeFile = true)
    {
        var backup = await RetentionKit.SeedAsync(Harness, world.Agent, world.Job, acceptedAt);
        if (writeFile) Place(backup.Key);
        return backup;
    }

    /// <summary>A run of a new job of <paramref name="agent"/> whose only attempt ended Failed with <paramref name="code"/> (a retryable code leaves the run RetryPending, a non-retryable one ends it).</summary>
    public async Task<SeededAttempt> SeedFailedAttemptAsync(StoreAgent agent, string code, DateTimeOffset at)
    {
        var job = await RetentionKit.AddJobAsync(Harness, agent);
        var store = Harness.Store();
        var run = await Harness.StartRunAsync(store, job, Guid.NewGuid().ToString("N"), at);
        var claim = await BackupStoreHarness.ClaimAsync(store, agent, at.AddSeconds(1)) ?? throw new InvalidOperationException("The agent could not claim the run.");
        await BackupStoreHarness.StartAttemptAsync(store, claim, at.AddSeconds(2));
        await BackupStoreHarness.CompleteAsync(store, claim, BackupStoreHarness.Failed(code), at.AddSeconds(3));
        return new SeededAttempt(agent.TenantId, job.Id, run.Id, claim.Attempt.Id);
    }

    // ---- real files ----

    public string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bp_retention_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _directories.Add(path);
        return path;
    }

    public string PathOf(string key, string? root = null) => Path.Combine(root ?? Root, key.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Writes a real file under <paramref name="root"/> (the Local root by default) and returns its path.</summary>
    public string Place(string key, string? root = null, DateTimeOffset? modified = null, byte[]? content = null)
    {
        var path = PathOf(key, root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content ?? [1, 2, 3, 4]);
        if (modified is { } at) File.SetLastWriteTimeUtc(path, at.UtcDateTime);
        return path;
    }

    public bool Exists(string key, string? root = null) => File.Exists(PathOf(key, root));

    public void Backdate(string key, DateTimeOffset modified, string? root = null) => File.SetLastWriteTimeUtc(PathOf(key, root), modified.UtcDateTime);

    // ---- rows, read without going through the code under test ----

    public async Task<BackupReplica> ReplicaAsync(Guid replicaId)
    {
        await using var context = Harness.Database.CreatePlatformContext();
        return await context.BackupReplicas.AsNoTracking().SingleAsync(replica => replica.Id == replicaId);
    }

    public async Task<BackupArtifact> ArtifactAsync(Guid artifactId)
    {
        await using var context = Harness.Database.CreatePlatformContext();
        return await context.BackupArtifacts.AsNoTracking().Include(artifact => artifact.Replicas).SingleAsync(artifact => artifact.Id == artifactId);
    }

    public void Dispose()
    {
        foreach (var path in Enumerable.Reverse(_directories))
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>The identity of an attempt that exists in the database.</summary>
internal sealed record SeededAttempt(TenantId TenantId, Guid JobId, Guid RunId, Guid AttemptId)
{
    public string Key(string name = RetentionKit.ManagedFileName, string? prefix = null) =>
        (prefix is null ? "" : prefix + "/") + $"{BackupPlatform.Contracts.Backups.ManagedBackupLayout.AttemptScope(TenantId.Value, JobId, RunId, AttemptId)}/{name}";
}

/// <summary>Maps (provider, target) to the storages the test configured; anything else is unreachable, exactly like an unconfigured operator.</summary>
internal sealed class TestStorageResolver : IBackupStorageResolver
{
    private readonly List<(StorageProviderId Provider, string Target, TenantId? Scope, IStorageProvider Storage, string Name)> _entries = [];

    public void Add(StorageProviderId provider, string target, TenantId? scope, IStorageProvider storage, string? name = null) =>
        _entries.Add((provider, target, scope, storage, name ?? $"{provider.Value}:{target}"));

    public IStorageProvider? Resolve(TenantId tenantId, string storageProvider, string storageTarget) =>
        _entries.FirstOrDefault(entry => entry.Provider.Value == storageProvider && entry.Target == storageTarget && (entry.Scope is null || entry.Scope == tenantId)).Storage;

    public IReadOnlyList<BackupManagedStorage> ManagedStorages =>
        [.. _entries.Select(entry => new BackupManagedStorage(entry.Name, entry.Provider, entry.Scope, entry.Storage))];
}

/// <summary>Wraps a real provider to count what reaches it and to inject a failure or a reaction at the moment of a delete.</summary>
internal sealed class CountingStorage(IStorageProvider inner) : IStorageProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _deleteCalls = [];
    private readonly Dictionary<string, int> _deletions = [];
    private readonly Queue<Exception> _faults = new();

    /// <summary>Runs after every delete that returned, with the key.</summary>
    public Action<string>? AfterDelete { get; set; }

    /// <summary>The next delete call throws this (one fault per call, in order).</summary>
    public void FailNextDelete(Exception fault)
    {
        lock (_gate) _faults.Enqueue(fault);
    }

    public int DeleteCalls(string key)
    {
        lock (_gate) return _deleteCalls.GetValueOrDefault(key);
    }

    /// <summary>Delete calls that found and removed the object.</summary>
    public int Deletions(string key)
    {
        lock (_gate) return _deletions.GetValueOrDefault(key);
    }

    public int TotalDeleteCalls
    {
        get
        {
            lock (_gate) return _deleteCalls.Values.Sum();
        }
    }

    public StorageProviderId Id => inner.Id;
    public StorageTopology Topology => inner.Topology;
    public StorageCapabilities Capabilities => inner.Capabilities;

    public Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken) =>
        inner.PutAsync(key, content, options, progress, cancellationToken);

    public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken) => inner.ExistsAsync(key, cancellationToken);

    public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken) => inner.GetMetadataAsync(key, cancellationToken);

    public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken) =>
        inner.VerifyAsync(key, expected, cancellationToken);

    public async Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        Exception? fault = null;
        lock (_gate)
        {
            _deleteCalls[key.Value] = _deleteCalls.GetValueOrDefault(key.Value) + 1;
            if (_faults.Count > 0) fault = _faults.Dequeue();
        }

        if (fault is not null) throw fault;
        var existed = await inner.DeleteAsync(key, cancellationToken);
        if (existed)
        {
            lock (_gate) _deletions[key.Value] = _deletions.GetValueOrDefault(key.Value) + 1;
        }

        AfterDelete?.Invoke(key.Value);
        return existed;
    }

    public IAsyncEnumerable<StorageEntry> EnumerateAsync(StorageObjectKey? prefix, DateTimeOffset modifiedBefore, CancellationToken cancellationToken) =>
        inner.EnumerateAsync(prefix, modifiedBefore, cancellationToken);
}

/// <summary>Wraps the real store to make the commit of a finished delete fail, as a database outage between the physical delete and its record would.</summary>
internal sealed class FlakyRetentionStore(IBackupRetentionStore inner) : IBackupRetentionStore
{
    private int _completeFailures;

    public int CompleteCalls { get; private set; }

    /// <summary>The next <paramref name="count"/> completions throw instead of reaching the database.</summary>
    public void FailNextCompletions(int count) => _completeFailures = count;

    public Task<BackupRetentionApplication> ApplyExpiredRetentionAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        inner.ApplyExpiredRetentionAsync(now, limit, cancellationToken);

    public Task<IReadOnlyList<ClaimedReplicaDeletion>> ClaimDueDeletionsAsync(string owner, DateTimeOffset now, TimeSpan lease, int limit, CancellationToken cancellationToken) =>
        inner.ClaimDueDeletionsAsync(owner, now, lease, limit, cancellationToken);

    public Task<bool> CompleteDeletionAsync(Guid replicaId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        CompleteCalls++;
        if (_completeFailures > 0)
        {
            _completeFailures--;
            throw new InvalidOperationException("The database connection was lost.");
        }

        return inner.CompleteDeletionAsync(replicaId, leaseToken, now, cancellationToken);
    }

    public Task<bool> FailDeletionAsync(Guid replicaId, Guid leaseToken, DateTimeOffset now, string failureCode, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken) =>
        inner.FailDeletionAsync(replicaId, leaseToken, now, failureCode, nextAttemptAt, cancellationToken);

    public Task<BackupArtifactDeleteResult> RequestDeleteAsync(TenantId tenantId, Guid artifactId, Guid? requestedByUserId, DateTimeOffset now, CancellationToken cancellationToken) =>
        inner.RequestDeleteAsync(tenantId, artifactId, requestedByUserId, now, cancellationToken);

    public Task<BackupArtifactCancelResult> CancelDeleteAsync(TenantId tenantId, Guid artifactId, DateTimeOffset now, CancellationToken cancellationToken) =>
        inner.CancelDeleteAsync(tenantId, artifactId, now, cancellationToken);

    public Task<BackupArtifactRecord?> GetArtifactAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken) =>
        inner.GetArtifactAsync(tenantId, artifactId, cancellationToken);

    public Task<IReadOnlyList<BackupArtifactRecord>> ListArtifactsAsync(TenantId tenantId, Guid? jobId, int limit, CancellationToken cancellationToken) =>
        inner.ListArtifactsAsync(tenantId, jobId, limit, cancellationToken);

    public Task<BackupArtifactPage<BackupArtifactRecord>> QueryArtifactsAsync(TenantId tenantId, BackupArtifactQuery query, CancellationToken cancellationToken) =>
        inner.QueryArtifactsAsync(tenantId, query, cancellationToken);

    public Task<IReadOnlySet<string>> FindReferencedKeysAsync(string storageProvider, IReadOnlyCollection<string> keys, CancellationToken cancellationToken) =>
        inner.FindReferencedKeysAsync(storageProvider, keys, cancellationToken);

    public Task<IReadOnlyDictionary<Guid, BackupAttemptActivity>> GetAttemptActivityAsync(IReadOnlyCollection<Guid> attemptIds, CancellationToken cancellationToken) =>
        inner.GetAttemptActivityAsync(attemptIds, cancellationToken);

    public Task<int> PruneScheduleOccurrencesAsync(DateTimeOffset decidedBefore, int limit, CancellationToken cancellationToken) =>
        inner.PruneScheduleOccurrencesAsync(decidedBefore, limit, cancellationToken);
}
