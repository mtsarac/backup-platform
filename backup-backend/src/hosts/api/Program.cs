using BackupPlatform.Api.Endpoints;
using BackupPlatform.Api.Flags;
using BackupPlatform.Contracts.Flags;
using BackupPlatform.Api.Downloads;
using BackupPlatform.Api.Push;
using BackupPlatform.Api.Security;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var isBootstrap = args.Contains("--bootstrap-user", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(isBootstrap ? args.Where(argument => argument != "--bootstrap-user").ToArray() : args);
// Hosting's request-start messages include the query string: OAuth callback credentials and the one-click unsubscribe token (NotificationRecipientApiIntegrationTests pins this).
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Services.AddPlatformSecurity();
var featureFlags = new FeatureFlags(key => builder.Configuration.GetValue<bool?>($"Features:{key}"));
builder.Services.AddSingleton(featureFlags);
builder.Services.AddExternalSecrets(builder.Configuration, featureFlags);
builder.Services.AddAgentCertificates(builder.Configuration);
if (builder.Configuration.GetValue<bool>("AgentSecurity:Mtls:Enabled"))
{
    builder.WebHost.ConfigureKestrel(server => server.ConfigureHttpsDefaults(https =>
    {
        https.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.AllowCertificate;
        // AgentAuthentication validates the private CA and live credential generation, not the host trust store.
        https.ClientCertificateValidation = (_, _, _) => true;
    }));
}

var app = builder.Build();
// The hosting startup filter would otherwise process forwarded headers a second time.
app.Configuration["ForwardedHeaders_Enabled"] = "false";
_ = app.Services.GetRequiredService<DbContextOptions<PlatformDbContext>>();
_ = app.Services.GetRequiredService<BackupPlatform.Notifications.NotificationOptions>();
_ = app.Services.GetRequiredService<BackupPlatform.Agents.Connectivity.AgentConnectivityOptions>();
_ = app.Services.GetRequiredService<BackupPlatform.Application.Backups.BackupExecutionOptions>();
_ = app.Services.GetRequiredService<BackupPlatform.Application.Backups.BackupSchedulingOptions>();
_ = app.Services.GetRequiredService<BackupPlatform.Application.Restores.RestoreExecutionOptions>();
_ = app.Services.GetRequiredService<BackupPlatform.Application.Downloads.ShareRevocationOptions>();
if (app.Services.GetRequiredService<BackupPlatform.Application.Backups.ArtifactRecoveryOptions>().PlatformRecovery is null)
    app.Logger.LogWarning("platform_recovery_missing: Encryption:PlatformRecoveryPublicKeyPem is not configured, so newly encrypted artifacts have no platform recovery recipient.");

if (isBootstrap)
{
    await BootstrapCommand.ExecuteAsync(app.Services);
    await app.DisposeAsync();
    return;
}

app.UsePlatformSecurity();
app.MapFeatureEndpoints();
app.MapAuthenticationEndpoints();
app.MapUserEndpoints();
app.MapAgentEndpoints();
app.MapAgentUpdateEndpoints();
app.MapAuditReportingEndpoints();
app.MapAgentAdministrationEndpoints();
app.MapDiscoveryEndpoints();
app.MapDatabaseCredentialEndpoints();
app.MapPlatformEndpoints();
app.MapManagedServicePlatformEndpoints();
app.MapQuotaEndpoints();
app.MapStorageConnectionEndpoints();
app.MapNotificationEndpoints();
app.MapNotificationRecipientEndpoints();
app.MapUserNotificationEndpoints();
app.MapBackupEndpoints();
app.MapBackupHealthEndpoints();
app.MapBackupArtifactEndpoints();
app.MapBackupShareEndpoints();
app.MapBackupTaskEndpoints();
app.MapReplicaRepairTaskEndpoints();
app.MapUploadBandwidthEndpoints();
app.MapRestoreEndpoints();
app.MapRestoreTaskEndpoints();
app.MapDownloadEndpoints();
app.MapArtifactEncryptionEndpoints();
app.MapAgentDownloadEndpoints();
app.MapAgentStorageTransferEndpoints();
app.MapAgentDistributionEndpoints();
app.MapHub<InAppNotificationHub>("/hubs/notifications").RequireAuthorization();
app.MapAgentHub();

app.Run();

public partial class Program { }
