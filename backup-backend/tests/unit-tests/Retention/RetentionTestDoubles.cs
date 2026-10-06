using System.Runtime.CompilerServices;
using BackupPlatform.Application.Backups;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.UnitTests.Retention;

/// <summary>A clock a test moves by hand; nothing in the retention tests ever waits for time to pass.</summary>
internal sealed class SettableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Records the order in which the fakes were called, across store and storage.</summary>
internal sealed class CallJournal
{
    private readonly object _gate = new();
    private readonly List<string> _entries = [];

    public string[] Entries
    {
        get
        {
            lock (_gate) return _entries.ToArray();
        }
    }

    public void Add(string entry)
    {
        lock (_gate) _entries.Add(entry);
    }
}

/// <summary>Everything logged, formatted, so a test can assert what must never appear in it.</summary>
internal sealed class CapturingLog<T> : ILogger<T>
{
    private readonly object _gate = new();
    private readonly List<string> _entries = [];

    public string AllText
    {
        get
        {
            lock (_gate) return string.Join('\n', _entries);
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var text = $"{logLevel}|{formatter(state, exception)}" + (exception is null ? "" : "|" + exception);
        lock (_gate) _entries.Add(text);
    }
}

/// <summary>An in-memory <see cref="IBackupRetentionStore"/>: every answer is scripted by the test, every call is recorded.</summary>
internal sealed class FakeRetentionStore(CallJournal? journal = null) : IBackupRetentionStore
{
    private readonly CallJournal _journal = journal ?? new CallJournal();
    private int _referenceQueries;

    // ---- expiration and deletion ----
    public Queue<BackupRetentionApplication> Applications { get; } = new();
    public List<(DateTimeOffset Now, int Limit)> ApplyCalls { get; } = [];
    public Queue<IReadOnlyList<ClaimedReplicaDeletion>> ClaimBatches { get; } = new();
    public List<(string Owner, DateTimeOffset Now, TimeSpan Lease, int Limit)> ClaimCalls { get; } = [];
    public Func<Guid, bool> CompleteResult { get; set; } = _ => true;
    public Func<Guid, Exception?> CompleteFault { get; set; } = _ => null;
    public Func<Guid, bool> FailResult { get; set; } = _ => true;
    public List<(Guid ReplicaId, Guid Token, DateTimeOffset Now)> Completions { get; } = [];
    public List<(Guid ReplicaId, Guid Token, DateTimeOffset Now, string Code, DateTimeOffset NextAttemptAt)> Failures { get; } = [];

    // ---- sweeps ----
    public HashSet<(string Provider, string Key)> References { get; } = [];
    public Dictionary<Guid, BackupAttemptActivity> Attempts { get; } = [];
    public List<(string Provider, string[] Keys)> ReferenceQueries { get; } = [];
    public List<Guid[]> ActivityQueries { get; } = [];

    /// <summary>Runs after each reference query is answered (1-based number of the query): lets a test make a reference appear mid-sweep.</summary>
    public Action<int>? AfterReferenceQuery { get; set; }

    public Queue<int> PruneResults { get; } = new();
    public List<(DateTimeOffset DecidedBefore, int Limit)> PruneCalls { get; } = [];

    // ---- artifacts ----
    public Func<TenantId, Guid, DateTimeOffset, BackupArtifactDeleteResult> OnRequestDelete { get; set; } = (_, _, _) => throw new NotSupportedException();
    public Func<TenantId, Guid, DateTimeOffset, BackupArtifactCancelResult> OnCancelDelete { get; set; } = (_, _, _) => throw new NotSupportedException();
    public Func<TenantId, Guid, BackupArtifactRecord?> OnGetArtifact { get; set; } = (_, _) => throw new NotSupportedException();
    public Func<TenantId, Guid?, int, IReadOnlyList<BackupArtifactRecord>> OnListArtifacts { get; set; } = (_, _, _) => throw new NotSupportedException();
    public List<(TenantId Tenant, Guid ArtifactId, Guid? User, DateTimeOffset Now)> DeleteRequests { get; } = [];
    public List<(TenantId Tenant, Guid? JobId, int Limit)> ListCalls { get; } = [];

    public Task<BackupRetentionApplication> ApplyExpiredRetentionAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        ApplyCalls.Add((now, limit));
        _journal.Add("apply");
        return Task.FromResult(Applications.Count > 0 ? Applications.Dequeue() : new BackupRetentionApplication(0, 0));
    }

    public Task<IReadOnlyList<ClaimedReplicaDeletion>> ClaimDueDeletionsAsync(string owner, DateTimeOffset now, TimeSpan lease, int limit, CancellationToken cancellationToken)
    {
        ClaimCalls.Add((owner, now, lease, limit));
        _journal.Add("claim");
        return Task.FromResult(ClaimBatches.Count > 0 ? ClaimBatches.Dequeue() : Array.Empty<ClaimedReplicaDeletion>());
    }

    public Task<bool> CompleteDeletionAsync(Guid replicaId, Guid leaseToken, DateTimeOffset now, CancellationToken cancellationToken)
    {
        _journal.Add($"complete:{replicaId}");
        if (CompleteFault(replicaId) is { } fault) throw fault;
        Completions.Add((replicaId, leaseToken, now));
        return Task.FromResult(CompleteResult(replicaId));
    }

    public Task<bool> FailDeletionAsync(Guid replicaId, Guid leaseToken, DateTimeOffset now, string failureCode, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken)
    {
        _journal.Add($"fail:{replicaId}:{failureCode}");
        Failures.Add((replicaId, leaseToken, now, failureCode, nextAttemptAt));
        return Task.FromResult(FailResult(replicaId));
    }

    public Task<BackupArtifactDeleteResult> RequestDeleteAsync(TenantId tenantId, Guid artifactId, Guid? requestedByUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        DeleteRequests.Add((tenantId, artifactId, requestedByUserId, now));
        return Task.FromResult(OnRequestDelete(tenantId, artifactId, now));
    }

    public Task<BackupArtifactCancelResult> CancelDeleteAsync(TenantId tenantId, Guid artifactId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(OnCancelDelete(tenantId, artifactId, now));

    public Task<BackupArtifactRecord?> GetArtifactAsync(TenantId tenantId, Guid artifactId, CancellationToken cancellationToken) =>
        Task.FromResult(OnGetArtifact(tenantId, artifactId));

    public Task<IReadOnlyList<BackupArtifactRecord>> ListArtifactsAsync(TenantId tenantId, Guid? jobId, int limit, CancellationToken cancellationToken)
    {
        ListCalls.Add((tenantId, jobId, limit));
        return Task.FromResult(OnListArtifacts(tenantId, jobId, limit));
    }

    public Task<IReadOnlySet<string>> FindReferencedKeysAsync(string storageProvider, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        ReferenceQueries.Add((storageProvider, [.. keys]));
        IReadOnlySet<string> found = keys.Where(key => References.Contains((storageProvider, key))).ToHashSet();
        AfterReferenceQuery?.Invoke(++_referenceQueries);
        return Task.FromResult(found);
    }

    public Task<IReadOnlyDictionary<Guid, BackupAttemptActivity>> GetAttemptActivityAsync(IReadOnlyCollection<Guid> attemptIds, CancellationToken cancellationToken)
    {
        ActivityQueries.Add([.. attemptIds]);
        IReadOnlyDictionary<Guid, BackupAttemptActivity> found = attemptIds.Where(Attempts.ContainsKey).ToDictionary(id => id, id => Attempts[id]);
        return Task.FromResult(found);
    }

    public Task<int> PruneScheduleOccurrencesAsync(DateTimeOffset decidedBefore, int limit, CancellationToken cancellationToken)
    {
        PruneCalls.Add((decidedBefore, limit));
        return Task.FromResult(PruneResults.Count > 0 ? PruneResults.Dequeue() : 0);
    }
}

/// <summary>An in-memory storage that lists what a test put into it and records every delete and existence check.</summary>
internal sealed class FakeStorage(CallJournal? journal = null) : IStorageProvider
{
    private readonly CallJournal _journal = journal ?? new CallJournal();
    private readonly List<StorageEntry> _entries = [];

    public StorageProviderId Id { get; init; } = StorageProviderId.Local;
    public StorageTopology Topology => StorageTopology.AgentDirect;
    public StorageCapabilities Capabilities { get; set; } = new([StorageCapabilityIds.Delete, StorageCapabilityIds.Enumerate, StorageCapabilityIds.VerifyAbsence]);

    /// <summary>Thrown when a delete of this key is attempted (before anything is removed).</summary>
    public Func<StorageObjectKey, Exception?> DeleteFault { get; set; } = _ => null;

    /// <summary>Runs when a delete of the key starts.</summary>
    public Action<StorageObjectKey>? OnDelete { get; set; }

    /// <summary>Keys the provider claims to delete but keeps (an eventually consistent store).</summary>
    public HashSet<string> KeepAfterDelete { get; } = [];

    /// <summary>Thrown by the enumeration when it is first advanced.</summary>
    public Exception? EnumerateFault { get; set; }

    /// <summary>Throws <see cref="EnumerateFault"/> after this many entries were yielded (null: before the first).</summary>
    public int? FaultAfterEntries { get; set; }

    /// <summary>Whether the listing honours <c>modifiedBefore</c> like a real provider. Off, it also returns young entries, to prove the sweeper checks ages itself.</summary>
    public bool HonourModifiedBefore { get; set; } = true;

    /// <summary>Runs after each entry was yielded (1-based count).</summary>
    public Action<int>? AfterYield { get; set; }

    public List<DateTimeOffset> ModifiedBeforeValues { get; } = [];
    public int EnumerateCalls { get; private set; }

    public IReadOnlyList<StorageEntry> Entries => _entries;

    public bool Has(string key) => _entries.Any(entry => entry.Key.Value == key);

    /// <summary>Removes a file behind the sweeper's back (somebody else deleted it).</summary>
    public void Remove(string key) => _entries.RemoveAll(entry => entry.Key.Value == key);

    public FakeStorage AddObject(string key, DateTimeOffset modified, long size = 10) => Add(new StorageEntry(new StorageObjectKey(key), StorageEntryKind.Object, size, modified, null));

    public FakeStorage AddPartial(string key, DateTimeOffset modified, bool? inUse = false) =>
        Add(new StorageEntry(new StorageObjectKey(key), StorageEntryKind.UnpublishedUpload, 10, modified, inUse));

    public FakeStorage Add(StorageEntry entry)
    {
        _entries.Add(entry);
        return this;
    }

    public Task<StorageObjectMetadata> PutAsync(StorageObjectKey key, Stream content, StoragePutOptions options, IProgress<long>? progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<bool> ExistsAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        _journal.Add($"exists:{key.Value}");
        return Task.FromResult(Has(key.Value));
    }

    public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<StorageVerificationResult> VerifyAsync(StorageObjectKey key, StorageVerificationExpectation expected, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<bool> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        _journal.Add($"delete:{key.Value}");
        OnDelete?.Invoke(key);
        if (DeleteFault(key) is { } fault) throw fault;
        if (KeepAfterDelete.Contains(key.Value)) return Task.FromResult(true);
        return Task.FromResult(_entries.RemoveAll(entry => entry.Key.Value == key.Value) > 0);
    }

    public async IAsyncEnumerable<StorageEntry> EnumerateAsync(
        StorageObjectKey? prefix, DateTimeOffset modifiedBefore, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnumerateCalls++;
        ModifiedBeforeValues.Add(modifiedBefore);
        await Task.Yield();
        if (EnumerateFault is not null && FaultAfterEntries is null) throw EnumerateFault;
        var yielded = 0;
        foreach (var entry in _entries.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EnumerateFault is not null && FaultAfterEntries == yielded) throw EnumerateFault;
            if (HonourModifiedBefore && entry.LastModifiedUtc >= modifiedBefore) continue;
            yielded++;
            yield return entry;
            AfterYield?.Invoke(yielded);
        }

        if (EnumerateFault is not null && FaultAfterEntries is not null && FaultAfterEntries >= yielded) throw EnumerateFault;
    }
}

/// <summary>A resolver whose answers the test scripts.</summary>
internal sealed class FakeResolver : IBackupStorageResolver
{
    public Func<TenantId, string, string, IStorageProvider?> OnResolve { get; set; } = (_, _, _) => null;
    public List<BackupManagedStorage> Storages { get; } = [];
    public List<(TenantId Tenant, string Provider, string Target)> Resolutions { get; } = [];

    public IReadOnlyList<BackupManagedStorage> ManagedStorages => Storages;

    public IStorageProvider? Resolve(TenantId tenantId, string storageProvider, string storageTarget)
    {
        Resolutions.Add((tenantId, storageProvider, storageTarget));
        return OnResolve(tenantId, storageProvider, storageTarget);
    }

    public static FakeResolver Serving(IStorageProvider provider)
    {
        var resolver = new FakeResolver { OnResolve = (_, _, _) => provider };
        return resolver;
    }
}

internal static class RetentionFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    public const string ManagedName = "Sales_20261005T053015Z_a1b2c3d4_FULL.bak";
    public const string PartialName = ".Sales_20261005T053015Z_a1b2c3d4_FULL.bak.0123456789abcdef0123456789abcdef.partial";

    /// <summary>A managed storage key for the attempt of a run of a job of a tenant.</summary>
    public static string KeyOf(Guid tenant, Guid job, Guid run, Guid attempt, string? name = null, string? prefix = null) =>
        (prefix is null ? "" : prefix + "/") + ManagedBackupLayout.AttemptScope(tenant, job, run, attempt) + "/" + (name ?? ManagedName);

    public static ClaimedReplicaDeletion Claim(string key, int attempt = 1, Guid? replicaId = null, string provider = "local", string target = "default") =>
        new(replicaId ?? Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), provider, target, key, Guid.NewGuid(), attempt);

    public static BackupAttemptActivity Activity(Guid tenant, Guid job, Guid run, Guid attempt, BackupStatus runStatus, BackupStatus? attemptStatus = null) =>
        new(attempt, new TenantId(tenant), job, run, attemptStatus ?? runStatus, runStatus);
}
