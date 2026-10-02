using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Tenancy;
using BackupPlatform.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Infrastructure.Identity;

/// <summary>Explicit one-time provisioning, never automatic seeding or a public registration endpoint.</summary>
/// <remarks>
/// Run the API with --bootstrap-user only after applying migrations, supplying the login, display name
/// and password externally. No default credentials exist. Existing credentials are never overwritten.
/// The transaction lock serializes concurrent bootstrap commands; subsequent user management is out of scope.
/// </remarks>
public static class UserBootstrapper
{
    public static async Task<UserId> CreateInitialUserAsync(
        DbContextOptions<PlatformDbContext> options, string loginIdentifier, string displayName,
        string password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (password.Length is < 12 or > 1024)
        {
            throw new ArgumentException("The bootstrap password must contain 12 to 1024 characters.", nameof(password));
        }
        var userId = UserId.New();
        var hasher = new PasswordHashService();
        var credential = new PasswordCredential(userId, loginIdentifier, hasher.Hash(userId, password));
        await using var context = PlatformDbContext.CreateForPlatformOperations(options);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(731429850126)", cancellationToken);
        if (await context.PasswordCredentials.AnyAsync(cancellationToken))
        {
            throw new InvalidOperationException("Bootstrap is only available before the first password credential exists.");
        }
        context.Users.Add(new User(userId, displayName));
        context.PasswordCredentials.Add(credential);
        context.PlatformAdmins.Add(new PlatformAdmin(userId));
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return userId;
    }
}
