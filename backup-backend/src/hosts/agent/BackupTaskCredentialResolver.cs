using System.Text.Json;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.SecretProviders.Abstractions;

namespace BackupPlatform.Agent.Host;

/// <summary>
/// Deployment-supplied secret source: BACKUP_AGENT_SECRET_{reference} contains a JSON object with userName/password.
/// Read at execution time, never copied to appsettings, the task protocol, a database, the identity store or a credential cache.
/// Environment injection belongs to the service manager; integrated authentication requires no such secret.
/// A secret manager can replace ISecretProvider without changing jobs or task execution.
/// </summary>
internal sealed class EnvironmentTaskSecretProvider : ISecretProvider
{
    public ValueTask<SecretValue> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        cancellationToken.ThrowIfCancellationRequested();
        var value = Environment.GetEnvironmentVariable("BACKUP_AGENT_SECRET_" + reference.Name);
        if (string.IsNullOrEmpty(value) || value.Length > 16 * 1024)
        {
            throw new InvalidOperationException("The referenced database credential is unavailable.");
        }
        return ValueTask.FromResult(new SecretValue(value));
    }
}

internal sealed class BackupTaskCredentialResolver(ISecretProvider secrets) : IBackupTaskCredentialResolver
{
    public async ValueTask<DatabaseCredential> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        var secret = await secrets.ResolveAsync(new SecretReference(reference), cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(secret.Reveal());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("userName", out var userName)
                || !root.TryGetProperty("password", out var password)
                || userName.ValueKind != JsonValueKind.String || password.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(userName.GetString()) || string.IsNullOrEmpty(password.GetString()))
            {
                throw new InvalidOperationException("The referenced database credential has an invalid format.");
            }
            return new PasswordDatabaseCredential(userName.GetString()!, new DatabaseSecret(password.GetString()!));
        }
        catch (JsonException)
        {
            // Do not propagate a parser diagnostic or an inner exception containing secret bytes.
            throw new InvalidOperationException("The referenced database credential has an invalid format.");
        }
    }
}
