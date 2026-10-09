using BackupPlatform.StorageProviders.GoogleDrive;

namespace BackupPlatform.Api.Backups;

internal sealed class GoogleDriveStagingLease
{
    private readonly GoogleDriveStagingDirectory _directory;
    private readonly FileStream _file;
    private readonly Guid _id;
    private readonly Func<CancellationToken, Task<bool>> _renewAsync;
    private readonly Func<ValueTask> _releaseAsync;
    private readonly TimeSpan _renewInterval;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _owner;
    private readonly CancellationTokenSource _heartbeatStop = new();
    private readonly Task _heartbeat;

    public GoogleDriveStagingLease(GoogleDriveStagingDirectory directory, FileStream file, Guid id,
        Func<CancellationToken, Task<bool>> renewAsync, Func<ValueTask> releaseAsync,
        TimeSpan leaseDuration, TimeProvider time, ILogger logger, CancellationToken token)
    {
        _directory = directory;
        _file = file;
        _id = id;
        _renewAsync = renewAsync;
        _releaseAsync = releaseAsync;
        _renewInterval = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond, leaseDuration.Ticks / 3));
        _time = time;
        _logger = logger;
        _owner = CancellationTokenSource.CreateLinkedTokenSource(token);
        _heartbeat = HeartbeatAsync();
        Handle = new GoogleDriveStagedFile(directory.GetUploadPath(file, id), _owner.Token, DisposeAsync);
    }

    public GoogleDriveStagedFile Handle { get; }
    public long AvailableBytes => _directory.GetAvailableBytes();
    public FileStream File => _file;

    private async Task HeartbeatAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(_renewInterval, _time, _heartbeatStop.Token);
                using var deadline = new CancellationTokenSource(_renewInterval, _time);
                using var renewal = CancellationTokenSource.CreateLinkedTokenSource(_heartbeatStop.Token, deadline.Token);
                if (!await _renewAsync(renewal.Token))
                {
                    _owner.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_heartbeatStop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Google Drive staging lease renewal failed; its owner has been cancelled.");
            _owner.Cancel();
        }
    }

    private async ValueTask DisposeAsync()
    {
        _heartbeatStop.Cancel();
        await _heartbeat;
        try
        {
            await _directory.DeleteOwnedAsync(_id, _file);
            await _releaseAsync();
        }
        finally
        {
            await _file.DisposeAsync();
            _directory.Dispose();
            _owner.Cancel();
            _owner.Dispose();
            _heartbeatStop.Dispose();
        }
    }
}
