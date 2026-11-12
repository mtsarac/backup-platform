using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;

namespace BackupPlatform.Tenancy;

/// <summary>Machine-readable validation failure naming the offending field; never carries the rejected value.</summary>
public sealed class InvalidPlatformRequestException(string field, string code) : Exception($"Invalid platform request field '{field}' ({code}).")
{
    public string Field { get; } = field;
    public string Code { get; } = code;
}

/// <summary>
/// Validated platform administration writes. Authorization is the caller's responsibility: only a PlatformAdmin may
/// reach these operations, independently of any tenant membership. Every write is audited by the store, with the
/// actor recorded as a platform administrator.
/// </summary>
public sealed class PlatformAdministrationService(IPlatformAdministrationStore store, PasswordHashService passwordHasher, TimeProvider timeProvider)
{
    public const int MaximumNameLength = 200;
    public const int MinimumPasswordLength = 12;
    public const int MaximumPasswordLength = 1024;

    public Task<PlatformTenantItem> CreateTenantAsync(string? name, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return store.CreateTenantAsync(ValidName(name, "name"), actor, timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<PlatformChange<PlatformTenantItem>> RenameTenantAsync(TenantId tenantId, string? name, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(actor);
        return store.RenameTenantAsync(tenantId, ValidName(name, "name"), actor, timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<PlatformChange<PlatformUserItem>> CreateUserAsync(
        string? username, string? email, string? displayName, string? password, string? role, Guid[]? tenantIds, UserId actor, CancellationToken cancellationToken)
    {
        var (name, login, address, accountRole) = ValidateAccount(displayName, username, email, role);
        if (!PasswordPolicy.Validate(password, out var failureReason))
        {
            throw new InvalidPlatformRequestException("password", failureReason ?? "invalid_value");
        }
        if (tenantIds is null || tenantIds.Any(id => id == Guid.Empty) || tenantIds.Distinct().Count() != tenantIds.Length)
        {
            throw new InvalidPlatformRequestException("tenantIds", "invalid_value");
        }
        if (accountRole == UserRole.UserAdmin && tenantIds.Length == 0)
        {
            throw new InvalidPlatformRequestException("tenantIds", "membership_required");
        }
        if (accountRole == UserRole.PlatformAdmin && tenantIds.Length != 0)
        {
            throw new InvalidPlatformRequestException("tenantIds", "membership_not_allowed");
        }
        var userId = UserId.New();
        return store.CreateUserAsync(userId, name, login, address, passwordHasher.Hash(userId, password!), accountRole,
            tenantIds.Select(id => new TenantId(id)).ToArray(), actor, timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<PlatformChange<PlatformUserItem>> UpdateUserAsync(
        UserId userId, string? username, string? email, string? displayName, string? role, UserId actor, CancellationToken cancellationToken)
    {
        var (name, login, address, accountRole) = ValidateAccount(displayName, username, email, role);
        return store.UpdateUserAsync(userId, name, login, address, accountRole, actor, timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<PlatformChange<PlatformUserItem>> DisableUserAsync(UserId userId, string? reason, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(actor);
        return store.DisableUserAsync(userId, reason?.Trim(), actor, timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<PlatformChange<PlatformUserItem>> EnableUserAsync(UserId userId, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(actor);
        return store.EnableUserAsync(userId, actor, timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<PlatformChange<PlatformUserItem>> AssignTemporaryPasswordAsync(
        UserId targetUserId, string? temporaryPassword, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetUserId);
        ArgumentNullException.ThrowIfNull(actor);
        if (!PasswordPolicy.Validate(temporaryPassword, out var failureReason))
        {
            throw new InvalidPlatformRequestException("temporaryPassword", failureReason ?? "invalid_value");
        }
        return store.AssignTemporaryPasswordAsync(targetUserId, passwordHasher.Hash(targetUserId, temporaryPassword!), actor, timeProvider.GetUtcNow(), cancellationToken);
    }

    private static (string Name, string Username, string Email, UserRole Role) ValidateAccount(string? displayName, string? username, string? email, string? role)
    {
        var name = ValidName(displayName, "displayName");
        try { LoginIdentifierNormalizer.Normalize(username!); }
        catch (ArgumentException) { throw new InvalidPlatformRequestException("username", "invalid_value"); }
        try { LoginIdentifierNormalizer.NormalizeEmail(email!); }
        catch (ArgumentException) { throw new InvalidPlatformRequestException("email", "invalid_value"); }
        if (!Enum.TryParse<UserRole>(role, out var parsedRole) || !Enum.IsDefined(parsedRole) || role != parsedRole.ToString())
        {
            throw new InvalidPlatformRequestException("role", "invalid_value");
        }
        return (name, username!.Trim(), email!.Trim(), parsedRole);
    }

    public Task<PlatformChange<PlatformMembershipItem>> AddMembershipAsync(
        TenantId tenantId, UserId userId, UserId actor, CancellationToken cancellationToken) =>
        store.AddMembershipAsync(tenantId, userId, actor, timeProvider.GetUtcNow(), cancellationToken);

    public Task<PlatformChange<PlatformMembershipItem>> RemoveMembershipAsync(
        TenantId tenantId, UserId userId, UserId actor, CancellationToken cancellationToken) =>
        store.RemoveMembershipAsync(tenantId, userId, actor, timeProvider.GetUtcNow(), cancellationToken);

    private static string ValidName(string? value, string field)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new InvalidPlatformRequestException(field, "required");
        }
        if (trimmed.Length > MaximumNameLength)
        {
            throw new InvalidPlatformRequestException(field, "too_long");
        }
        return trimmed;
    }

}
