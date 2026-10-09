using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.StorageProviders.GoogleDrive;

public interface IGoogleDriveStagingStore
{
    Task<GoogleDriveStagedFile> StageAsync(Stream content, long expectedLength, ContentChecksum checksum, CancellationToken token);
}

/// <summary>Keeps admission, the file lock and lease alive until remote verification and catalog publication finish.</summary>
public sealed class GoogleDriveStagedFile : IAsyncDisposable
{
    private readonly Func<ValueTask> _disposeAsync;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;

    public GoogleDriveStagedFile(string path, CancellationToken cancellationToken, Func<ValueTask> disposeAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        CancellationToken = cancellationToken;
        _disposeAsync = disposeAsync ?? throw new ArgumentNullException(nameof(disposeAsync));
    }

    public string Path { get; }
    public CancellationToken CancellationToken { get; }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            return new ValueTask(_disposeTask ??= _disposeAsync().AsTask());
        }
    }
}
