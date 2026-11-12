using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Identity;

/// <summary>Explicit one-time provisioning, never automatic seeding or a public registration endpoint.</summary>
/// <remarks>
/// Run the API with --bootstrap-user only after applying migrations, supplying credentials externally.
/// No default credentials exist. Existing credentials are never overwritten.
/// The transaction lock serializes concurrent bootstrap commands; subsequent user management is out of scope.
/// </remarks>
public static class UserBootstrapper
{
    public static async Task<UserId> CreateInitialUserAsync(
        DbContextOptions<PlatformDbContext> options, string loginIdentifier, string displayName,
        string email, string password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (password.Length is < 12 or > 1024)
        {
            throw new ArgumentException("The bootstrap password must contain 12 to 1024 characters.", nameof(password));
        }
        var userId = UserId.New();
        var user = new User(userId, displayName, UserRole.PlatformAdmin);
        var hasher = new PasswordHashService();
        var credential = new PasswordCredential(userId, loginIdentifier, hasher.Hash(userId, password), email);
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(731429850126)", cancellationToken);
        if (await context.Users.AnyAsync(user => user.Role == UserRole.PlatformAdmin, cancellationToken))
        {
            throw new InvalidOperationException("Bootstrap is only available when no platform administrator exists.");
        }
        context.Users.Add(user);
        context.PasswordCredentials.Add(credential);
        context.AuditEvents.Add(AuditEvent.Record(DateTimeOffset.UtcNow, UserId.System, AuditActorKind.System,
            "user.bootstrap", null, "user", userId.Value.ToString("D")));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return userId;
    }
}
