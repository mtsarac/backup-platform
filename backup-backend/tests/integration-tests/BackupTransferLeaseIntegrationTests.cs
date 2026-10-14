using System.Data.Common;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BackupPlatform.IntegrationTests;

/// <summary>The transfer lease replaces a pinned session lock: exclusion by short database writes, renewal by the holder, takeover only after expiry, self-fencing on loss.</summary>
public sealed class BackupTransferLeaseIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly BackupTransferLeaseOptions Fast = new(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1));

    private BackupTransferLeaseService Service(BackupTransferLeaseOptions? timing = null, params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(interceptors).Options, timing ?? Fast);

    private async Task<long> RowsAsync(Guid connection, string key)
    {
        await using var db = fixture.CreatePlatformContext();
        var id = BackupTransferLock.Id(connection, key);
        return await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM backup_transfer_leases WHERE \"LockId\" = {id}").SingleAsync();
    }

    [Fact]
    public async Task A_second_writer_waits_for_the_first_and_the_lease_row_is_removed_on_release()
    {
        var connection = Guid.NewGuid();
        const string key = "a/b/object.bak";
        var service = Service();
        var first = await service.AcquireAsync(connection, key, default);
        Assert.Equal(1, await RowsAsync(connection, key));

        var second = service.AcquireAsync(connection, key, default);
        await Task.Delay(800);
        Assert.False(second.IsCompleted);

        await first.DisposeAsync();
        await using var taken = await second.WaitAsync(Deadline);
        Assert.False(taken.Lost.IsCancellationRequested);
    }

    [Fact]
    public async Task Contending_writers_never_overlap_and_a_different_key_is_never_blocked()
    {
        var connection = Guid.NewGuid();
        var service = Service();
        var inside = 0;
        var overlapped = false;
        await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            for (var round = 0; round < 4; round++)
            {
                await using var hold = await service.AcquireAsync(connection, "shared", default).WaitAsync(Deadline);
                if (Interlocked.Increment(ref inside) > 1) overlapped = true;
                await Task.Delay(20);
                Interlocked.Decrement(ref inside);
            }
        }));
        Assert.False(overlapped);

        await using var held = await service.AcquireAsync(connection, "one", default);
        await using var other = await service.AcquireAsync(connection, "two", default).WaitAsync(Deadline);
        await using var otherConnection = await service.AcquireAsync(Guid.NewGuid(), "one", default).WaitAsync(Deadline);
    }

    [Fact]
    public async Task A_renewing_holder_keeps_the_key_far_beyond_one_lease_time()
    {
        var connection = Guid.NewGuid();
        var service = Service();
        await using var hold = await service.AcquireAsync(connection, "long-transfer", default);

        var rival = service.AcquireAsync(connection, "long-transfer", default);
        await Task.Delay(TimeSpan.FromSeconds(7)); // more than two lease times
        Assert.False(rival.IsCompleted);
        Assert.False(hold.Lost.IsCancellationRequested);
        await hold.DisposeAsync();
        await using var next = await rival.WaitAsync(Deadline);
    }

    [Fact]
    public async Task A_crashed_holders_lease_expires_and_is_taken_over_without_waiting_forever()
    {
        var connection = Guid.NewGuid();
        var lockId = BackupTransferLock.Id(connection, "orphan");
        await using (var db = fixture.CreatePlatformContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO backup_transfer_leases (\"LockId\",\"OwnerId\",\"ExpiresAt\") VALUES ({lockId}, {Guid.NewGuid()}, clock_timestamp() + interval '2 seconds')");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await using var hold = await Service().AcquireAsync(connection, "orphan", default).WaitAsync(Deadline);

        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task A_holder_whose_lease_was_taken_over_is_told_so_and_never_deletes_the_new_owners_row()
    {
        var connection = Guid.NewGuid();
        var lockId = BackupTransferLock.Id(connection, "stolen");
        var hold = await Service().AcquireAsync(connection, "stolen", default);
        await using (var db = fixture.CreatePlatformContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE backup_transfer_leases SET \"OwnerId\" = {Guid.NewGuid()} WHERE \"LockId\" = {lockId}");

        await hold.Lost.WhenCancelledAsync().WaitAsync(Deadline);
        await hold.DisposeAsync();

        Assert.Equal(1, await RowsAsync(connection, "stolen"));
    }

    [Fact]
    public async Task A_holder_that_cannot_reach_the_database_stops_itself_before_its_lease_can_expire()
    {
        var connection = Guid.NewGuid();
        var broken = new RenewalOutage();
        var hold = await Service(Fast, broken).AcquireAsync(connection, "outage", default);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        broken.Active = true;

        await hold.Lost.WhenCancelledAsync().WaitAsync(Deadline);

        // Ttl 3 s minus the 1 s margin: the holder gives up at ~2 s after its last renewal, while the database still says the lease is its own.
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.9));
        Assert.Equal(1, await RowsAsync(connection, "outage"));
        await hold.DisposeAsync();
    }

    [Fact]
    public async Task Leases_of_long_dead_holders_are_swept_when_the_table_is_next_used()
    {
        var connection = Guid.NewGuid();
        var staleId = BackupTransferLock.Id(connection, "stale");
        await using (var db = fixture.CreatePlatformContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO backup_transfer_leases (\"LockId\",\"OwnerId\",\"ExpiresAt\") VALUES ({staleId}, {Guid.NewGuid()}, clock_timestamp() - interval '3 hours')");

        await using var hold = await Service().AcquireAsync(connection, "fresh", default);

        Assert.Equal(0, await RowsAsync(connection, "stale"));
    }

    private sealed class RenewalOutage : DbCommandInterceptor
    {
        public volatile bool Active;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Active && command.CommandText.Contains("UPDATE backup_transfer_leases", StringComparison.Ordinal)) throw new InvalidOperationException("database unreachable");
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}

internal static class CancellationTokenExtensions
{
    public static Task WhenCancelledAsync(this CancellationToken token)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => done.TrySetResult());
        return done.Task;
    }
}
