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
public sealed class NativeSnapshotProvider(SnapshotKind kind, ISnapshotCommandRunner commands) : ISnapshotProvider
{
    public SnapshotKind Kind { get; } = kind;

    public async Task<SnapshotLease> CreateAsync(SnapshotRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
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
