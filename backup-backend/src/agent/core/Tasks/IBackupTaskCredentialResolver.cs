using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.Agent.Core.Tasks;

/// <summary>Resolves a locally configured credential name per execution. No credential material is persisted by the worker.</summary>
public interface IBackupTaskCredentialResolver
{
    ValueTask<DatabaseCredential> ResolveAsync(string reference, CancellationToken cancellationToken);
}
