using System.Diagnostics;
using System.Text.Json;
using BackupPlatform.Agent.Core.Updates;
using BackupPlatform.Contracts.Agents;
using Microsoft.Extensions.Configuration;

namespace BackupPlatform.Agent.Host;

/// <summary>Only systemd's supervised loop or the installer's fixed SYSTEM task performs a restart.</summary>
public static class AgentUpdateCommands
{
    public static AgentUpdateSettings ReadSettings(string path)
    {
        AgentUpdateService.AssertNoLinks(path);
        if (new FileInfo(path).Length > 128 * 1024) throw new InvalidDataException("Update settings are too large.");
        var settings = JsonSerializer.Deserialize<AgentUpdateSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid update settings.");
        foreach (var directory in new[] { settings.Root, settings.Staging, settings.Store })
        {
            if (!Path.IsPathFullyQualified(directory)) throw new InvalidDataException("Update directories must be absolute.");
            AgentUpdateService.AssertNoLinks(directory);
        }
        return settings;
    }

    public static void ConfigureRuntime(IConfigurationManager configuration)
    {
        if (configuration["Agent:Updates:SettingsFile"] is not { Length: > 0 } path) return;
        var settings = ReadSettings(path);
        if (settings.RuntimeConfiguration is { Length: > 0 } runtime)
        {
            AgentUpdateService.AssertNoLinks(runtime);
            configuration.AddJsonFile(runtime, optional: true, reloadOnChange: false);
            var environment = configuration["environment"] ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
            if (environment.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0) throw new InvalidDataException("Invalid runtime environment name.");
            var environmentFile = Path.Combine(Path.GetDirectoryName(runtime)!, Path.GetFileNameWithoutExtension(runtime) + "." + environment + ".json");
            AgentUpdateService.AssertNoLinks(environmentFile);
            configuration.AddJsonFile(environmentFile, optional: true, reloadOnChange: false);
            configuration.AddEnvironmentVariables();
        }
    }

    public static async Task<bool> TryRunAsync(string[] args)
    {
        if (args.FirstOrDefault() is not ("--supervise-update" or "--apply-update")) return false;
        try
        {
            if (args.Length != 2) throw new ArgumentException("Use the installed update supervisor/task.");
            var settings = ReadSettings(args[1]);
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            if (args[0] == "--supervise-update")
            {
                if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
                await SuperviseLinuxAsync(settings, stop.Token).ConfigureAwait(false);
            }
            else
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                try { await ApplyWindowsAsync(settings, stop.Token).ConfigureAwait(false); }
                catch
                {
                    await RunProcessAsync("sc.exe", ["start", "BackupAgent"], CancellationToken.None, false).ConfigureAwait(false);
                    throw;
                }
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Agent update failed (" + exception.GetType().Name + "). Previous identity and configuration were preserved.");
            Environment.ExitCode = 1;
        }
        return true;
    }

    private static async Task SuperviseLinuxAsync(AgentUpdateSettings settings, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (File.Exists(Path.Combine(settings.Root, ".attempted"))) AgentReleaseActivation.RollbackLinux(settings.Root);
            var pending = File.Exists(Path.Combine(settings.Root, ".pending"));
            if (pending) File.WriteAllText(Path.Combine(settings.Root, ".attempted"), "attempted");
            var link = new FileInfo(Path.Combine(settings.Root, "current")).LinkTarget;
            if (link is null || !link.StartsWith("releases/", StringComparison.Ordinal) || !AgentUpdateService.CanonicalVersion(link[9..], out _)) throw new InvalidDataException("Invalid active release.");
            var release = Path.Combine(settings.Root, link);
            AgentUpdateService.AssertNoLinks(release);
            using var child = StartProcess(Path.Combine(release, "BackupPlatform.Agent.Host"), ["--store-directory", settings.Store]);
            try
            {
                if (pending)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(60);
                    while (!child.HasExited && File.Exists(Path.Combine(settings.Root, ".pending")) && DateTime.UtcNow < deadline)
                        await Task.Delay(250, token).ConfigureAwait(false);
                    if (File.Exists(Path.Combine(settings.Root, ".pending")))
                    {
                        if (!child.HasExited) child.Kill(true);
                        await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                        AgentReleaseActivation.RollbackLinux(settings.Root);
                    }
                }
                await child.WaitForExitAsync(token).ConfigureAwait(false);
            }
            finally
            {
                if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            }
            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        }
    }

    private sealed record ActiveRelease(string Version, string Executable);
    private sealed record PendingRelease(ActiveRelease Previous, ActiveRelease Next);

    private static async Task ApplyWindowsAsync(AgentUpdateSettings settings, CancellationToken token)
    {
        var control = Path.Combine(settings.Root, "update-control");
        AgentUpdateService.AssertNoLinks(control);
        var statePath = Path.Combine(control, "active.json");
        var pendingPath = Path.Combine(control, "pending.json");
        var activationPath = Path.Combine(control, "activation");
        if (File.Exists(pendingPath))
        {
            var pending = JsonSerializer.Deserialize<PendingRelease>(await File.ReadAllTextAsync(pendingPath, token).ConfigureAwait(false)) ?? throw new InvalidDataException("Invalid activation journal.");
            var health = Path.Combine(settings.Staging, "healthy");
            if (await ReadHealthAsync(health, token).ConfigureAwait(false) == pending.Next.Version)
            {
                await WriteStateAsync(statePath, pending.Next, token).ConfigureAwait(false);
                File.Delete(pendingPath);
                File.Delete(activationPath);
                return;
            }
            await RunProcessAsync("sc.exe", ["stop", "BackupAgent"], token, false).ConfigureAwait(false);
            WaitWindowsStopped();
            await ConfigureWindowsServiceAsync(pending.Previous.Executable, token).ConfigureAwait(false);
            await WriteStateAsync(statePath, pending.Previous, token).ConfigureAwait(false);
            File.Delete(pendingPath);
            File.Delete(activationPath);
            await RunProcessAsync("sc.exe", ["start", "BackupAgent"], token).ConfigureAwait(false);
            return;
        }
        var previous = JsonSerializer.Deserialize<ActiveRelease>(await File.ReadAllTextAsync(statePath, token).ConfigureAwait(false)) ?? throw new InvalidDataException("No active release journal.");
        AgentUpdateService.AssertNoLinks(previous.Executable);
        if (File.Exists(activationPath))
        {
            var health = Path.Combine(settings.Staging, "healthy");
            if (await ReadHealthAsync(health, token).ConfigureAwait(false) != previous.Version)
                throw new InvalidDataException("Incomplete activation acknowledgement.");
            File.Delete(activationPath);
            return;
        }
        // Snapshot untrusted service-writable input into the protected tree before privileged extraction.
        var snapshot = Path.Combine(control, ".snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapshot);
        try
        {
            var manifestPath = Path.Combine(settings.Staging, "manifest.json");
            AgentUpdateService.AssertNoLinks(manifestPath);
            await using var metadata = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (metadata.Length > 65536) throw new InvalidDataException("Update metadata is too large.");
            var manifest = await JsonSerializer.DeserializeAsync<AgentUpdateManifest>(metadata, cancellationToken: token).ConfigureAwait(false) ?? throw new InvalidDataException("Invalid manifest.");
            var target = AgentUpdateService.ValidateManifest(manifest, "win-x64", settings.TrustedKeys);
            if (!AgentUpdateService.CanonicalVersion(previous.Version, out var current)
                || !AgentUpdatePolicy.IsEligible(current, target, Version.Parse(manifest.Payload.MinimumSupportedVersion), out _)) throw new InvalidDataException("Update is not eligible.");
            var updater = new AgentUpdateService();
            var snapshotPackage = Path.Combine(snapshot, "package");
            await using (var source = new FileStream(Path.Combine(settings.Staging, "package"), FileMode.Open, FileAccess.Read, FileShare.Read))
                await updater.DownloadAsync(source, snapshotPackage, manifest, "win-x64", settings.TrustedKeys, token).ConfigureAwait(false);
            var release = await updater.StageAsync(snapshotPackage, Path.Combine(settings.Root, "releases"), manifest, "win-x64", settings.TrustedKeys, token).ConfigureAwait(false);
            var next = new ActiveRelease(manifest.Payload.Version, Path.Combine(release, "BackupPlatform.Agent.Host.exe"));
            // Wait for the old host's clean shutdown; never stop active work from the privileged helper.
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (true)
            {
                try { using var lease = new FileStream(Path.Combine(settings.Store, ".enrollment.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); break; }
                catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(250, token).ConfigureAwait(false); }
            }
            WaitWindowsStopped();
            try
            {
                await WriteStateAsync(pendingPath, new PendingRelease(previous, next), token).ConfigureAwait(false);
                File.WriteAllText(activationPath, next.Version);
                File.Delete(Path.Combine(settings.Staging, "healthy"));
                await ConfigureWindowsServiceAsync(next.Executable, token).ConfigureAwait(false);
                await RunProcessAsync("sc.exe", ["start", "BackupAgent"], token).ConfigureAwait(false);
                deadline = DateTime.UtcNow.AddMinutes(1);
                while (await ReadHealthAsync(Path.Combine(settings.Staging, "healthy"), token).ConfigureAwait(false) != next.Version && DateTime.UtcNow < deadline)
                    await Task.Delay(250, token).ConfigureAwait(false);
                if (await ReadHealthAsync(Path.Combine(settings.Staging, "healthy"), token).ConfigureAwait(false) != next.Version)
                    throw new IOException("The updated service did not acknowledge startup.");
                await WriteStateAsync(statePath, next, token).ConfigureAwait(false);
                File.Delete(pendingPath);
                File.Delete(activationPath);
            }
            catch
            {
                await RunProcessAsync("sc.exe", ["stop", "BackupAgent"], CancellationToken.None, false).ConfigureAwait(false);
                WaitWindowsStopped();
                await ConfigureWindowsServiceAsync(previous.Executable, CancellationToken.None).ConfigureAwait(false);
                await WriteStateAsync(statePath, previous, CancellationToken.None).ConfigureAwait(false);
                File.Delete(pendingPath);
                File.Delete(activationPath);
                await RunProcessAsync("sc.exe", ["start", "BackupAgent"], CancellationToken.None, false).ConfigureAwait(false);
                throw;
            }
        }
        finally { Directory.Delete(snapshot, true); }
    }

    private static async Task<string?> ReadHealthAsync(string path, CancellationToken token)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[129];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = await file.ReadAsync(buffer.AsMemory(total), token).ConfigureAwait(false)) != 0) total += read;
            return total <= 128 ? System.Text.Encoding.UTF8.GetString(buffer, 0, total) : null;
        }
        catch (IOException) { return null; }
    }

    private static async Task WriteStateAsync<T>(string path, T value, CancellationToken token)
    {
        var temporary = path + ".tmp";
        await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(file, value, cancellationToken: token).ConfigureAwait(false);
            await file.FlushAsync(token).ConfigureAwait(false);
            file.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    private static void WaitWindowsStopped()
    {
#if AGENT_PLATFORM_WINDOWS
        using var service = new System.ServiceProcess.ServiceController("BackupAgent");
        service.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromMinutes(2));
#else
        throw new PlatformNotSupportedException();
#endif
    }

    private static Task ConfigureWindowsServiceAsync(string executable, CancellationToken token) =>
        RunProcessAsync("sc.exe", ["config", "BackupAgent", "binPath=", "\"" + executable + "\""], token);

    private static Process StartProcess(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Unable to start supervised update command.");
    }

    internal static async Task RunProcessAsync(string executable, IEnumerable<string> arguments, CancellationToken token, bool requireSuccess = true)
    {
        using var process = StartProcess(executable, arguments);
        try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } }
        if (requireSuccess && process.ExitCode != 0) throw new IOException("The operating-system update command failed.");
    }
}
