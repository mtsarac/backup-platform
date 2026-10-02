using BackupPlatform.Infrastructure.Identity;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BackupPlatform.Api.Security;

/// <summary>One-time CLI provisioning using externally supplied credentials; never exposed over HTTP.</summary>
internal static class BootstrapCommand
{
    public static async Task ExecuteAsync(IServiceProvider services)
    {
        var login = RequireEnvironment("BACKUP_PLATFORM_BOOTSTRAP_LOGIN");
        var displayName = RequireEnvironment("BACKUP_PLATFORM_BOOTSTRAP_DISPLAY_NAME");
        var password = RequireEnvironment("BACKUP_PLATFORM_BOOTSTRAP_PASSWORD");
        var options = services.GetRequiredService<DbContextOptions<PlatformDbContext>>();
        var userId = await UserBootstrapper.CreateInitialUserAsync(options, login, displayName, password, CancellationToken.None);
        Console.WriteLine($"Initial platform administrator created: {userId}.");
    }

    private static string RequireEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Set {name} externally for the explicit bootstrap command.");
        }
        return value;
    }
}
