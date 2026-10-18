using System.Security.Cryptography;
using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Encryption;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>Resolves only host-registered staging and local credential names; task input can never choose a staging path.</summary>
public sealed class BackupTaskRequestFactory(IBackupTaskCredentialResolver credentials, IEnumerable<StagingArea> stagingAreas,
    ITaskStorageProviderFactory? storageFactory = null, Func<BackupTaskSnapshot, IUploadBandwidth>? uploadBandwidth = null, LocalVolumes? volumes = null)
{
    private readonly StagingArea? _staging = stagingAreas.SingleOrDefault();

    public async ValueTask<DatabaseBackupPipelineRequest> CreateAsync(BackupTaskSnapshot task, CancellationToken cancellationToken)
    {
        var configuration = task.Configuration;
        ValidateConfiguration(configuration);
        if (task.MaxArtifactBytes is < 0) throw new ArgumentException("The artifact byte limit is invalid.");
        var staging = _staging ?? throw new ArgumentException("Default staging is not configured.");
        var connection = new DatabaseConnectionSettings(new DatabaseEndpoint(configuration.Host, configuration.Port),
            new DatabaseConnectionOptions(requireEncryption: configuration.Encrypt, trustServerCertificate: configuration.TrustServerCertificate));
        ArgumentNullException.ThrowIfNull(configuration.StoragePrefix);
        var scope = ManagedBackupLayout.AttemptScope(task.TenantId, task.JobId, task.RunId, task.AttemptId);
        var prefix = new StorageObjectKey(configuration.StoragePrefix.Length == 0 ? scope : $"{configuration.StoragePrefix}/{scope}");
        var options = new FullBackupOptions(configuration.CopyOnly ? BackupOptionMode.Required : BackupOptionMode.Off,
            ParsePolicy(configuration.ChecksumPolicy), ParsePolicy(configuration.CompressionPolicy));
        var credential = configuration.IntegratedSecurity
            ? IntegratedDatabaseCredential.Instance
            : await ResolveCredentialAsync(configuration.DatabaseCredentialReference!, cancellationToken).ConfigureAwait(false);
        if (configuration.Engine == "postgres" && credential is not PasswordDatabaseCredential) throw new BackupTaskCredentialException();
        var targets = configuration.Destinations.Select(destination => new BackupPipelineDestination(
            new StorageProviderId(destination.StorageProvider),
            destination.StorageProvider is "sftp" or "google-drive"
                ? (storageFactory ?? throw new ArgumentException("Backend-relayed storage is not configured.")).CreateBackup(task, destination)
                : destination.StorageProvider == "local" && destination.StorageTarget is { } volume
                    ? volumes?.Resolve(volume) ?? throw new ArgumentException("The local storage volume is not configured on this agent.")
                    : null,
            destination.StorageConnectionId,
            destination.IsPrimary)).ToArray();
        var primary = targets.Single(target => target.IsPrimary);
        var engineId = new DatabaseEngineId(configuration.Engine);
        return new DatabaseBackupPipelineRequest(engineId, connection, credential, configuration.Database,
            staging, primary.Storage, prefix, options)
        {
            StorageOverride = primary.StorageOverride,
            MaxArtifactBytes = task.MaxArtifactBytes,
            Destinations = targets,
            UploadBandwidth = uploadBandwidth?.Invoke(task),
            RequiredVerifiedReplicas = Math.Max(1, configuration.RequiredVerifiedReplicaCount),
            Encryption = task.Encryption is { } encryption ? ToArtifactEncryption(encryption) : null
        };
    }

    private const int MinWrapKeyBits = 3072;

    private static ArtifactEncryption ToArtifactEncryption(BackupEncryptionInstruction instruction)
    {
        if (instruction.KeyVersion < 1)
        {
            throw new BackupTaskEncryptionException();
        }

        var key = RSA.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(instruction.PublicKeySpkiBase64), out _);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            key.Dispose();
            throw new BackupTaskEncryptionException();
        }

        if (key.KeySize < MinWrapKeyBits)
        {
            key.Dispose();
            throw new BackupTaskEncryptionException();
        }

        return new ArtifactEncryption(instruction.Required, [new BpeRecipient(BpeRecipientType.TenantWrap, instruction.KeyVersion, key)]);
    }

    private async ValueTask<DatabaseCredential> ResolveCredentialAsync(string reference, CancellationToken cancellationToken)
    {
        try
        {
            return await credentials.ResolveAsync(reference, cancellationToken).ConfigureAwait(false)
                ?? throw new BackupTaskCredentialException();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Never retain resolver diagnostics: they can contain connection strings or secret values.
            throw new BackupTaskCredentialException();
        }
    }

    private static void ValidateConfiguration(BackupExecutionConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Engine is not ("mssql" or "postgres") || configuration.StagingReference != "default"
            || string.IsNullOrWhiteSpace(configuration.Database) || configuration.Database.Length > 128
            || configuration.Database.Any(char.IsControl) || configuration.Host is null || configuration.Host.Length > 253
            || configuration.Engine == "postgres" && (configuration.Database != configuration.Database.Trim() || System.Text.Encoding.UTF8.GetByteCount(configuration.Database) > 63 || configuration.IntegratedSecurity))
        {
            throw new ArgumentException("The backup configuration is unsupported.");
        }
        var destinations = configuration.Destinations ?? throw new ArgumentException("The destination set is invalid.");
        if (destinations.Count(destination => destination.IsPrimary) != 1
            || configuration.RequiredVerifiedReplicaCount < 1 || configuration.RequiredVerifiedReplicaCount > destinations.Count
            || destinations.Select(destination => (destination.StorageProvider, destination.StorageConnectionId)).Distinct().Count() != destinations.Count)
        {
            throw new ArgumentException("The destination set is invalid.");
        }
        // An unsupported provider/location combination is refused here, explicitly: no destination is ever skipped silently.
        foreach (var destination in destinations)
        {
            if (destination.StorageProvider is not ("local" or "central" or "sftp" or "google-drive"))
            {
                throw new ArgumentException("The destination storage provider is unsupported.");
            }
            if (destination.StorageTarget is not null && (destination.StorageProvider != "local" || !LocalVolumes.IsValidName(destination.StorageTarget)))
            {
                throw new ArgumentException("The destination storage target is invalid.");
            }
            if (destination.StorageExecutionLocation is not ("Backend" or "Agent"))
            {
                throw new ArgumentException("The destination storage execution location is unsupported.");
            }
            if ((destination.StorageProvider is "sftp" or "google-drive") && (destination.StorageExecutionLocation != "Backend"
                || destination.StorageConnectionId is null || destination.StorageConnectionId == Guid.Empty || configuration.StoragePrefix != string.Empty))
            {
                throw new ArgumentException("The backend-relayed storage configuration is invalid.");
            }
        }
        if (!HasValidAuthentication(configuration.IntegratedSecurity, configuration.DatabaseCredentialReference))
        {
            throw new ArgumentException("Authentication requires either integrated security or a named local credential.");
        }
    }

    /// <summary>
    /// The one rule for how a task authenticates, shared by every task that connects to a database: integrated security carries no credential
    /// reference, anything else carries exactly one short name (letters, digits, '_' and '-') that the agent resolves locally.
    /// </summary>
    internal static bool HasValidAuthentication(bool integratedSecurity, string? credentialReference) =>
        integratedSecurity
            ? credentialReference is null
            : !string.IsNullOrWhiteSpace(credentialReference)
                && credentialReference.Length <= 64
                && credentialReference.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static BackupOptionMode ParsePolicy(string value) => value switch
    {
        "Disabled" => BackupOptionMode.Off,
        "Preferred" => BackupOptionMode.IfSupported,
        "Required" => BackupOptionMode.Required,
        _ => throw new ArgumentException("The backup option policy is unsupported.")
    };
}

internal sealed class BackupTaskCredentialException : Exception
{
}

/// <summary>The task's encryption instruction cannot be honoured; reported as invalid configuration so an encrypted job never silently produces a plaintext artifact.</summary>
internal sealed class BackupTaskEncryptionException : Exception
{
}
