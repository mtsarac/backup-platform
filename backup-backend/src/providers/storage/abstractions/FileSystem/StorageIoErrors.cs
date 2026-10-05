namespace BackupPlatform.StorageProviders.Abstractions.FileSystem;

/// <summary>
/// Translates file system exceptions into user-safe <see cref="StorageException"/>s. The original exception is kept as the inner
/// exception (for logs); the message is fixed text and never contains a path.
/// </summary>
internal static class StorageIoErrors
{
    // Windows: ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL as HRESULTs. Unix: ENOSPC (same value on Linux and macOS) and
    // Linux EDQUOT; .NET exposes the raw errno as the HResult of the IOException. The value ranges do not collide in practice.
    private const int WindowsDiskFull = unchecked((int)0x80070070);
    private const int WindowsHandleDiskFull = unchecked((int)0x80070027);
    private const int UnixNoSpace = 28;
    private const int LinuxQuotaExceeded = 122;

    public static StorageException Map(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException => new StorageException(StorageFailureKind.AccessDenied, "Access to the storage was denied.", exception),
            IOException io when IsDiskFull(io) => new StorageException(StorageFailureKind.InsufficientSpace, "The storage does not have enough free space.", exception),
            _ => new StorageException(StorageFailureKind.Io, "The storage could not be read or written.", exception)
        };

    private static bool IsDiskFull(IOException exception) =>
        exception.HResult is WindowsDiskFull or WindowsHandleDiskFull or UnixNoSpace or LinuxQuotaExceeded;
}
