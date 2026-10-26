using System.ComponentModel;
using System.Runtime.InteropServices;

namespace BackupPlatform.Agent.Core.Updates;

/// <summary>Journaled symlink cutover; the supervised launcher rolls back an unacknowledged release.</summary>
public static class AgentReleaseActivation
{
    public static void BeginLinux(string root, string version)
    {
        if (!AgentUpdateService.CanonicalVersion(version, out _)) throw new InvalidDataException("Invalid release version.");
        AgentUpdateService.AssertNoLinks(root);
        AgentUpdateService.AssertNoLinks(Path.Combine(root, "releases", version));
        if (!File.Exists(Path.Combine(root, "releases", version, "BackupPlatform.Agent.Host"))) throw new InvalidDataException("Release is incomplete.");
        var current = new FileInfo(Path.Combine(root, "current"));
        var previous = current.LinkTarget;
        if (previous is null || !previous.StartsWith("releases/", StringComparison.Ordinal) || !AgentUpdateService.CanonicalVersion(previous[9..], out _))
            throw new InvalidDataException("Current release is not an installed version.");
        if (File.Exists(Path.Combine(root, ".pending"))) throw new IOException("An activation is already pending.");
        ReplaceLink(root, ".previous", previous);
        File.WriteAllText(Path.Combine(root, ".pending"), version);
        try { ReplaceLink(root, "current", "releases/" + version); }
        catch { File.Delete(Path.Combine(root, ".pending")); throw; }
    }

    public static void ConfirmLinux(string root, string version)
    {
        var pending = Path.Combine(root, ".pending");
        if (File.Exists(pending) && File.ReadAllText(pending) == version
            && new FileInfo(Path.Combine(root, "current")).LinkTarget == "releases/" + version)
        {
            File.Delete(pending);
            File.Delete(Path.Combine(root, ".attempted"));
        }
    }

    public static void RollbackLinux(string root)
    {
        if (!File.Exists(Path.Combine(root, ".pending"))) return;
        var previous = new FileInfo(Path.Combine(root, ".previous")).LinkTarget;
        if (previous is null || !previous.StartsWith("releases/", StringComparison.Ordinal) || !AgentUpdateService.CanonicalVersion(previous[9..], out _))
            throw new InvalidDataException("Rollback target is invalid.");
        AgentUpdateService.AssertNoLinks(Path.Combine(root, previous));
        ReplaceLink(root, "current", previous);
        File.Delete(Path.Combine(root, ".pending"));
        File.Delete(Path.Combine(root, ".attempted"));
    }

    private static void ReplaceLink(string root, string name, string target)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Release link activation requires Linux.");
        var destination = Path.Combine(root, name);
        var temporary = Path.Combine(root, ".link-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.CreateSymbolicLink(temporary, target);
            // File.Move rejects symlinks to directories before reaching rename(2)
            if (Rename(temporary, destination) != 0)
                throw new IOException("Cannot atomically replace the release link.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        finally { File.Delete(temporary); }
    }

    [DllImport("libc", EntryPoint = "rename", SetLastError = true)]
    private static extern int Rename([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
}
