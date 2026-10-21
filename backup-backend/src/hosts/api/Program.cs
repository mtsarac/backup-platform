using BackupPlatform.Api.Endpoints;
using BackupPlatform.Api.Downloads;
using BackupPlatform.Api.Push;
using BackupPlatform.Api.Support;
using BackupPlatform.Api.Security;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var isBootstrap = args.Contains("--bootstrap-user", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(isBootstrap ? args.Where(argument => argument != "--bootstrap-user").ToArray() : args);
// Hosting's request-start messages include the query string: OAuth callback credentials and the one-click unsubscribe token (NotificationRecipientApiIntegrationTests pins this).
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Services.AddPlatformSecurity();

var app = builder.Build();
_ = app.Services.GetRequiredService<DbContextOptions<PlatformDbContext>>();
_ = app.Services.GetRequiredService<AgentTransferAdmission>();
_ = app.Services.GetRequiredService<BackupPlatform.Notifications.NotificationOptions>();
_ = app.Services.GetRequiredService<BackupPlatform.Agents.Connectivity.AgentConnectivityOptions>();
_ = app.Services.GetRequiredService<BackupPlatform.Application.Backups.BackupExecutionOptions>();
_ = app.Services.GetRequiredService<BackupPlatform.Application.Backups.BackupSchedulingOptions>();
_ = app.Services.GetRequiredService<BackupPlatform.Application.Restores.RestoreExecutionOptions>();
if (app.Services.GetRequiredService<BackupPlatform.Application.Backups.ArtifactRecoveryOptions>().PlatformRecovery is null)
    app.Logger.LogWarning("platform_recovery_missing: Encryption:PlatformRecoveryPublicKeyPem is not configured, so newly encrypted artifacts have no platform recovery recipient.");

if (isBootstrap)
{
    await BootstrapCommand.ExecuteAsync(app.Services);
    await app.DisposeAsync();
    return;
}

app.UsePlatformSecurity();
app.MapAuthenticationEndpoints();
app.MapUserEndpoints();
app.MapAgentEndpoints();
app.MapAgentUpdateEndpoints();
app.MapAgentAdministrationEndpoints();
app.MapDiscoveryEndpoints();
app.MapDatabaseCredentialEndpoints();
app.MapPlatformEndpoints();
app.MapManagedServicePlatformEndpoints();
app.MapQuotaEndpoints();
app.MapStorageConnectionEndpoints();
app.MapNotificationEndpoints();
app.MapNotificationRecipientEndpoints();
app.MapBackupEndpoints();
app.MapBackupHealthEndpoints();
app.MapBackupArtifactEndpoints();
app.MapBackupTaskEndpoints();
app.MapUploadBandwidthEndpoints();
app.MapRestoreEndpoints();
app.MapRestoreTaskEndpoints();
app.MapDownloadEndpoints();
app.MapArtifactEncryptionEndpoints();
app.MapArtifactBreakGlassEndpoints();
app.MapSupportEndpoints();
app.MapSupportTransparencyEndpoints();
app.MapAgentDownloadEndpoints();
app.MapAgentStorageTransferEndpoints();
app.MapAgentDistributionEndpoints();
app.MapAgentHub();

app.Run();

public partial class Program { }
