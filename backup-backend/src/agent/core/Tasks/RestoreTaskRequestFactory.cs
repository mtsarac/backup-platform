using BackupPlatform.Agent.Core.Restore;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.StorageProviders.Abstractions;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>
/// Turns a claimed restore snapshot into a pipeline request. Resolves only host-registered staging and local credential names: task input
/// can never choose a staging path, and it carries no secret. A snapshot this agent cannot act on is refused here, before anything is read
/// or changed, with the failure code the backend will show; nothing about the snapshot's content is retained in the exception.
/// </summary>
public sealed class RestoreTaskRequestFactory(
    IBackupTaskCredentialResolver credentials,
    IEnumerable<StagingArea> stagingAreas,
    ITaskStorageProviderFactory? storageFactory = null)
{
    private const int MaxDatabaseNameLength = 128;
    private const int MaxHostLength = 253;

    private readonly StagingArea? _staging = stagingAreas.SingleOrDefault();

    public async ValueTask<DatabaseRestorePipelineRequest> CreateAsync(RestoreTaskSnapshot task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Purpose is not ("Execution" or "Precheck"))
        {
            throw new RestoreTaskRequestException(RestoreAgentFailureCodes.InvalidConfiguration);
        }
        var staging = ValidateAndResolveStaging(task);
        var mode = ParseMode(task.Mode);

        DatabaseConnectionSettings connection;
        StorageProviderId storage;
        StorageObjectKey key;
        ContentChecksum checksum;
        try
        {
            connection = new DatabaseConnectionSettings(new DatabaseEndpoint(task.Target.Host, task.Target.Port),
                new DatabaseConnectionOptions(requireEncryption: task.Target.Encrypt, trustServerCertificate: task.Target.TrustServerCertificate));
            storage = new StorageProviderId(task.Source.StorageProvider);
            key = new StorageObjectKey(task.Source.StorageKey);
            checksum = new ContentChecksum(ChecksumAlgorithm.Sha256, task.Source.Checksum);
        }
        catch (ArgumentException)
        {
            throw new RestoreTaskRequestException(RestoreAgentFailureCodes.InvalidConfiguration);
        }

        var credential = task.Target.IntegratedSecurity
            ? IntegratedDatabaseCredential.Instance
            : await ResolveCredentialAsync(task.Target.DatabaseCredentialReference!, cancellationToken).ConfigureAwait(false);
        if (task.Engine == "postgres" && credential is not PasswordDatabaseCredential)
            throw new RestoreTaskRequestException(RestoreAgentFailureCodes.InvalidConfiguration);
        try
        {
            return new DatabaseRestorePipelineRequest(
                new DatabaseEngineId(task.Engine), connection, credential, task.DatabaseName, mode, staging, storage, key, task.TenantId,
                task.Source.SizeBytes, checksum,
                task.Options.TerminateActiveConnections, task.Options.VerifyChecksum, task.Options.VerifyBackup,
                purpose: task.Purpose == "Precheck" ? RestorePipelinePurpose.Precheck : RestorePipelinePurpose.Execution)
            {
                StorageOverride = task.Source.Access == "BackendRelayed" ? storageFactory!.CreateRestore(task) : null
            };
        }
        catch (ArgumentException)
        {
            throw new RestoreTaskRequestException(RestoreAgentFailureCodes.InvalidConfiguration);
        }
    }

    public async ValueTask<RestoreDiscoveryRequest> CreateDiscoveryAsync(RestoreTaskSnapshot task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Purpose != "Discovery" || !HasValidIdentityAndTarget(task))
        {
            throw new RestoreTaskRequestException(RestoreAgentFailureCodes.InvalidConfiguration);
        }

        DatabaseConnectionSettings connection;
        try
        {
            connection = new DatabaseConnectionSettings(new DatabaseEndpoint(task.Target.Host, task.Target.Port),
                new DatabaseConnectionOptions(requireEncryption: task.Target.Encrypt, trustServerCertificate: task.Target.TrustServerCertificate));
        }
        catch (ArgumentException)
        {
            throw new RestoreTaskRequestException(RestoreAgentFailureCodes.InvalidConfiguration);
        }

        var credential = task.Target.IntegratedSecurity
            ? IntegratedDatabaseCredential.Instance
            : await ResolveCredentialAsync(task.Target.DatabaseCredentialReference!, cancellationToken).ConfigureAwait(false);
        if (task.Engine == "postgres" && credential is not PasswordDatabaseCredential)
            throw new RestoreTaskRequestException(RestoreAgentFailureCodes.InvalidConfiguration);
        return new RestoreDiscoveryRequest(new DatabaseEngineId(task.Engine), connection, credential);
    }

    private static bool HasValidIdentityAndTarget(RestoreTaskSnapshot task) =>
        task.Version == 1 && task.OperationId != Guid.Empty && task.AttemptId != Guid.Empty && task.TenantId != Guid.Empty
        && task.Engine is ("mssql" or "postgres")
        && task.LeaseToken != Guid.Empty && task.Target is not null
        && (task.Engine != "postgres" || !task.Target.IntegratedSecurity)
        && !string.IsNullOrWhiteSpace(task.Target.Host) && task.Target.Host.Length <= MaxHostLength
        && !task.Target.Host.Any(char.IsControl)
        && BackupTaskRequestFactory.HasValidAuthentication(task.Target.IntegratedSecurity, task.Target.DatabaseCredentialReference);

    private StagingArea ValidateAndResolveStaging(RestoreTaskSnapshot task)
    {
        if (!HasValidIdentityAndTarget(task)
            || task.ArtifactId == Guid.Empty || task.ReplicaId == Guid.Empty
            || task.Source is null || task.Options is null
            || string.IsNullOrWhiteSpace(task.DatabaseName) || task.DatabaseName.Length > MaxDatabaseNameLength || task.DatabaseName.Any(char.IsControl)
            || task.Engine == "postgres" && System.Text.Encoding.UTF8.GetByteCount(task.DatabaseName) > 63
            || !HasValidSource(task.Source))
        {
            throw new RestoreTaskRequestException(RestoreAgentFailureCodes.InvalidConfiguration);
        }

        if (task.StagingReference != "default")
        {
            throw new RestoreTaskRequestException(RestoreAgentFailureCodes.InvalidConfiguration);
        }

        return _staging ?? throw new RestoreTaskRequestException(RestoreAgentFailureCodes.StagingInvalid);
    }

    /// <summary>
    /// The replica must match its access path: local disk, mounted central storage, or backend-relayed SFTP.
    /// Contradictory pairings and a relay without its provider factory are refused rather than guessed.
    /// </summary>
    private bool HasValidSource(RestoreTaskSource source) =>
        source.SizeBytes > 0
        && string.Equals(source.ChecksumAlgorithm, "sha256", StringComparison.Ordinal)
        && ((source.Access, source.StorageProvider, source.StorageExecutionLocation) is
            ("AgentLocal", "local", "Agent") or ("CentralStorage", "central", "Backend")
            || (source.Access, source.StorageProvider, source.StorageExecutionLocation) is ("BackendRelayed", "sftp" or "google-drive", "Backend")
            && Guid.TryParse(source.StorageTarget, out var connectionId) && connectionId != Guid.Empty
            && storageFactory is not null);

    private static DatabaseRestoreMode ParseMode(string mode) => mode switch
    {
        "NewDatabase" => DatabaseRestoreMode.NewDatabase,
        "ReplaceExistingDatabase" => DatabaseRestoreMode.ReplaceExistingDatabase,
        _ => throw new RestoreTaskRequestException(RestoreAgentFailureCodes.InvalidConfiguration)
    };

    private async ValueTask<DatabaseCredential> ResolveCredentialAsync(string reference, CancellationToken cancellationToken)
    {
        try
        {
            return await credentials.ResolveAsync(reference, cancellationToken).ConfigureAwait(false)
                ?? throw new RestoreTaskRequestException(RestoreAgentFailureCodes.InvalidConfiguration);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Never retain resolver diagnostics: they can contain connection strings or secret values.
            throw new RestoreTaskRequestException(RestoreAgentFailureCodes.InvalidConfiguration);
        }
    }
}

/// <summary>A restore snapshot that cannot be turned into a request. <see cref="FailureCode"/> is the only information it carries, on purpose.</summary>
internal sealed class RestoreTaskRequestException(string failureCode) : Exception("The restore task cannot be executed by this agent.")
{
    public string FailureCode { get; } = failureCode;
}
