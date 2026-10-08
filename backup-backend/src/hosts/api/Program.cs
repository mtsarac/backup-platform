using BackupPlatform.Api.Endpoints;
using BackupPlatform.Api.Push;
using BackupPlatform.Api.Security;
using BackupPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var isBootstrap = args.Contains("--bootstrap-user", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(isBootstrap ? args.Where(argument => argument != "--bootstrap-user").ToArray() : args);
builder.Services.AddPlatformSecurity();

var app = builder.Build();
_ = app.Services.GetRequiredService<DbContextOptions<PlatformDbContext>>();
_ = app.Services.GetRequiredService<BackupPlatform.Agents.Connectivity.AgentConnectivityOptions>();
_ = app.Services.GetRequiredService<BackupPlatform.Application.Backups.BackupExecutionOptions>();
_ = app.Services.GetRequiredService<BackupPlatform.Application.Backups.BackupSchedulingOptions>();
_ = app.Services.GetRequiredService<BackupPlatform.Application.Restores.RestoreExecutionOptions>();

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
app.MapAgentAdministrationEndpoints();
app.MapPlatformEndpoints();
app.MapManagedServicePlatformEndpoints();
app.MapQuotaEndpoints();
app.MapBackupEndpoints();
app.MapBackupArtifactEndpoints();
app.MapBackupTaskEndpoints();
app.MapRestoreEndpoints();
app.MapRestoreTaskEndpoints();
app.MapAgentHub();

app.Run();

public partial class Program { }
