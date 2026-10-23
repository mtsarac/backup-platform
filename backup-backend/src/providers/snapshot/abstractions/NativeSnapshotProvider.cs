using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BackupPlatform.SnapshotProviders.Abstractions;

/// <summary>Native copy-on-write snapshot implementations available to the agent.</summary>
public enum SnapshotKind { Vss, Lvm, Btrfs, Zfs }

/// <summary>Locally configured source and snapshot location; never accepted directly from tenant input.</summary>
public sealed record SnapshotRequest(string Source, string SnapshotDirectory, string Name, string? LvmSize = null);

/// <summary>Creates an owned read-only view which must be disposed after the consuming backup completes.</summary>
public interface ISnapshotProvider
{
    SnapshotKind Kind { get; }
    Task<SnapshotLease> CreateAsync(SnapshotRequest request, CancellationToken cancellationToken);
}

/// <summary>A read-only snapshot and its idempotent cleanup operation. Cleanup failures remain visible to the caller.</summary>
public sealed class SnapshotLease(string path, Func<Task> cleanup) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool deleted;
    public string Path { get; } = path;
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (deleted) return;
            await cleanup().ConfigureAwait(false);
            deleted = true;
        }
        finally { gate.Release(); }
    }
}

/// <summary>Bounded, shell-free native command execution; exposed for platform-provider acceptance tests.</summary>
public interface ISnapshotCommandRunner
{
    Task<string> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

/// <summary>Executes snapshot tools without exposing raw command output in errors.</summary>
public sealed class SnapshotCommandRunner : ISnapshotCommandRunner
{
    public async Task<string> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        process.Start();
        using var registration = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(output, errors).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("The native snapshot operation failed.");
            return await output.ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}

/// <summary>VSS with writers, LVM, Btrfs and ZFS snapshot creation and cleanup. No filesystem freeze is left behind on failure.</summary>
public sealed class NativeSnapshotProvider(SnapshotKind kind, ISnapshotCommandRunner commands, Func<string, bool>? isMounted = null) : ISnapshotProvider
{
    // The agent names every snapshot it creates bp_<32 hex>; only such names are ever swept.
    private static readonly Regex OwnedName = new("^bp_[0-9a-f]{32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex OwnedZfsSnapshot = new("^[A-Za-z0-9][A-Za-z0-9_./:-]*@(bp_[0-9a-f]{32})$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex LvmName = new("^[A-Za-z0-9_+.-]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private readonly ConcurrentDictionary<string, byte> live = new(StringComparer.Ordinal);
    private readonly Func<string, bool> mounted = isMounted ?? IsMounted;
    public SnapshotKind Kind { get; } = kind;

    /// <summary>A snapshot is live from the moment its creation starts until its lease is cleaned up, so a sweep never removes one a backup is still reading.</summary>
    public async Task<SnapshotLease> CreateAsync(SnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name;
        live[name] = 0;
        SnapshotLease lease;
        try { lease = await CreateCoreAsync(request, cancellationToken).ConfigureAwait(false); }
        catch { live.TryRemove(name, out _); throw; }
        return new SnapshotLease(lease.Path, async () => { await lease.DisposeAsync().ConfigureAwait(false); live.TryRemove(name, out _); });
    }

    /// <summary>
    /// Removes snapshots this agent created that no live lease owns: what a crashed process or a rollback that could not finish left behind.
    /// <paramref name="snapshotDirectory"/> is the directory snapshots are created in (Btrfs subvolumes and LVM mount points live there; ZFS needs no directory).
    /// A failure on one leftover does not stop the others; all failures are rethrown together. Returns the number removed. VSS is not swept.
    /// </summary>
    public async Task<int> SweepLeftoversAsync(string snapshotDirectory, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(snapshotDirectory)) throw new ArgumentException("Snapshot directory must be absolute.", nameof(snapshotDirectory));
        var leftovers = new List<(string Description, Func<Task> Remove)>();
        switch (Kind)
        {
            case SnapshotKind.Btrfs:
                if (!Directory.Exists(snapshotDirectory)) return 0;
                foreach (var directory in Directory.EnumerateDirectories(snapshotDirectory))
                {
                    var name = Path.GetFileName(directory);
                    if (OwnedName.IsMatch(name) && !live.ContainsKey(name))
                        leftovers.Add((name, () => Run("btrfs", ["subvolume", "delete", directory], CancellationToken.None)));
                }
                break;
            case SnapshotKind.Zfs:
                foreach (var line in (await Run("zfs", ["list", "-H", "-t", "snapshot", "-o", "name"], cancellationToken).ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (OwnedZfsSnapshot.Match(line) is { Success: true } match && !live.ContainsKey(match.Groups[1].Value))
                        leftovers.Add((line, () => Run("zfs", ["destroy", line], CancellationToken.None)));
                break;
            case SnapshotKind.Lvm:
                foreach (var line in (await Run("lvs", ["--noheadings", "--separator", "/", "-o", "vg_name,lv_name"], cancellationToken).ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = line.Split('/');
                    if (parts is not [var vg, var lv] || !LvmName.IsMatch(vg) || !OwnedName.IsMatch(lv) || live.ContainsKey(lv)) continue;
                    var mountPoint = Path.Combine(snapshotDirectory, lv);
                    leftovers.Add((lv, async () =>
                    {
                        if (mounted(mountPoint)) await Run("umount", [mountPoint], CancellationToken.None).ConfigureAwait(false);
                        await Run("lvremove", ["--force", "/dev/" + vg + "/" + lv], CancellationToken.None).ConfigureAwait(false);
                        if (Directory.Exists(mountPoint)) Directory.Delete(mountPoint);
                    }));
                }
                break;
            default: return 0;
        }
        var removed = 0;
        var failures = new List<Exception>();
        foreach (var (description, remove) in leftovers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await remove().ConfigureAwait(false); removed++; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { failures.Add(new IOException("A leftover " + Kind + " snapshot could not be removed: " + description, exception)); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
        return removed;
    }

    private static bool IsMounted(string path) =>
        OperatingSystem.IsLinux() && File.ReadLines("/proc/self/mounts").Any(line => line.Split(' ') is [_, var target, ..] && target == path);

    private async Task<SnapshotLease> CreateCoreAsync(SnapshotRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Regex.IsMatch(request.Name, "^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            throw new ArgumentException("Snapshot name must be a simple unique identifier.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Source) || request.Source.Any(char.IsControl) || request.Source.StartsWith('-'))
            throw new ArgumentException("A native snapshot source is required.", nameof(request));
        if (Kind == SnapshotKind.Vss)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("VSS requires Windows.");
            if (!Regex.IsMatch(request.Source, "^[A-Za-z]:[\\\\/]$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                throw new ArgumentException("VSS source must be a volume root.", nameof(request));
            var script = "$ErrorActionPreference='Stop'; $ctx=Get-CimInstance Win32_ShadowContext | Where-Object { $_.Persistent -and $_.NoAutoRelease -and -not $_.NoWriters } | Select-Object -First 1; if($null -eq $ctx){throw 'A persistent writer-enabled VSS context is required'}; $s=([wmiclass]'Win32_ShadowCopy').Create('" + request.Source + "',$ctx.Name); if($s.ReturnValue -ne 0){throw 'VSS failed'}; try { $copy=Get-CimInstance Win32_ShadowCopy | Where-Object ID -eq $s.ShadowID; if($null -eq $copy -or $copy.NoWriters -or -not $copy.Persistent){throw 'Invalid VSS view'}; $copy | Select-Object ID,DeviceObject | ConvertTo-Json -Compress } catch { Get-CimInstance Win32_ShadowCopy | Where-Object ID -eq $s.ShadowID | Remove-CimInstance; throw }";
            var text = await Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], CancellationToken.None).ConfigureAwait(false);
            using var json = JsonDocument.Parse(text);
            var id = Guid.Parse(json.RootElement.GetProperty("ID").GetString()!);
            var path = json.RootElement.GetProperty("DeviceObject").GetString()! + "\\";
            return await LeaseAsync(path, () => Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; Get-CimInstance Win32_ShadowCopy | Where-Object ID -eq '{" + id + "}' | Remove-CimInstance"], CancellationToken.None), cancellationToken).ConfigureAwait(false);
        }
        if (!Path.IsPathFullyQualified(request.SnapshotDirectory)) throw new ArgumentException("Snapshot directory must be absolute.", nameof(request));
        var destination = Path.Combine(request.SnapshotDirectory, request.Name);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("The snapshot destination already exists.");
        switch (Kind)
        {
            case SnapshotKind.Btrfs:
                await Run("btrfs", ["subvolume", "snapshot", "-r", request.Source, destination], CancellationToken.None).ConfigureAwait(false);
                return await LeaseAsync(destination, () => Run("btrfs", ["subvolume", "delete", destination], CancellationToken.None), cancellationToken).ConfigureAwait(false);
            case SnapshotKind.Zfs:
                if (!Regex.IsMatch(request.Source, "^[A-Za-z0-9][A-Za-z0-9_./:-]*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) || request.Source.Contains("..", StringComparison.Ordinal))
                    throw new ArgumentException("ZFS source must be a dataset name.", nameof(request));
                var snapshot = request.Source + "@" + request.Name;
                await Run("zfs", ["snapshot", snapshot], CancellationToken.None).ConfigureAwait(false);
                try
                {
                    var mount = (await Run("zfs", ["get", "-H", "-o", "value", "mountpoint", request.Source], cancellationToken).ConfigureAwait(false)).Trim();
                    if (!Path.IsPathFullyQualified(mount)) throw new IOException("The dataset is not mounted.");
                    return new SnapshotLease(Path.Combine(mount, ".zfs", "snapshot", request.Name), () => Run("zfs", ["destroy", snapshot], CancellationToken.None));
                }
                catch { await Run("zfs", ["destroy", snapshot], CancellationToken.None).ConfigureAwait(false); throw; }
            case SnapshotKind.Lvm:
                if (!Regex.IsMatch(request.Source, "^/dev/[A-Za-z0-9_+-]+/[A-Za-z0-9_+-]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) ||
                    !Regex.IsMatch(request.LvmSize ?? "", "^[1-9][0-9]*[MGT]$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                    throw new ArgumentException("LVM requires a volume path and a bounded snapshot size.", nameof(request));
                var device = request.Source[..(request.Source.LastIndexOf('/') + 1)] + request.Name;
                await Run("lvcreate", ["--snapshot", "--permission", "r", "--size", request.LvmSize!, "--name", request.Name, request.Source], CancellationToken.None).ConfigureAwait(false);
                var mounted = false;
                var removed = false;
                try
                {
                    Directory.CreateDirectory(destination);
                    await Run("mount", ["-o", "ro", device, destination], CancellationToken.None).ConfigureAwait(false);
                    mounted = true;
                    cancellationToken.ThrowIfCancellationRequested();
                    return new SnapshotLease(destination, async () =>
                    {
                        if (mounted)
                        {
                            await Run("umount", [destination], CancellationToken.None).ConfigureAwait(false);
                            mounted = false;
                        }
                        if (!removed)
                        {
                            await Run("lvremove", ["--force", device], CancellationToken.None).ConfigureAwait(false);
                            removed = true;
                        }
                        if (Directory.Exists(destination)) Directory.Delete(destination);
                    });
                }
                catch
                {
                    if (mounted) await Run("umount", [destination], CancellationToken.None).ConfigureAwait(false);
                    await Run("lvremove", ["--force", device], CancellationToken.None).ConfigureAwait(false);
                    if (Directory.Exists(destination)) Directory.Delete(destination);
                    throw;
                }
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static async Task<SnapshotLease> LeaseAsync(string path, Func<Task> cleanup, CancellationToken token)
    {
        var lease = new SnapshotLease(path, cleanup);
        if (!token.IsCancellationRequested) return lease;
        await lease.DisposeAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return lease;
    }

    private Task<string> Run(string file, string[] arguments, CancellationToken token) => commands.RunAsync(file, arguments, token);
}
