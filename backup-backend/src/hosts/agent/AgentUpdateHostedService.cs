using System.Runtime.InteropServices;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Core.Updates;
using BackupPlatform.Contracts.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Host;

public sealed record AgentUpdateSettings
{
    public required string Root { get; init; }
    public required string Staging { get; init; }
    public required string Store { get; init; }
    public required Dictionary<string, string> TrustedKeys { get; init; }
    public string? RuntimeConfiguration { get; init; }
}

public static class AgentAutoUpdateRegistration
{
    public static IServiceCollection AddAgentAutoUpdates(this IServiceCollection services, IConfiguration configuration)
    {
        var enabled = configuration["Agent:Updates:SettingsFile"];
        services.AddSingleton(_ =>
        {
            var gate = new AgentUpdateGate();
            if (!string.IsNullOrEmpty(enabled)) gate.TryBeginUpdate();
            return gate;
        });
        if (enabled is { Length: > 0 })
        {
            services.AddSingleton(AgentUpdateCommands.ReadSettings(enabled));
            services.AddHostedService<AgentUpdateHostedService>();
        }
        return services;
    }
}

internal sealed class AgentUpdateHostedService(IAgentUpdateClient client, IAgentCredentialStore identities, AgentUpdateGate gate,
    AgentUpdateSettings settings, AgentSoftwareInfo software, IHostApplicationLifetime lifetime, ILogger<AgentUpdateHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
        if (OperatingSystem.IsLinux()) AgentReleaseActivation.ConfirmLinux(settings.Root, InstalledVersion());
        else
        {
            File.WriteAllText(Path.Combine(settings.Staging, "healthy"), InstalledVersion());
            while (File.Exists(Path.Combine(settings.Root, "update-control", "activation")) || File.Exists(Path.Combine(settings.Root, "update-control", "pending.json")))
                await Task.Delay(250, stoppingToken).ConfigureAwait(false);
        }
        gate.CancelUpdate();
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try { if (await CheckAsync(stoppingToken).ConfigureAwait(false)) return; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogWarning("Agent update deferred ({ExceptionType}).", exception.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private string InstalledVersion()
    {
        var directory = Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        return AgentUpdateService.CanonicalVersion(directory, out _) ? directory : software.Version;
    }

    private async Task<bool> CheckAsync(CancellationToken token)
    {
        var platform = (OperatingSystem.IsWindows(), RuntimeInformation.ProcessArchitecture) switch
        {
            (true, Architecture.X64) => "win-x64",
            (false, Architecture.X64) when OperatingSystem.IsLinux() => "linux-x64",
            (false, Architecture.Arm64) when OperatingSystem.IsLinux() => "linux-arm64",
            _ => throw new PlatformNotSupportedException("Unsupported auto-update RID.")
        };
        var identity = await identities.LoadAsync(token).ConfigureAwait(false);
        if (identity is null) return false;
        var response = await client.GetManifestAsync(identity, platform, token).ConfigureAwait(false);
        if (!response.IsSuccess) return false;
        var manifest = response.Value;
        var target = AgentUpdateService.ValidateManifest(manifest, platform, settings.TrustedKeys);
        if (!AgentUpdateService.CanonicalVersion(InstalledVersion(), out var current)
            || !AgentUpdatePolicy.IsEligible(current, target, Version.Parse(manifest.Payload.MinimumSupportedVersion), out _)) return false;
        if (!gate.TryBeginUpdate()) return false;
        var restart = false;
        try
        {
            AgentUpdateService.AssertNoLinks(settings.Staging);
            Directory.CreateDirectory(settings.Staging);
            var package = Path.Combine(settings.Staging, "package");
            File.Delete(package);
            var downloaded = await client.DownloadAsync(identity, manifest, package, settings.TrustedKeys, token).ConfigureAwait(false);
            if (!downloaded.IsSuccess) return false;
            if (OperatingSystem.IsLinux())
            {
                await new AgentUpdateService().StageAsync(package, Path.Combine(settings.Root, "releases"), manifest, platform, settings.TrustedKeys, token).ConfigureAwait(false);
                AgentReleaseActivation.BeginLinux(settings.Root, manifest.Payload.Version);
                restart = true;
                lifetime.StopApplication();
            }
            else
            {
                var temporary = Path.Combine(settings.Staging, ".manifest-" + Guid.NewGuid().ToString("N"));
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest), token).ConfigureAwait(false);
                File.Move(temporary, Path.Combine(settings.Staging, "manifest.json"), true);
                File.Delete(Path.Combine(settings.Staging, "healthy"));
                await AgentUpdateCommands.RunProcessAsync("schtasks.exe", ["/Run", "/TN", "BackupAgentUpdate"], token).ConfigureAwait(false);
                // The fixed SYSTEM task waits for our identity lease to close before touching SCM.
                restart = true;
                lifetime.StopApplication();
            }
            return true;
        }
        finally { if (!restart) gate.CancelUpdate(); }
    }
}
