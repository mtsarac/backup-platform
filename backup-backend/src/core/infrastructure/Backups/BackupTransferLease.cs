using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using BackupPlatform.Infrastructure.Persistence;

namespace BackupPlatform.Infrastructure.Backups;

/// <summary>The row behind one storage key's transfer lease; see <see cref="BackupTransferLeaseService"/>.</summary>
public sealed class BackupTransferLease
{
    public long LockId { get; private set; }
    public Guid OwnerId { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
}

internal sealed class BackupTransferLeaseConfiguration : IEntityTypeConfiguration<BackupTransferLease>
{
    public void Configure(EntityTypeBuilder<BackupTransferLease> builder)
    {
        builder.ToTable("backup_transfer_leases");
        builder.HasKey(lease => lease.LockId);
        builder.Property(lease => lease.LockId).ValueGeneratedNever();
        builder.HasIndex(lease => lease.ExpiresAt);
    }
}

/// <summary>Timing of a transfer lease. The lease must outlive a missed renewal or two; the holder stops itself well before the database would hand the key to another writer.</summary>
public sealed record BackupTransferLeaseOptions(TimeSpan Ttl, TimeSpan RenewEvery, TimeSpan SelfFenceMargin)
{
    public static BackupTransferLeaseOptions Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
}

/// <summary>
/// Mutual exclusion between the writers, verifiers and deleters of ONE storage object (and the retention sweep that must not publish absence ahead of an
/// in-flight writer), as a short-lived DATABASE LEASE instead of a session lock: nothing holds a PostgreSQL connection or transaction while a (possibly hours-long,
/// bandwidth-paced) transfer runs. A holder renews its row every few seconds; a holder that cannot renew in time cancels its own work
/// (<see cref="TransferLeaseHold.Lost"/>) before the lease can expire, and a crashed holder's lease simply expires. Expiry is judged by the database clock only.
/// Fencing is unchanged: every transfer still re-validates its attempt's lease after acquiring this one.
/// </summary>
public sealed class BackupTransferLeaseService(DbContextOptions<PlatformDbContext> dbOptions, BackupTransferLeaseOptions? timing = null)
{
    private static readonly TimeSpan FirstPoll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaximumPoll = TimeSpan.FromSeconds(1);
    private readonly BackupTransferLeaseOptions _timing = timing ?? BackupTransferLeaseOptions.Default;

    /// <summary>Waits until no other holder owns the key, then owns it until the hold is disposed or lost.</summary>
    public async Task<TransferLeaseHold> AcquireAsync(Guid connectionId, string key, CancellationToken cancellationToken)
    {
        var lockId = BackupTransferLock.Id(connectionId, key);
        var owner = Guid.NewGuid();
        var poll = FirstPoll;
        while (true)
        {
            if (await TryAcquireAsync(lockId, owner, cancellationToken).ConfigureAwait(false)) return new TransferLeaseHold(this, lockId, owner, _timing);
            await Task.Delay(poll, cancellationToken).ConfigureAwait(false);
            poll = TimeSpan.FromMilliseconds(Math.Min(poll.TotalMilliseconds * 2, MaximumPoll.TotalMilliseconds));
        }
    }

    private async Task<bool> TryAcquireAsync(long lockId, Guid owner, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(dbOptions);
        return await BackupTransferLeases.TryTakeAsync(context, lockId, owner, _timing.Ttl, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> RenewAsync(long lockId, Guid owner, CancellationToken cancellationToken)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(dbOptions);
        var seconds = _timing.Ttl.TotalSeconds;
        return await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE backup_transfer_leases SET \"ExpiresAt\" = clock_timestamp() + make_interval(secs => {seconds}) WHERE \"LockId\" = {lockId} AND \"OwnerId\" = {owner}",
            cancellationToken).ConfigureAwait(false) == 1;
    }

    internal async Task ReleaseAsync(long lockId, Guid owner)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using var context = PlatformDbContext.CreateForPlatformOperations(dbOptions);
            await context.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM backup_transfer_leases WHERE \"LockId\" = {lockId} AND \"OwnerId\" = {owner}", timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The row expires by itself within the lease time; a failed release must not turn a finished transfer into an error.
        }
    }
}

/// <summary>Ownership of one storage key. <see cref="Lost"/> is cancelled when the lease could not be renewed (or was taken over): the holder must stop writing.</summary>
public sealed class TransferLeaseHold : IAsyncDisposable
{
    private readonly BackupTransferLeaseService _service;
    private readonly long _lockId;
    private readonly Guid _owner;
    private readonly CancellationTokenSource _lost = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _renewal;
    private int _disposed;

    internal TransferLeaseHold(BackupTransferLeaseService service, long lockId, Guid owner, BackupTransferLeaseOptions timing)
    {
        _service = service;
        _lockId = lockId;
        _owner = owner;
        // The fence is a timer of its own, never a check inside the renewal loop: a renewal that hangs or fails slowly cannot postpone it. It is re-armed only
        // by a successful renewal, so the holder is told to stop Ttl - SelfFenceMargin after the database last promised the lease, whatever happens meanwhile.
        _lost.CancelAfter(timing.Ttl - timing.SelfFenceMargin);
        _renewal = RenewAsync(timing, _stop.Token);
    }

    public CancellationToken Lost => _lost.Token;

    private async Task RenewAsync(BackupTransferLeaseOptions timing, CancellationToken cancellationToken)
    {
        var wait = timing.RenewEvery;
        try
        {
            while (true)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                var asked = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    // A hung database call must not outlast its turn: each attempt is bounded by the renewal interval.
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    attempt.CancelAfter(timing.RenewEvery);
                    if (!await _service.RenewAsync(_lockId, _owner, attempt.Token).ConfigureAwait(false))
                    {
                        await _lost.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                    // The database stamped the new expiry when the statement ran, at most `asked` ago: count the fence from the moment the request was made.
                    _lost.CancelAfter(Max(timing.Ttl - timing.SelfFenceMargin - asked.Elapsed, TimeSpan.Zero));
                    wait = timing.RenewEvery;
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    wait = TimeSpan.FromSeconds(1); // database trouble: keep trying; the fence timer is already running
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        await _renewal.ConfigureAwait(false);
        _stop.Dispose();
        // Owner-checked: a lease that was taken over is not ours to delete, one that merely ran into the fence still is.
        await _service.ReleaseAsync(_lockId, _owner).ConfigureAwait(false);
        _lost.Dispose();
    }
}

internal static class BackupTransferLeases
{
    /// <summary>
    /// Takes the row if nobody holds a live lease on it. With a zero <paramref name="ttl"/> the row is taken only for the surrounding transaction (it is locked until
    /// commit, so a writer's acquisition waits for it and then finds it expired): that is how the retention claim excludes writers without owning a lease.
    /// </summary>
    public static async Task<bool> TryTakeAsync(PlatformDbContext context, long lockId, Guid owner, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var seconds = ttl.TotalSeconds;
        // Rows of long-dead holders and of past retention claims are not worth keeping; the sweep rides on every take because that is when the table is touched anyway. SKIP LOCKED: a concurrent sweep or a retention transaction that holds rows never makes another take wait (or deadlock) on them.
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM backup_transfer_leases WHERE "LockId" IN (
                SELECT "LockId" FROM backup_transfer_leases WHERE "ExpiresAt" < clock_timestamp() - interval '1 hour' FOR UPDATE SKIP LOCKED)
            """, cancellationToken).ConfigureAwait(false);
        return await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO backup_transfer_leases ("LockId", "OwnerId", "ExpiresAt")
            VALUES ({lockId}, {owner}, clock_timestamp() + make_interval(secs => {seconds}))
            ON CONFLICT ("LockId") DO UPDATE SET "OwnerId" = EXCLUDED."OwnerId", "ExpiresAt" = EXCLUDED."ExpiresAt"
            WHERE backup_transfer_leases."ExpiresAt" <= clock_timestamp()
            """, cancellationToken).ConfigureAwait(false) == 1;
    }
}
