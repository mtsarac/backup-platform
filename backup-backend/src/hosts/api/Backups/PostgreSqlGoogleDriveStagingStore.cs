using System.Buffers;
using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.GoogleDrive;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BackupPlatform.Api.Backups;

/// <summary>A short global advisory transaction admits bytes; no transaction or database connection spans streaming or upload.</summary>
public sealed class PostgreSqlGoogleDriveStagingStore : BackgroundService, IGoogleDriveStagingStore
{
    public const long AdmissionLockKey = 4778394325016135425L; // 0x4250_4452_5354_4701
    private const int BufferSize = 1024 * 1024;
    private const int CleanupBatchSize = 128;
    private readonly Meter _meter = new("BackupPlatform.GoogleDrive.Staging");
    private readonly Counter<long> _cleanupFailures;
    private readonly DbContextOptions<PlatformDbContext> _database;
    private readonly ILogger<PostgreSqlGoogleDriveStagingStore> _logger;
    private readonly TimeProvider _time;
    private readonly Func<string, long>? _probe;
    private readonly string _basePath;
    private readonly string _directoryPath;
    private readonly Lazy<string> _volumeId;
    private readonly string _nodeId;
    private readonly bool _sharedVolume;
    private readonly long _capacity;
    private readonly long _nodeCapacity;
    private readonly long _minFreeBytes;
    private readonly TimeSpan _leaseDuration;
    private readonly TimeSpan _cleanupAge;
    private readonly TimeSpan _cleanupInterval;
    private long _reservedBytes;
    private long _activeBytes;
    private long _staleBytes;
    private int _cleanupOffset;

    public PostgreSqlGoogleDriveStagingStore(DbContextOptions<PlatformDbContext> database,
        IOptions<GoogleDrivePlatformOptions> options, ILogger<PostgreSqlGoogleDriveStagingStore> logger,
        TimeProvider? time = null, Func<string, long>? availableBytesProbe = null)
    {
        _database = database;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _probe = availableBytesProbe;
        var configuration = options.Value;
        _capacity = configuration.StagingCapacityBytes;
        _nodeCapacity = configuration.NodeStagingCapacityBytes;
        _minFreeBytes = configuration.MinStagingFreeBytes;
        _leaseDuration = configuration.StagingLeaseDuration;
        _cleanupAge = configuration.StagingCleanupAge;
        _cleanupInterval = configuration.StagingCleanupInterval;
        if (_capacity <= 0 || _nodeCapacity <= 0 || _minFreeBytes < 0 || _leaseDuration < TimeSpan.FromMilliseconds(3)
            || _cleanupAge < TimeSpan.Zero || _cleanupInterval <= TimeSpan.Zero)
            throw new ArgumentException("Invalid Google Drive staging capacity or lease configuration.", nameof(options));
        _nodeId = string.IsNullOrWhiteSpace(configuration.StagingNodeId) ? Environment.MachineName : configuration.StagingNodeId;
        _sharedVolume = !string.IsNullOrWhiteSpace(configuration.StagingVolumeId);
        _basePath = string.IsNullOrWhiteSpace(configuration.StagingDirectory) ? System.IO.Path.GetTempPath() : configuration.StagingDirectory;
        _volumeId = new Lazy<string>(() =>
        {
            if (_sharedVolume) return configuration.StagingVolumeId;
            using var directory = new GoogleDriveStagingDirectory(_basePath, _probe);
            return directory.GetOrCreateVolumeId();
        }, LazyThreadSafetyMode.PublicationOnly);
        if (_nodeId.Length > 128 || configuration.StagingVolumeId.Length > 128) throw new ArgumentException("Staging identities must not exceed 128 characters.", nameof(options));
        _directoryPath = System.IO.Path.Combine(System.IO.Path.GetFullPath(_basePath), GoogleDriveStagingDirectory.OwnedDirectoryName);
        if (_directoryPath.Length > 4096) throw new ArgumentException("Staging directory is too long.", nameof(options));
        _cleanupFailures = _meter.CreateCounter<long>("drive.staging.cleanup.failures");
        _meter.CreateObservableGauge("drive.staging.reserved.bytes", () => Interlocked.Read(ref _reservedBytes), "By");
        _meter.CreateObservableGauge("drive.staging.active.bytes", () => Interlocked.Read(ref _activeBytes), "By");
        _meter.CreateObservableGauge("drive.staging.stale.bytes", () => Interlocked.Read(ref _staleBytes), "By");
    }

    public async Task<GoogleDriveStagedFile> StageAsync(Stream content, long expectedLength, ContentChecksum checksum, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(checksum);
        if (!content.CanRead) throw new ArgumentException("Staging content must be readable.", nameof(content));
        if (expectedLength <= 0) throw new ArgumentOutOfRangeException(nameof(expectedLength));
        if (expectedLength > _capacity || expectedLength > _nodeCapacity) throw CapacityFailure();
        token.ThrowIfCancellationRequested();
        GoogleDriveStagingDirectory? directory = null;
        GoogleDriveStagingReservation? reservation = null;
        GoogleDriveStagingLease? lease = null;
        try
        {
            await CleanupAsync(token);
            directory = new GoogleDriveStagingDirectory(_basePath, _probe);
            reservation = await ReserveAsync(directory, expectedLength, token);
            var file = directory.Create(reservation.Id);
            lease = new GoogleDriveStagingLease(directory, file, reservation.Id,
                renewal => RenewAsync(reservation, renewal), () => ReleaseAsync(reservation), _leaseDuration, _time, _logger, token);
            await CopyAsync(content, lease, reservation, checksum);
            return lease.Handle;
        }
        catch (Exception exception)
        {
            try
            {
                if (lease is not null) await lease.Handle.DisposeAsync();
                else if (reservation is not null && directory is not null && directory.TryDeleteStale(reservation.Id)) await ReleaseAsync(reservation);
            }
            catch (Exception cleanupError)
            {
                _cleanupFailures.Add(1);
                _logger.LogWarning(cleanupError, "Failed staging cleanup remains durably admitted until a later sweep.");
            }
            finally { if (lease is null) directory?.Dispose(); }
            if (exception is IOException or UnauthorizedAccessException or Win32Exception or DbException)
                throw new StorageException(StorageFailureKind.Io, "Google Drive staging is unavailable.", exception);
            throw;
        }
    }

    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        using var directory = new GoogleDriveStagingDirectory(_basePath, _probe);
        var volumeId = _volumeId.Value;
        await using var context = PlatformDbContext.CreateForPlatformOperations(_database);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(context, cancellationToken);
        var now = await DatabaseNowAsync(context, cancellationToken);
        var staleBefore = now - _cleanupAge;
        var candidates = await context.GoogleDriveStagingReservations.AsNoTracking()
            .Where(row => row.VolumeId == volumeId && row.DirectoryPath == _directoryPath && row.LeaseExpiresAt <= staleBefore)
            .OrderBy(row => row.LeaseExpiresAt).ThenBy(row => row.Id).Skip(_cleanupOffset).Take(CleanupBatchSize).ToListAsync(cancellationToken);
        var retained = 0;
        if (candidates.Count != 0)
        {
            foreach (var row in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (row.FileName != $"{row.Id:N}.stage") throw new IOException("Invalid tracked staging file name.");
                    if (!directory.TryDeleteStale(row.Id)) { retained++; continue; }
                    await context.GoogleDriveStagingReservations.Where(candidate => candidate.Id == row.Id && candidate.LeaseToken == row.LeaseToken)
                        .ExecuteDeleteAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
                {
                    retained++;
                    _cleanupFailures.Add(1);
                    _logger.LogWarning(exception, "Tracked Google Drive staging cleanup failed; reserved bytes remain counted.");
                }
            }
        }
        await UpdateMetricsAsync(context, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        // Locked stale owners must not starve crash remnants later in the scan.
        _cleanupOffset = candidates.Count < CleanupBatchSize || _cleanupOffset > int.MaxValue - retained ? 0 : _cleanupOffset + retained;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_cleanupInterval, _time);
        try
        {
            do
            {
                try { await CleanupAsync(stoppingToken); }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    _cleanupFailures.Add(1);
                    _logger.LogWarning(exception, "Google Drive staging sweep failed; durable admission is unchanged.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override void Dispose()
    {
        base.Dispose();
        _meter.Dispose();
    }

    private async Task<GoogleDriveStagingReservation> ReserveAsync(GoogleDriveStagingDirectory directory, long expectedLength, CancellationToken token)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(_database);
        await using var transaction = await context.Database.BeginTransactionAsync(token);
        await LockAsync(context, token);
        var totals = await ReadTotalsAsync(context, token);
        if (totals.Global > _capacity - expectedLength || totals.Node > _nodeCapacity - expectedLength || totals.Volume > _nodeCapacity - expectedLength)
            throw CapacityFailure();
        if (totals.Unwritten + expectedLength + _minFreeBytes > directory.GetAvailableBytes()) throw CapacityFailure();
        var now = await DatabaseNowAsync(context, token);
        var row = new GoogleDriveStagingReservation(Guid.NewGuid(), Guid.NewGuid(), _volumeId.Value, _nodeId, _sharedVolume,
            _directoryPath, expectedLength, now, now + _leaseDuration);
        context.GoogleDriveStagingReservations.Add(row);
        await context.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return row;
    }

    private async Task CopyAsync(Stream content, GoogleDriveStagingLease lease, GoogleDriveStagingReservation row, ContentChecksum checksum)
    {
        var token = lease.Handle.CancellationToken;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;
        try
        {
            while (true)
            {
                var count = await content.ReadAsync(buffer.AsMemory(0, BufferSize), token);
                if (count == 0) break;
                if (count > row.ExpectedBytes - written) throw IntegrityFailure();
                await CheckPhysicalAsync(lease, row, token);
                await lease.File.WriteAsync(buffer.AsMemory(0, count), token);
                hash.AppendData(buffer, 0, count);
                written += count;
                await RecordWrittenAsync(row, written, token);
            }
            await lease.File.FlushAsync(token);
            if (written != row.ExpectedBytes || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(checksum.Hex)))
                throw IntegrityFailure();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task CheckPhysicalAsync(GoogleDriveStagingLease lease, GoogleDriveStagingReservation row, CancellationToken token)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(_database);
        await using var transaction = await context.Database.BeginTransactionAsync(token);
        await LockAsync(context, token);
        var now = await DatabaseNowAsync(context, token);
        if (!await context.GoogleDriveStagingReservations.AnyAsync(candidate => candidate.Id == row.Id
            && candidate.LeaseToken == row.LeaseToken && candidate.LeaseExpiresAt > now, token))
            throw new OperationCanceledException("Google Drive staging lease was lost.", token);
        var totals = await ReadTotalsAsync(context, token);
        if (totals.Unwritten + _minFreeBytes > lease.AvailableBytes) throw CapacityFailure();
        await transaction.CommitAsync(token);
    }

    private async Task RecordWrittenAsync(GoogleDriveStagingReservation row, long written, CancellationToken token)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(_database);
        var updated = await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE google_drive_staging_reservations SET "WrittenBytes" = {written}
            WHERE "Id" = {row.Id} AND "LeaseToken" = {row.LeaseToken} AND "LeaseExpiresAt" > clock_timestamp()
            """, token);
        if (updated != 1) throw new OperationCanceledException("Google Drive staging lease was lost.", token);
    }

    private async Task<bool> RenewAsync(GoogleDriveStagingReservation row, CancellationToken token)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(_database);
        return await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE google_drive_staging_reservations SET "LeaseExpiresAt" = clock_timestamp() + {_leaseDuration}
            WHERE "Id" = {row.Id} AND "LeaseToken" = {row.LeaseToken} AND "LeaseExpiresAt" > clock_timestamp()
            """, token) == 1;
    }

    private async ValueTask ReleaseAsync(GoogleDriveStagingReservation row)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(_database);
        await using var transaction = await context.Database.BeginTransactionAsync(CancellationToken.None);
        await LockAsync(context, CancellationToken.None);
        await context.GoogleDriveStagingReservations.Where(candidate => candidate.Id == row.Id && candidate.LeaseToken == row.LeaseToken)
            .ExecuteDeleteAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private async Task<AdmissionTotals> ReadTotalsAsync(PlatformDbContext context, CancellationToken token)
    {
        return await context.GoogleDriveStagingReservations.GroupBy(row => 1).Select(group => new AdmissionTotals(
            group.Sum(row => (decimal)row.ExpectedBytes),
            group.Sum(row => row.NodeId == _nodeId ? (decimal)row.ExpectedBytes : 0),
            group.Sum(row => row.VolumeId == _volumeId.Value ? (decimal)row.ExpectedBytes : 0),
            group.Sum(row => row.VolumeId == _volumeId.Value ? (decimal)row.ExpectedBytes - row.WrittenBytes : 0))).SingleOrDefaultAsync(token)
            ?? new AdmissionTotals(0, 0, 0, 0);
    }

    private async Task UpdateMetricsAsync(PlatformDbContext context, DateTimeOffset now, CancellationToken token)
    {
        var totals = await context.GoogleDriveStagingReservations.GroupBy(row => 1).Select(group => new
        {
            Reserved = group.Sum(row => (decimal)row.ExpectedBytes),
            Active = group.Sum(row => row.LeaseExpiresAt > now ? (decimal)row.WrittenBytes : 0),
            Stale = group.Sum(row => row.LeaseExpiresAt <= now ? (decimal)row.ExpectedBytes : 0)
        }).SingleOrDefaultAsync(token);
        Interlocked.Exchange(ref _reservedBytes, (long)Math.Min(totals?.Reserved ?? 0, long.MaxValue));
        Interlocked.Exchange(ref _activeBytes, (long)Math.Min(totals?.Active ?? 0, long.MaxValue));
        Interlocked.Exchange(ref _staleBytes, (long)Math.Min(totals?.Stale ?? 0, long.MaxValue));
    }

    private static Task LockAsync(PlatformDbContext context, CancellationToken token) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({AdmissionLockKey})", token);

    private static Task<DateTimeOffset> DatabaseNowAsync(PlatformDbContext context, CancellationToken token) =>
        context.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);

    private static StorageException CapacityFailure() => new(StorageFailureKind.InsufficientSpace, "Google Drive staging capacity is exhausted.");
    private static StorageException IntegrityFailure() => new(StorageFailureKind.IntegrityMismatch, "Staged content does not match its expected length and checksum.");
    private sealed record AdmissionTotals(decimal Global, decimal Node, decimal Volume, decimal Unwritten);
}
