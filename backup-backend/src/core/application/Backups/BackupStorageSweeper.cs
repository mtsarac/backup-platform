using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Application.Backups;

/// <summary>What one storage sweep found. Every enumerated entry lands in exactly one of the counters below (or is not counted after the sweep was truncated).</summary>
/// <param name="Examined">Entries the storages returned (they only return entries older than the smaller of the two minimum ages).</param>
/// <param name="Unrecognized">Not provably ours: a name outside the managed layout, another tenant's key, or an attempt the database does not know. Never touched.</param>
/// <param name="KeptReferenced">Objects some replica (of any status, any tenant) still references.</param>
/// <param name="KeptYoung">Younger than the minimum age that applies to their kind.</param>
/// <param name="KeptActive">Files of work that may still be in flight: a run waiting, leased, running or retrying, a live attempt, or an upload a writer holds open.</param>
/// <param name="Failed">Deletes that were attempted and failed; the file stays and the next sweep tries again.</param>
/// <param name="StoragesUnavailable">Storages that could not be enumerated (no capability, or the listing failed).</param>
/// <param name="Truncated">The sweep stopped at <see cref="BackupRetentionOptions.MaxSweepDeletionsPerRun"/> with more to delete; the next sweep continues.</param>
public sealed record StorageSweepReport(
    int Examined, int Unrecognized, int KeptReferenced, int KeptYoung, int KeptActive, int OrphansDeleted, int PartialUploadsDeleted, int Failed,
    int StoragesUnavailable, bool Truncated);

/// <summary>
/// Removes what the platform itself left behind in the storages the operator configured for the backend: stored backup files no replica references
/// (a completion that never reached the backend, or a replica row that never existed) and unpublished uploads of dead writers.
/// <para>
/// Conservative by construction: a file is deleted only when EVERY condition holds, and each decision is made for that one file alone. The
/// name must be of the managed layout, its tenant must belong to the storage, its attempt must exist in the database under the very tenant, job
/// and run the key names, the run must be finished, nothing may reference the key, and it must be older than the configured minimum age.
/// Everything else (user files, unrecognised names, other tenants, unknown attempts) is left exactly as found. Keys only ever come from the
/// provider's own enumeration, never from a caller, so there is nothing to traverse.
/// </para>
/// </summary>
public sealed class BackupStorageSweeper(
    IBackupRetentionStore store, IBackupStorageResolver storages, BackupRetentionOptions options, TimeProvider time, ILogger<BackupStorageSweeper> logger)
{
    private const int ChunkSize = 200;

    public async Task<StorageSweepReport> SweepAsync(CancellationToken cancellationToken)
    {
        var tally = new Tally();
        var now = time.GetUtcNow();
        foreach (var storage in storages.ManagedStorages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!storage.Provider.Capabilities.Supports(StorageCapabilityIds.Enumerate))
            {
                tally.StoragesUnavailable++;
                logger.LogWarning("Storage {Storage} cannot be enumerated; it is not swept.", storage.Name);
                continue;
            }

            await SweepStorageAsync(storage, now, tally, cancellationToken);
            if (tally.Truncated) break;
        }

        return tally.ToReport();
    }

    private async Task SweepStorageAsync(BackupManagedStorage storage, DateTimeOffset now, Tally tally, CancellationToken cancellationToken)
    {
        // The provider returns nothing younger than this, so a file that is still being written is never even seen; each entry is checked against
        // the threshold of its own kind again below.
        var enumerateBefore = now - (options.OrphanMinimumAge < options.PartialUploadMinimumAge ? options.OrphanMinimumAge : options.PartialUploadMinimumAge);
        var chunk = new List<StorageEntry>(ChunkSize);
        IAsyncEnumerator<StorageEntry>? enumerator = null;
        try
        {
            while (true)
            {
                // Only the storage's own listing is guarded: whatever it throws costs this storage the rest of the sweep, never the other storages
                // (a failure of the database in ProcessChunkAsync is not a storage failure and propagates).
                bool more;
                try
                {
                    enumerator ??= storage.Provider.EnumerateAsync(null, enumerateBefore, cancellationToken).GetAsyncEnumerator(cancellationToken);
                    more = await enumerator.MoveNextAsync();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    tally.StoragesUnavailable++;
                    logger.LogWarning("Storage {Storage} could not be enumerated ({Failure}); the entries seen so far are still processed.", storage.Name, Describe(exception));
                    break;
                }

                if (!more) break;
                chunk.Add(enumerator!.Current);
                if (chunk.Count < ChunkSize) continue;
                await ProcessChunkAsync(storage, chunk, now, tally, cancellationToken);
                chunk.Clear();
                if (tally.Truncated) return;
            }

            await ProcessChunkAsync(storage, chunk, now, tally, cancellationToken);
        }
        finally
        {
            if (enumerator is not null)
            {
                try { await enumerator.DisposeAsync(); }
                catch (Exception exception) when (exception is not OperationCanceledException) { /* a failing listing is already counted */ }
            }
        }
    }

    private async Task ProcessChunkAsync(BackupManagedStorage storage, List<StorageEntry> chunk, DateTimeOffset now, Tally tally, CancellationToken cancellationToken)
    {
        if (chunk.Count == 0) return;
        var providerId = storage.ProviderId.Value;

        var candidates = new List<(StorageEntry Entry, ManagedStorageKey Key)>();
        foreach (var entry in chunk)
        {
            tally.Examined++;
            if (!ManagedBackupLayout.TryParseStorageKey(entry.Key.Value, out var key) || key is null
                || key.IsPartial != (entry.Kind == StorageEntryKind.UnpublishedUpload)
                || (storage.TenantScope is { } scope && scope.Value != key.TenantId))
            {
                tally.Unrecognized++;
                continue;
            }

            var youngest = now - (key.IsPartial ? options.PartialUploadMinimumAge : options.OrphanMinimumAge);
            if (entry.LastModifiedUtc >= youngest)
            {
                tally.KeptYoung++;
                continue;
            }

            if (key.IsPartial && entry.InUse == true)
            {
                tally.KeptActive++;
                continue;
            }

            candidates.Add((entry, key));
        }

        if (candidates.Count == 0) return;

        var objectKeys = candidates.Where(c => !c.Key.IsPartial).Select(c => c.Entry.Key.Value).ToArray();
        var referenced = objectKeys.Length == 0
            ? new HashSet<string>()
            : await store.FindReferencedKeysAsync(providerId, objectKeys, cancellationToken);
        var attemptIds = candidates.Where(c => c.Key.IsPartial || !referenced.Contains(c.Entry.Key.Value)).Select(c => c.Key.AttemptId).Distinct().ToArray();
        var activity = attemptIds.Length == 0
            ? new Dictionary<Guid, BackupAttemptActivity>()
            : await store.GetAttemptActivityAsync(attemptIds, cancellationToken);

        foreach (var (entry, key) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!key.IsPartial && referenced.Contains(entry.Key.Value))
            {
                tally.KeptReferenced++;
                continue;
            }

            // The attempt must exist AND be the one the key names (tenant, job and run included): a lookalike under invented ids is not ours.
            if (!activity.TryGetValue(key.AttemptId, out var attempt)
                || attempt.TenantId.Value != key.TenantId || attempt.JobId != key.JobId || attempt.RunId != key.RunId)
            {
                tally.Unrecognized++;
                continue;
            }

            // Whatever its kind, a file of a run that is queued, leased, running or waiting for a retry is left alone until the run is over.
            if (attempt.RunIsActive || attempt.AttemptIsLive || attempt.RepairIsActive)
            {
                tally.KeptActive++;
                continue;
            }

            if (tally.Deleted >= options.MaxSweepDeletionsPerRun)
            {
                tally.Truncated = true;
                return;
            }

            // The reference check above is a batch read that may be moments old; a replica can be recorded for this key meanwhile.
            if (!key.IsPartial && (await store.FindReferencedKeysAsync(providerId, [entry.Key.Value], cancellationToken)).Contains(entry.Key.Value))
            {
                tally.KeptReferenced++;
                continue;
            }

            await DeleteAsync(storage, entry, key, tally, cancellationToken);
        }
    }

    private async Task DeleteAsync(BackupManagedStorage storage, StorageEntry entry, ManagedStorageKey key, Tally tally, CancellationToken cancellationToken)
    {
        try
        {
            // False: it vanished since the listing (somebody else removed it); nothing of ours to count.
            if (!await storage.Provider.DeleteAsync(entry.Key, cancellationToken)) return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            tally.Failed++;
            logger.LogWarning("Could not delete a leftover file of backup attempt {AttemptId} from storage {Storage}: {Failure}.", key.AttemptId, storage.Name, Describe(exception));
            return;
        }

        if (key.IsPartial) tally.PartialUploadsDeleted++;
        else tally.OrphansDeleted++;
        logger.LogInformation("Deleted {Kind} of backup attempt {AttemptId} from storage {Storage}.", key.IsPartial ? "an unpublished upload" : "an orphaned backup file", key.AttemptId, storage.Name);
    }

    /// <summary>A log-safe reason: the failure kind or type, never the message (a provider message may carry a path).</summary>
    private static string Describe(Exception exception) => exception is StorageException storage ? $"storage failure {storage.Kind}" : exception.GetType().Name;

    private sealed class Tally
    {
        public int Examined, Unrecognized, KeptReferenced, KeptYoung, KeptActive, OrphansDeleted, PartialUploadsDeleted, Failed, StoragesUnavailable;
        public bool Truncated;

        public int Deleted => OrphansDeleted + PartialUploadsDeleted;

        public StorageSweepReport ToReport() =>
            new(Examined, Unrecognized, KeptReferenced, KeptYoung, KeptActive, OrphansDeleted, PartialUploadsDeleted, Failed, StoragesUnavailable, Truncated);
    }
}
