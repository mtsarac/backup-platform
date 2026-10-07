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
        string? loginIdentifier, string? displayName, string? password, UserId actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var name = ValidName(displayName, "displayName");
        string login;
        try
        {
            login = loginIdentifier is null ? throw new ArgumentException("missing") : LoginIdentifierNormalizer.Normalize(loginIdentifier);
        }
        catch (ArgumentException)
        {
            throw new InvalidPlatformRequestException("loginIdentifier", "invalid_value");
        }
        if (password is null || password.Length is < MinimumPasswordLength or > MaximumPasswordLength)
        {
            throw new InvalidPlatformRequestException("password", "invalid_length");
        }
        var userId = UserId.New();
        // The trimmed original spelling is kept for display; uniqueness is decided on the normalized form.
        return store.CreateUserAsync(userId, name, loginIdentifier!.Trim(), passwordHasher.Hash(userId, password), actor, timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<PlatformChange<PlatformMembershipItem>> AddMembershipAsync(
        TenantId tenantId, UserId userId, TenantRole role, UserId actor, CancellationToken cancellationToken)
    {
        ValidRole(role);
        return store.AddMembershipAsync(tenantId, userId, role, actor, timeProvider.GetUtcNow(), cancellationToken);
    }

    public Task<PlatformChange<PlatformMembershipItem>> ChangeMembershipRoleAsync(
        TenantId tenantId, UserId userId, TenantRole role, UserId actor, CancellationToken cancellationToken)
    {
        ValidRole(role);
        return store.ChangeMembershipRoleAsync(tenantId, userId, role, actor, timeProvider.GetUtcNow(), cancellationToken);
    }

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

    private static void ValidRole(TenantRole role)
    {
        if (!Enum.IsDefined(role))
        {
            throw new InvalidPlatformRequestException("role", "invalid_value");
        }
    }
}
