using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>Fetches the database login behind a platform-minted reference from the backend per execution. No credential material is persisted by the worker.</summary>
public interface IBackupTaskCredentialResolver
{
    ValueTask<DatabaseCredential> ResolveAsync(string reference, CancellationToken cancellationToken);
}
