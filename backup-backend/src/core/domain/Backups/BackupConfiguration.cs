using BackupPlatform.Domain.Backups;
namespace BackupPlatform.Domain.Backups;

/// <summary>Execution input contains references, never credentials or host-local paths.</summary>
public sealed record BackupConfiguration(
    string Engine, string Database, string Host, int? Port, bool IntegratedSecurity,
    string? DatabaseCredentialReference, bool Encrypt, bool TrustServerCertificate,
    string StagingReference, string StoragePrefix, bool CopyOnly,
    string ChecksumPolicy, string CompressionPolicy)
{
    /// <summary>
    /// The only destination source: non-empty, exactly one primary. A destination's connection may be null on a JOB (the tenant's default connection
    /// for its provider); a RUN snapshots the resolved id at admission so retries and replica publication use the destination the run was admitted against.
    /// </summary>
    public IReadOnlyList<BackupDestination> Destinations { get; init; } = [];

    /// <summary>
    /// Minimum number of verified replicas required for a run attempt to be deemed successful.
    /// Defaults to 1. Must be between 1 and the total number of destinations.
    /// </summary>
    public int RequiredVerifiedReplicaCount { get; init; } = 1;

    public bool EnforceThreeTwoOne { get; init; }
    public string? SourceSite { get; init; }
    public FileBackupPolicy? Files { get; init; }

    /// <summary>"Off" (default; also what a configuration saved before encryption existed deserializes to), "Optional" or "Required".</summary>
    public string EncryptionPolicy { get; init; } = "Off";

    public BackupConfiguration Validate()
    {
        if (!DatabaseEngines.IsValid(Engine)) throw new ArgumentException("Unsupported backup engine.");
        BackupGuard.Text(Database, 128);
        if (Engine == "postgres" && (Database != Database.Trim() || System.Text.Encoding.UTF8.GetByteCount(Database) > 63 || IntegratedSecurity))
            throw new ArgumentException("PostgreSQL requires a database name of at most 63 UTF-8 bytes and password authentication.");
        if (Engine == "file-system")
        {
            BackupGuard.Reference(Database);
            if (Host != "localhost" || Port is not null || !IntegratedSecurity || DatabaseCredentialReference is not null || Files is null)
                throw new ArgumentException("File backups require a local source reference and explicit file selection.");
            Files.Validate();
        }
        else if (Files is not null) throw new ArgumentException("File selection only applies to file backups.");
        BackupGuard.Text(Host, 255);
        if (Port is < 1 or > 65535) throw new ArgumentException("Invalid database port.");
        if (!IntegratedSecurity) BackupGuard.Text(DatabaseCredentialReference, 128);
        if (DatabaseCredentialReference is not null) BackupGuard.Reference(DatabaseCredentialReference);
        if (StagingReference != "default") throw new ArgumentException("Unknown staging reference.");
        if (StoragePrefix.Length != 0) BackupGuard.RelativeKey(StoragePrefix);
        if (ChecksumPolicy is not ("Required" or "Preferred" or "Disabled") ||
            CompressionPolicy is not ("Required" or "Preferred" or "Disabled"))
            throw new ArgumentException("Invalid backup option policy.");
        if (EncryptionPolicy is not ("Off" or "Optional" or "Required")) throw new ArgumentException("Invalid encryption policy.");

        var effectiveDestinations = Destinations;
        if (effectiveDestinations.Count == 0)
            throw new ArgumentException("At least one storage destination is required.");

        var primaryCount = effectiveDestinations.Count(d => d.IsPrimary);
        if (primaryCount != 1)
            throw new ArgumentException("Exactly one destination must be designated as primary.");

        var distinctDestinations = effectiveDestinations
            .Select(d => (d.StorageProvider, d.StorageConnectionId))
            .Distinct()
            .Count();
        if (distinctDestinations != effectiveDestinations.Count)
            throw new ArgumentException("Duplicate storage destinations are not allowed.");

        foreach (var dest in effectiveDestinations)
        {
            dest.Validate();
        }

        if (RequiredVerifiedReplicaCount < 1 || RequiredVerifiedReplicaCount > effectiveDestinations.Count)
            throw new ArgumentException("RequiredVerifiedReplicaCount must be between 1 and the number of destinations.");
        if (EnforceThreeTwoOne && (RequiredVerifiedReplicaCount < 3 || effectiveDestinations.Count < 3
            || string.IsNullOrWhiteSpace(SourceSite) || SourceSite.Length > 64
            || SourceSite.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))))
            throw new ArgumentException("3-2-1 requires three verified copies and a source site identifier.");

        return this;
    }

    // Value equality for the destination list (a record compares collections by reference, so two loads of the same snapshot would differ).
    public bool Equals(BackupConfiguration? other) => other is not null && Engine == other.Engine && Database == other.Database && Host == other.Host && Port == other.Port &&
        IntegratedSecurity == other.IntegratedSecurity && DatabaseCredentialReference == other.DatabaseCredentialReference && Encrypt == other.Encrypt &&
        TrustServerCertificate == other.TrustServerCertificate && StagingReference == other.StagingReference &&
        StoragePrefix == other.StoragePrefix && CopyOnly == other.CopyOnly && ChecksumPolicy == other.ChecksumPolicy && CompressionPolicy == other.CompressionPolicy &&
        RequiredVerifiedReplicaCount == other.RequiredVerifiedReplicaCount && EncryptionPolicy == other.EncryptionPolicy
        && EnforceThreeTwoOne == other.EnforceThreeTwoOne && SourceSite == other.SourceSite && Equals(Files, other.Files) && Destinations.SequenceEqual(other.Destinations);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Engine); hash.Add(Database); hash.Add(Host); hash.Add(Port); hash.Add(RequiredVerifiedReplicaCount);
        hash.Add(Files);
        foreach (var destination in Destinations) hash.Add(destination);
        return hash.ToHashCode();
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
    Pending = 1, Leased = 2, Running = 3, Succeeded = 4, Failed = 5, Cancelled = 6, AgentLost = 7, RetryPending = 8, Paused = 9
}

public sealed record BackupCompletion(BackupStatus Status, string? FailureCode, BackupArtifact? Artifact, IReadOnlyList<BackupDestinationOutcome>? Outcomes = null);
