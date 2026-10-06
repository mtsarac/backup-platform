namespace BackupPlatform.Domain.Backups;

/// <summary>Execution input contains references, never credentials or host-local paths.</summary>
public sealed record BackupConfiguration(
    string Engine, string Database, string Host, int? Port, bool IntegratedSecurity,
    string? DatabaseCredentialReference, bool Encrypt, bool TrustServerCertificate,
    string StagingReference, string StorageProvider, string StoragePrefix, bool CopyOnly,
    string ChecksumPolicy, string CompressionPolicy)
{
    public BackupStorageExecutionLocation StorageExecutionLocation { get; init; } =
        StorageProvider == "local" ? BackupStorageExecutionLocation.Agent : BackupStorageExecutionLocation.Backend;

    public BackupConfiguration Validate()
    {
        if (Engine != "mssql") throw new ArgumentException("Unsupported database engine.");
        BackupGuard.Text(Database, 128);
        BackupGuard.Text(Host, 255);
        if (Port is < 1 or > 65535) throw new ArgumentException("Invalid database port.");
        if (!IntegratedSecurity) BackupGuard.Text(DatabaseCredentialReference, 128);
        if (DatabaseCredentialReference is not null) BackupGuard.Reference(DatabaseCredentialReference);
        if (StagingReference != "default") throw new ArgumentException("Unknown staging reference.");
        BackupGuard.Reference(StorageProvider);
        if (StorageExecutionLocation is not (BackupStorageExecutionLocation.Backend or BackupStorageExecutionLocation.Agent) ||
            StorageExecutionLocation == BackupStorageExecutionLocation.Agent && StorageProvider != "local")
            throw new ArgumentException("Invalid storage execution location.");
        if (StoragePrefix.Length != 0) BackupGuard.RelativeKey(StoragePrefix);
        if (ChecksumPolicy is not ("Required" or "Preferred" or "Disabled") ||
            CompressionPolicy is not ("Required" or "Preferred" or "Disabled"))
            throw new ArgumentException("Invalid backup option policy.");
        return this;
    }
}

internal static class BackupGuard
{
    // Durable timestamps use microseconds so immutable results remain equal after a persistence round trip.
    public static DateTimeOffset Timestamp(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
    public static void Id(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A nonempty identifier is required.");
    }
    public static void Text(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new ArgumentException("Invalid backup metadata.");
    }
    public static void Reference(string value)
    {
        Text(value, 128);
        if (value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new ArgumentException("Invalid configuration reference.");
    }
    public static void RelativeKey(string value)
    {
        Text(value, 1024);
        if (value.StartsWith('/') || value.Contains('\\') || value.Contains(':') ||
            value.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("A relative storage key is required.");
    }
    public static void Checksum(string value, string algorithm)
    {
        if (algorithm != "sha256" || value.Length != 64 || !value.All(char.IsAsciiHexDigit))
            throw new ArgumentException("A SHA-256 checksum is required.");
    }
}

/// <summary>
/// Attempts only ever use Pending..AgentLost. <see cref="RetryPending"/> exists on the run only: the last attempt failed with a
/// retryable cause, attempts remain, and the next one is scheduled (<see cref="BackupRun.NextAttemptAt"/>).
/// </summary>
public enum BackupStatus
{
    Pending = 1, Leased = 2, Running = 3, Succeeded = 4, Failed = 5, Cancelled = 6, AgentLost = 7, RetryPending = 8
}

public sealed record BackupCompletion(BackupStatus Status, string? FailureCode, BackupArtifact? Artifact);
