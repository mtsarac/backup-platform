using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.Agent.Host;

/// <summary>
/// Fetches the login for one operation from the backend, which only answers for references minted for this agent. Nothing is cached or written down:
/// a changed or removed login takes effect on the next operation, and a stolen agent disk holds no database password.
/// </summary>
internal sealed class BackupTaskCredentialResolver(IDatabaseCredentialClient client, IAgentCredentialStore identities) : IBackupTaskCredentialResolver
{
    public async ValueTask<DatabaseCredential> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        var identity = await identities.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The agent identity is unavailable.");
        var result = await client.ResolveAsync(identity, reference, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is not { } login || string.IsNullOrWhiteSpace(login.UserName) || string.IsNullOrEmpty(login.Password))
        {
            throw new InvalidOperationException("The referenced database credential is unavailable.");
        }
        return new PasswordDatabaseCredential(login.UserName, new DatabaseSecret(login.Password));
    }
}
