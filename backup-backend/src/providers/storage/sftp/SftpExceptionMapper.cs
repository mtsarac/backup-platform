using Renci.SshNet.Messages.Transport;
using System.Net.Sockets;
using System.Security.Authentication;
using BackupPlatform.StorageProviders.Abstractions;
using Renci.SshNet.Common;

namespace BackupPlatform.StorageProviders.Sftp;

public static class SftpExceptionMapper
{
    public static StorageException Map(Exception exception, string defaultUserMessage = "SFTP operation failed.")
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is StorageException alreadyMapped)
        {
            return alreadyMapped;
        }

        if (exception is SshAuthenticationException authEx)
        {
            return new StorageException(StorageFailureKind.AuthenticationFailed, "SFTP authentication failed.", authEx);
        }

        if (exception is SshConnectionException connEx)
        {
            if (connEx.DisconnectReason == DisconnectReason.HostNotAllowedToConnect ||
                connEx.DisconnectReason == DisconnectReason.ByApplication)
            {
                return new StorageException(StorageFailureKind.AccessDenied, "SFTP connection access denied.", connEx);
            }

            return new StorageException(StorageFailureKind.NetworkUnavailable, "Failed to connect to SFTP host.", connEx);
        }

        if (exception is SftpPermissionDeniedException permEx)
        {
            return new StorageException(StorageFailureKind.AccessDenied, "SFTP access denied.", permEx);
        }

        if (exception is SftpPathNotFoundException notFoundEx)
        {
            return new StorageException(StorageFailureKind.NotFound, "SFTP target path not found.", notFoundEx);
        }

        if (exception is SshOperationTimeoutException or TimeoutException)
        {
            return new StorageException(StorageFailureKind.Timeout, "SFTP operation timed out.", exception);
        }

        if (exception is SocketException sockEx)
        {
            if (sockEx.SocketErrorCode is SocketError.TimedOut)
            {
                return new StorageException(StorageFailureKind.Timeout, "SFTP network operation timed out.", sockEx);
            }

            return new StorageException(StorageFailureKind.NetworkUnavailable, "SFTP network error occurred.", sockEx);
        }

        if (exception is AuthenticationException)
        {
            return new StorageException(StorageFailureKind.AuthenticationFailed, "SFTP authentication failed.", exception);
        }

        if (exception is SshException sshEx)
        {
            return new StorageException(StorageFailureKind.Io, "SFTP server I/O error occurred.", sshEx);
        }

        if (exception is IOException ioEx)
        {
            return new StorageException(StorageFailureKind.Io, "SFTP I/O error occurred.", ioEx);
        }

        return new StorageException(StorageFailureKind.Unexpected, defaultUserMessage, exception);
    }
}
