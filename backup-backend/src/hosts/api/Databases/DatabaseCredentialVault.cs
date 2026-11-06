using System.Text.Json;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.SecretProviders.Abstractions;

namespace BackupPlatform.Api.Databases;

/// <summary>Why a database login could not be turned into a reference; the endpoint maps it to the request field the caller has to fix.</summary>
public sealed class DatabaseLoginRejectedException() : ArgumentException("The database login is invalid or missing.");

/// <summary>
/// Database logins typed in the UI, encrypted through <see cref="ISecretStore"/>. Every login gets its own platform-minted reference that embeds the agent
/// it was entered for: two instances (or two jobs) on one host never share a login by name, and an agent can only ever resolve references minted for
/// itself, so an agent of another tenant (or another agent of the same tenant) gets nothing even if it learns a reference.
/// </summary>
public sealed class DatabaseCredentialVault(ISecretStore secrets, BackupPlatform.Application.Backups.IJobCredentialClaims claims, TimeProvider time)
{
    private const int MaxUserName = 128;
    private const int MaxPassword = 1024;

    public static bool BelongsTo(string? reference, Guid agentId) =>
        reference is { Length: 49 } && reference.StartsWith($"dbc-{agentId:N}-", StringComparison.Ordinal) && reference[37..].All(char.IsAsciiHexDigitLower);

    /// <exception cref="DatabaseLoginRejectedException">The user name or password is empty, too long or contains control characters.</exception>
    public async Task<string> StoreAsync(Guid agentId, DatabaseLoginInput login, CancellationToken cancellationToken, string? referenceSeed = null)
    {
        if (string.IsNullOrWhiteSpace(login.UserName) || login.UserName.Length > MaxUserName || login.UserName.Any(char.IsControl)
            || string.IsNullOrEmpty(login.Password) || login.Password.Length > MaxPassword)
            throw new DatabaseLoginRejectedException();
        var reference = referenceSeed is null ? NewReference(agentId) : SeededReference(agentId, referenceSeed);
        // Claimed BEFORE the secret exists: a crash after the write leaves a claim the sweep can act on, never an unrecorded credential.
        if (referenceSeed is not null) await claims.ClaimAsync(reference, time.GetUtcNow(), cancellationToken);
        var payload = JsonSerializer.Serialize(new DatabaseCredentialResponse { UserName = login.UserName.Trim(), Password = login.Password });
        await secrets.SetAsync(new SecretReference(reference), new SecretValue(payload), cancellationToken);
        return reference;
    }

    /// <summary>A restore target may only name a login of the agent that runs it: references are platform-minted, so free text or another agent's reference is refused.</summary>
    public bool AcceptsTarget(Guid agentId, BackupPlatform.Application.Restores.RestoreTargetRequest? target) =>
        target is null || target.IntegratedSecurity || BelongsTo(target.DatabaseCredentialReference, agentId);

    /// <summary>
    /// The reference a target should store: null for integrated security, a freshly minted one for a typed login, otherwise the caller's
    /// <paramref name="requested"/> reference, which must already belong to <paramref name="agentId"/> (a job prefilled from discovery, a restore reusing a job's login).
    /// A reference is never accepted as free text.
    /// </summary>
    /// <exception cref="DatabaseLoginRejectedException">A password login has neither a usable typed login nor one of this agent's references.</exception>
    public async Task<string?> PrepareAsync(Guid agentId, bool integratedSecurity, DatabaseLoginInput? login, string? requested, CancellationToken cancellationToken, string? referenceSeed = null)
    {
        if (integratedSecurity) return null;
        if (login is not null) return await StoreAsync(agentId, login, cancellationToken, referenceSeed);
        return BelongsTo(requested, agentId) ? requested : throw new DatabaseLoginRejectedException();
    }

    /// <summary>Null unless <paramref name="reference"/> was minted for <paramref name="agentId"/> and still exists.</summary>
    public async Task<DatabaseCredentialResponse?> ResolveAsync(Guid agentId, string? reference, CancellationToken cancellationToken)
    {
        if (!BelongsTo(reference, agentId)) return null;
        try
        {
            var value = await secrets.ResolveAsync(new SecretReference(reference!), cancellationToken);
            return JsonSerializer.Deserialize<DatabaseCredentialResponse>(value.Reveal());
        }
        catch (Exception exception) when (exception is SecretUnavailableException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Same agent and seed, same reference: a retried creation rewrites the one credential it already wrote instead of minting another.</summary>
    private static string SeededReference(Guid agentId, string seed) =>
        $"dbc-{agentId:N}-{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("job-login-ref-v1:" + seed)))[..12]}";

    /// <summary>Removes a credential minted for a request that then created nothing, and its claim.</summary>
    public async Task<bool> DeleteAsync(string reference, CancellationToken cancellationToken)
    {
        var existed = await secrets.DeleteAsync(new SecretReference(reference), cancellationToken);
        await claims.ReleaseAsync(reference, cancellationToken);
        return existed;
    }

    private static string NewReference(Guid agentId) => $"dbc-{agentId:N}-{Guid.NewGuid().ToString("N")[..12]}";
}
