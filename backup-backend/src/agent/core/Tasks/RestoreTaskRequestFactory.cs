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
public sealed class RestoreTaskRequestFactory(IBackupTaskCredentialResolver credentials, IEnumerable<StagingArea> stagingAreas)
{
    private const int MaxDatabaseNameLength = 128;
    private const int MaxHostLength = 253;

    private readonly StagingArea? _staging = stagingAreas.SingleOrDefault();

    public async ValueTask<DatabaseRestorePipelineRequest> CreateAsync(RestoreTaskSnapshot task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
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
        try
        {
            return new DatabaseRestorePipelineRequest(
                DatabaseEngineId.MsSql, connection, credential, task.DatabaseName, mode, staging, storage, key, task.TenantId,
                task.Source.SizeBytes, checksum,
                task.Options.TerminateActiveConnections, task.Options.VerifyChecksum, task.Options.VerifyBackup);
        }
        catch (ArgumentException)
        {
            throw new RestoreTaskRequestException(RestoreAgentFailureCodes.InvalidConfiguration);
        }
    }

    private StagingArea ValidateAndResolveStaging(RestoreTaskSnapshot task)
    {
        if (task.Version != 1 || task.OperationId == Guid.Empty || task.AttemptId == Guid.Empty || task.TenantId == Guid.Empty
            || task.ArtifactId == Guid.Empty || task.ReplicaId == Guid.Empty || task.LeaseToken == Guid.Empty
            || task.Source is null || task.Target is null || task.Options is null
            || string.IsNullOrWhiteSpace(task.DatabaseName) || task.DatabaseName.Length > MaxDatabaseNameLength || task.DatabaseName.Any(char.IsControl)
            || task.Target.Host is null || task.Target.Host.Length > MaxHostLength
            || !BackupTaskRequestFactory.HasValidAuthentication(task.Target.IntegratedSecurity, task.Target.DatabaseCredentialReference)
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
    /// The replica must be one this agent can actually read as the snapshot describes it: an agent-local replica lives in the agent's own
    /// <c>local</c> storage, a central one in the <c>central</c> storage the backend owns. A pairing that contradicts itself is refused rather than guessed.
    /// </summary>
    private static bool HasValidSource(RestoreTaskSource source) =>
        source.SizeBytes > 0
        && string.Equals(source.ChecksumAlgorithm, "sha256", StringComparison.Ordinal)
        && (source.Access, source.StorageProvider, source.StorageExecutionLocation) is
            ("AgentLocal", "local", "Agent") or ("CentralStorage", "central", "Backend");

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
