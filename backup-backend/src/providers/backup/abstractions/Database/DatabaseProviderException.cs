namespace BackupPlatform.BackupProviders.Abstractions.Database;

/// <summary>Engine-independent classification of a provider failure.</summary>
public enum DatabaseFailureKind
{
    Unexpected = 0,
    /// <summary>The instance could not be reached or the connection was lost.</summary>
    Unreachable,
    Timeout,
    AuthenticationFailed,
    PermissionDenied,
    /// <summary>The supplied settings cannot form a connection.</summary>
    InvalidConfiguration,
    /// <summary>The TLS handshake reached the server but its certificate was not trusted (self-signed, unknown issuer or name mismatch).</summary>
    ServerCertificateUntrusted,
    /// <summary>Encryption is required but the server does not speak TLS at all (no handshake took place).</summary>
    ServerTlsUnavailable
}

/// <summary>
/// A provider failure whose <see cref="Exception.Message"/> is safe to show to a user. The engine's own exception is kept
/// as <see cref="Exception.InnerException"/> strictly for internal diagnostics (logs); never forward it or its
/// <c>ToString()</c> to a user or over the wire. Caller cancellation is never wrapped: it surfaces as
/// <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class DatabaseProviderException : Exception
{
    public DatabaseProviderException(DatabaseFailureKind kind, string userMessage, Exception? innerException = null)
        : base(userMessage, innerException)
    {
        Kind = kind;
    }

    public DatabaseFailureKind Kind { get; }
}
