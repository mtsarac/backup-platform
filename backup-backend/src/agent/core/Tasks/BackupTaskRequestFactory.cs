using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>Resolves only host-registered staging and local credential names; task input can never choose a staging path.</summary>
public sealed class BackupTaskRequestFactory(IBackupTaskCredentialResolver credentials, IEnumerable<StagingArea> stagingAreas,
    ITaskStorageProviderFactory? storageFactory = null)
{
    private readonly StagingArea? _staging = stagingAreas.SingleOrDefault();

    public async ValueTask<DatabaseBackupPipelineRequest> CreateAsync(BackupTaskSnapshot task, CancellationToken cancellationToken)
    {
        var configuration = task.Configuration;
        ValidateConfiguration(configuration);
        var staging = _staging ?? throw new ArgumentException("Default staging is not configured.");
        var connection = new DatabaseConnectionSettings(new DatabaseEndpoint(configuration.Host, configuration.Port),
            new DatabaseConnectionOptions(requireEncryption: configuration.Encrypt, trustServerCertificate: configuration.TrustServerCertificate));
        var storage = new StorageProviderId(configuration.StorageProvider);
        ArgumentNullException.ThrowIfNull(configuration.StoragePrefix);
        var scope = ManagedBackupLayout.AttemptScope(task.TenantId, task.JobId, task.RunId, task.AttemptId);
        var prefix = new StorageObjectKey(configuration.StoragePrefix.Length == 0 ? scope : $"{configuration.StoragePrefix}/{scope}");
        var options = new FullBackupOptions(configuration.CopyOnly ? BackupOptionMode.Required : BackupOptionMode.Off,
            ParsePolicy(configuration.ChecksumPolicy), ParsePolicy(configuration.CompressionPolicy));
        var credential = configuration.IntegratedSecurity
            ? IntegratedDatabaseCredential.Instance
            : await ResolveCredentialAsync(configuration.DatabaseCredentialReference!, cancellationToken).ConfigureAwait(false);
        return new DatabaseBackupPipelineRequest(DatabaseEngineId.MsSql, connection, credential, configuration.Database,
            staging, storage, prefix, options)
        {
            StorageOverride = configuration.StorageProvider == "sftp"
                ? (storageFactory ?? throw new ArgumentException("Backend-relayed storage is not configured.")).CreateBackup(task)
                : null
        };
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
        if (configuration.Engine != "mssql" || configuration.StagingReference != "default"
            || string.IsNullOrWhiteSpace(configuration.Database) || configuration.Database.Length > 128
            || configuration.Database.Any(char.IsControl) || configuration.Host is null || configuration.Host.Length > 253)
        {
            throw new ArgumentException("The backup configuration is unsupported.");
        }
        if (configuration.StorageProvider == "sftp" && (configuration.StorageExecutionLocation != "Backend"
            || configuration.StorageConnectionId is null || configuration.StorageConnectionId == Guid.Empty || configuration.StoragePrefix != string.Empty))
        {
            throw new ArgumentException("The backend-relayed storage configuration is invalid.");
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
