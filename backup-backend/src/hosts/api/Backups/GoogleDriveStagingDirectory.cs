using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace BackupPlatform.Api.Backups;

internal sealed class GoogleDriveStagingDirectory : IDisposable
{
    internal const string OwnedDirectoryName = "backup-platform-drive-staging";
    private readonly List<SafeFileHandle> _handles = [];
    private readonly Func<string, long>? _probe;

    public GoogleDriveStagingDirectory(string basePath, Func<string, long>? probe)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetFullPath(basePath), OwnedDirectoryName);
        _probe = probe;
        try
        {
            if (OperatingSystem.IsWindows()) OpenWindows();
            else OpenUnix();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string Path { get; }
    private SafeFileHandle DirectoryHandle => _handles[^1];

    public string GetOrCreateVolumeId()
    {
        const string identityName = "volume.id";
        FileStream stream;
        if (OperatingSystem.IsWindows())
        {
            stream = OpenWindowsIdentity(System.IO.Path.Combine(Path, identityName));
        }
        else
        {
            SafeFileHandle handle;
            try { handle = Native.OpenAt(DirectoryHandle, identityName, Native.CreateFlags, 384); }
            catch (Win32Exception error) when (error.NativeErrorCode == 17) { handle = Native.OpenAt(DirectoryHandle, identityName, Native.OpenFileFlags, 0); }
            try
            {
                Native.LockIdentity(handle);
                Native.RequirePrivateFile(handle);
                stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: false);
            }
            catch { handle.Dispose(); throw; }
        }
        using (stream)
        {
            if (stream.Length < 32)
            {
                stream.SetLength(0);
                stream.Write(System.Text.Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N")));
                stream.Flush(flushToDisk: true);
                stream.Position = 0;
            }
            Span<byte> identity = stackalloc byte[32];
            if (stream.Length != identity.Length || stream.Read(identity) != identity.Length
                || !Guid.TryParseExact(System.Text.Encoding.ASCII.GetString(identity), "N", out var volume))
                throw new IOException("Staging volume identity is invalid.");
            return $"local-{volume:N}";
        }
    }

    [SupportedOSPlatform("windows")]
    private static FileStream OpenWindowsIdentity(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Reparse volume identity is forbidden.");
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && attempt < 100)
            {
                Thread.Sleep(10);
            }
        }
    }

    public long GetAvailableBytes()
    {
        long actual;
        if (OperatingSystem.IsWindows())
        {
            if (!Native.GetDiskFreeSpaceEx(Path, out var available, out _, out _)) throw Native.Error();
            actual = (long)Math.Min(available, (ulong)long.MaxValue);
        }
        else
        {
            if (Native.FStatVfs(DirectoryHandle, out var statistics) != 0) throw Native.Error();
            var bytes = (decimal)statistics.AvailableBlocks * statistics.FragmentSize;
            actual = (long)Math.Min(bytes, long.MaxValue);
        }
        return _probe is null ? actual : Math.Min(actual, Math.Max(0, _probe(Path)));
    }

    public FileStream Create(Guid id)
    {
        var name = FileName(id);
        var path = System.IO.Path.Combine(Path, name);
        if (OperatingSystem.IsWindows())
        {
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.Read,
                BufferSize = 1, Options = FileOptions.Asynchronous
            });
        }
        var handle = Native.OpenAt(DirectoryHandle, name, Native.CreateFlags, 384);
        handle.Dispose();
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open, Access = FileAccess.ReadWrite, Share = FileShare.ReadWrite,
            BufferSize = 1, Options = FileOptions.Asynchronous
        });
    }

    public string GetUploadPath(FileStream stream, Guid id) => System.IO.Path.Combine(Path, FileName(id));

    public bool TryDeleteStale(Guid id)
    {
        var name = FileName(id);
        if (OperatingSystem.IsWindows()) return DeleteStaleWindows(name);
        SafeFileHandle handle;
        try { handle = Native.OpenAt(DirectoryHandle, name, Native.OpenFileFlags, 0); }
        catch (Win32Exception error) when (error.NativeErrorCode == 2) { return true; }
        using (handle)
        {
            if (!Native.TryLock(handle)) return false;
            Native.RequireRegularFile(handle);
            DeleteUnix(name);
        }
        return true;
    }

    public async ValueTask DeleteOwnedAsync(Guid id, FileStream stream)
    {
        if (OperatingSystem.IsWindows())
        {
            await stream.DisposeAsync();
            DeleteStaleWindows(FileName(id));
            if (File.Exists(System.IO.Path.Combine(Path, FileName(id)))) throw new IOException("Staging file is still locked.");
        }
        else
        {
            DeleteUnix(FileName(id));
            await stream.DisposeAsync();
        }
    }

    public void Dispose()
    {
        for (var index = _handles.Count - 1; index >= 0; index--) _handles[index].Dispose();
        _handles.Clear();
    }

    private void OpenUnix()
    {
        var parent = Native.OpenRoot();
        _handles.Add(parent);
        foreach (var component in Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var child = Native.OpenDirectoryAt(parent, component);
            _handles.Add(child);
            parent = child;
        }
        Native.RequirePrivateDirectory(parent);
    }

    private void DeleteUnix(string name)
    {
        if (Native.UnlinkAt(DirectoryHandle, name, 0) != 0 && Marshal.GetLastPInvokeError() != 2) throw Native.Error();
    }

    [SupportedOSPlatform("windows")]
    private void OpenWindows()
    {
        var root = System.IO.Path.GetPathRoot(Path)!;
        var current = root;
        PinWindowsDirectory(current);
        var components = Path[root.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < components.Length; index++)
        {
            current = System.IO.Path.Combine(current, components[index]);
            if (index == components.Length - 1)
            {
                var security = new DirectorySecurity();
                var owner = WindowsIdentity.GetCurrent().User!;
                security.SetOwner(owner);
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                if (!Directory.Exists(current)) new DirectoryInfo(current).Create(security);
            }
            else if (!Directory.Exists(current)) Directory.CreateDirectory(current);
            PinWindowsDirectory(current);
        }
        RequirePrivateWindowsDirectory();
    }

    [SupportedOSPlatform("windows")]
    private void PinWindowsDirectory(string path)
    {
        var handle = Native.CreateFile(path, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw Native.Error(); }
        _handles.Add(handle);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse staging paths are forbidden.");
    }

    [SupportedOSPlatform("windows")]
    private void RequirePrivateWindowsDirectory()
    {
        var security = new DirectoryInfo(Path).GetAccessControl();
        var owner = WindowsIdentity.GetCurrent().User!;
        if (!owner.Equals(security.GetOwner(typeof(SecurityIdentifier)))) throw new UnauthorizedAccessException("Staging directory has another owner.");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || owner.Equals(rule.IdentityReference)) continue;
            var identifier = (SecurityIdentifier)rule.IdentityReference;
            if (identifier.IsWellKnown(WellKnownSidType.LocalSystemSid) || identifier.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) continue;
            throw new UnauthorizedAccessException("Staging directory must be private.");
        }
    }

    [SupportedOSPlatform("windows")]
    private bool DeleteStaleWindows(string name)
    {
        var path = System.IO.Path.Combine(Path, name);
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Reparse staging files are forbidden.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (FileNotFoundException) { return true; }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return false; }
        File.Delete(path);
        return true;
    }

    private static string FileName(Guid id) => $"{id:N}.stage";

    private static class Native
    {
        internal const int CreateFlags = 2 | 64 | 128 | 0x20000 | 0x80000;
        internal const int OpenFileFlags = 2 | 0x20000 | 0x80000;
        private const int DirectoryFlags = 0x10000 | 0x20000 | 0x80000;

        internal static Win32Exception Error() => new(Marshal.GetLastPInvokeError());
        internal static SafeFileHandle OpenRoot() => Wrap(Open("/", DirectoryFlags));
        internal static SafeFileHandle OpenAt(SafeFileHandle parent, string name, int flags, uint mode) => Wrap(OpenAtNative(parent, name, flags, mode));

        internal static SafeFileHandle OpenDirectoryAt(SafeFileHandle parent, string name)
        {
            var descriptor = OpenAtNative(parent, name, DirectoryFlags, 0);
            if (descriptor >= 0) return Wrap(descriptor);
            if (Marshal.GetLastPInvokeError() != 2) throw Error();
            if (MkDirAt(parent, name, 448) != 0 && Marshal.GetLastPInvokeError() != 17) throw Error();
            return OpenAt(parent, name, DirectoryFlags, 0);
        }

        internal static void RequirePrivateDirectory(SafeFileHandle handle)
        {
            var status = GetStatus(handle);
            if (status.UserId != GetEffectiveUserId() || (status.Mode & 0x1ff) != 448 || (status.Mode & 0xf000) != 0x4000)
                throw new UnauthorizedAccessException("Staging directory must be private and owned by this process identity.");
        }

        internal static void RequireRegularFile(SafeFileHandle handle)
        {
            if ((GetStatus(handle).Mode & 0xf000) != 0x8000) throw new IOException("Tracked staging entry is not a regular file.");
        }

        internal static void RequirePrivateFile(SafeFileHandle handle)
        {
            var status = GetStatus(handle);
            if (status.UserId != GetEffectiveUserId() || (status.Mode & 0x1ff) != 384 || (status.Mode & 0xf000) != 0x8000)
                throw new UnauthorizedAccessException("Staging identity must be a private regular file.");
        }

        private static Statx GetStatus(SafeFileHandle handle)
        {
            if (StatX(handle, "", 0x1000, 0x7ff, out var status) != 0) throw Error();
            return status;
        }

        internal static bool TryLock(SafeFileHandle handle)
        {
            if (Flock(handle, 6) == 0) return true;
            if (Marshal.GetLastPInvokeError() == 11) return false;
            throw Error();
        }

        internal static void Lock(SafeFileHandle handle)
        {
            if (!TryLock(handle)) throw new IOException("Staging file is already locked.");
        }

        internal static void LockIdentity(SafeFileHandle handle)
        {
            if (Flock(handle, 2) != 0) throw Error();
        }

        private static SafeFileHandle Wrap(int descriptor)
        {
            if (descriptor < 0) throw Error();
            return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }

        [StructLayout(LayoutKind.Explicit, Size = 256)]
        internal struct StatVfs
        {
            [FieldOffset(8)] internal ulong FragmentSize;
            [FieldOffset(32)] internal ulong AvailableBlocks;
        }

        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct Statx
        {
            [FieldOffset(20)] internal uint UserId;
            [FieldOffset(28)] internal ushort Mode;
        }

        [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open(string path, int flags);
        [DllImport("libc", EntryPoint = "openat", SetLastError = true)] private static extern int OpenAtNative(SafeFileHandle directory, string path, int flags, uint mode);
        [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)] private static extern int MkDirAt(SafeFileHandle directory, string path, uint mode);
        [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)] internal static extern int UnlinkAt(SafeFileHandle directory, string path, int flags);
        [DllImport("libc", EntryPoint = "flock", SetLastError = true)] private static extern int Flock(SafeFileHandle descriptor, int operation);
        [DllImport("libc", EntryPoint = "statx", SetLastError = true)] private static extern int StatX(SafeFileHandle descriptor, string path, int flags, uint mask, out Statx status);
        [DllImport("libc", EntryPoint = "geteuid")] private static extern uint GetEffectiveUserId();
        [DllImport("libc", EntryPoint = "fstatvfs", SetLastError = true)] internal static extern int FStatVfs(SafeFileHandle descriptor, out StatVfs statistics);
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
    }
}
