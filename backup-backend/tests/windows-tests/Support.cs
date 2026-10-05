using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using BackupPlatform.Agent.Core.Identity;

namespace BackupPlatform.Agent.Platform.Windows.Tests;

/// <summary>A test that needs Windows. On any other OS it is reported as skipped (never as passed) with this reason.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = Capabilities.NotWindows;
        }
    }
}

/// <summary>A test that needs an elevated Windows session (it creates a local account or does something else only an administrator may).</summary>
public sealed class WindowsAdminFactAttribute : FactAttribute
{
    public WindowsAdminFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = Capabilities.NotWindows;
        }
        else if (!Capabilities.IsElevated)
        {
            Skip = "SKIPPED, NOT PASSED: this test creates a throw-away local account and needs an elevated (Administrator) session.";
        }
    }
}

/// <summary>A test that creates symbolic links, which Windows allows only to elevated sessions or when Developer Mode is on.</summary>
public sealed class WindowsSymlinkFactAttribute : FactAttribute
{
    public WindowsSymlinkFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = Capabilities.NotWindows;
        }
        else if (!Capabilities.CanCreateSymbolicLinks.Value)
        {
            Skip = "SKIPPED, NOT PASSED: this session may not create symbolic links (needs an elevated session or Developer Mode). Junction variants of the same checks still ran.";
        }
    }
}

internal static class Capabilities
{
    public const string NotWindows =
        "SKIPPED, NOT PASSED: DPAPI, NTFS ACLs and reparse points exist only on Windows; run this project on a Windows machine.";

    public static bool IsElevated =>
        OperatingSystem.IsWindows() && new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static Lazy<bool> CanCreateSymbolicLinks { get; } = new(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "bp_test_symlinkprobe_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), root);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            TestDirectory.DeleteTree(root);
        }
    });
}

/// <summary>A private scratch directory below the temp path. Disposing removes it, links included, without ever following a link out of it.</summary>
internal sealed class TestDirectory : IDisposable
{
    /// <param name="baseDirectory">Where the scratch directory goes; the user's temp directory unless the test needs another account to reach it.</param>
    public TestDirectory(string? baseDirectory = null)
    {
        Root = Path.Combine(baseDirectory ?? Path.GetTempPath(), "bp_test_winstore_" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>The names of the entries directly inside <paramref name="directory"/>, ordered.</summary>
    public static string[] Names(string directory) =>
        Directory.GetFileSystemEntries(directory).Select(entry => Path.GetFileName(entry)).Order(StringComparer.Ordinal).ToArray();

    public string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    public string CreateDirectory(params string[] parts)
    {
        var path = Combine(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose() => DeleteTree(Root);

    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return;
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                DeleteWithoutFollowingLinks(path);
                return;
            }
            catch (Exception exception) when (attempt < 5 && exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static void DeleteWithoutFollowingLinks(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            // Removes the link itself, never its target.
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.Delete(path);
            }
            else
            {
                File.Delete(path);
            }

            return;
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            return;
        }

        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            DeleteWithoutFollowingLinks(child);
        }

        Directory.Delete(path);
    }
}

internal static class Links
{
    public static void Symbolic(string link, string target, bool directory)
    {
        if (directory)
        {
            Directory.CreateSymbolicLink(link, target);
        }
        else
        {
            File.CreateSymbolicLink(link, target);
        }
    }

    /// <summary>A junction needs no special privilege, so these checks run in every session.</summary>
    public static void Junction(string link, string target)
    {
        var result = Processes.Run("cmd.exe", ["/c", "mklink", "/J", link, target]);
        Assert.True(result.ExitCode == 0, $"mklink /J failed: {result.Output}");
    }
}

internal static class Processes
{
    public static ProcessResult Run(string fileName, IEnumerable<string> arguments, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Wait(start, timeout);
    }

    public static ProcessResult Wait(ProcessStartInfo start, TimeSpan? timeout = null)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The process did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout ?? TimeSpan.FromSeconds(90)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{Path.GetFileName(start.FileName)} did not finish in time.");
        }

        return new ProcessResult(process.ExitCode, (output.Result + error.Result).Trim());
    }
}

public sealed record ProcessResult(int ExitCode, string Output);

/// <summary>The probe program of this test project, started as its own process (optionally as another local account).</summary>
internal static class Probe
{
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "probe");

    private static string Executable(string directory)
    {
        var apphost = Path.Combine(directory, "BackupPlatform.Agent.Platform.Windows.TestProbe.exe");
        Assert.True(File.Exists(apphost), $"The test probe was not built next to the tests ({apphost}).");
        return apphost;
    }

    public static ProcessResult Run(params string[] arguments) => Processes.Run(Executable(Directory), arguments);

    public static ProcessResult RunAs(LocalAccount account, string probeDirectory, params string[] arguments) =>
        AccountProbe.Run(account, Executable(probeDirectory), arguments);
}

/// <summary>
/// A throw-away local account (a member of Users only) that exists for the duration of one test class, to prove what another Windows
/// account can and cannot do. Needs an elevated session. Disposing deletes the account and its profile.
/// </summary>
public sealed class LocalAccount : IDisposable
{
    private readonly string _probeCopy;
    private readonly AccountLogonRight _logonRight;

    public LocalAccount()
    {
        Name = "bpw" + Guid.NewGuid().ToString("N")[..10];
        var password = "Bp!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', 'x').Replace('/', 'y').Replace('=', 'z') + "9a";
        SecurePassword = new SecureString();
        foreach (var character in password)
        {
            SecurePassword.AppendChar(character);
        }

        SecurePassword.MakeReadOnly();
        var created = Processes.Run("net.exe", ["user", Name, password, "/add", "/y", "/passwordchg:no", "/expires:never"]);
        Assert.True(created.ExitCode == 0, $"Could not create the test account: {created.Output.Replace(password, "***", StringComparison.Ordinal)}");

        _probeCopy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "bp_test_probe_" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            _logonRight = new AccountLogonRight(Sid);
            // The account must be able to start the probe; ProgramData grants Users read and execute.
            CopyTree(Probe.Directory, _probeCopy);
        }
        catch
        {
            _logonRight?.Dispose();
            TestDirectory.DeleteTree(_probeCopy);
            Processes.Run("net.exe", ["user", Name, "/delete"]);
            SecurePassword.Dispose();
            throw;
        }
    }

    public string Name { get; }

    public SecureString SecurePassword { get; }

    /// <summary>The directory the other account runs the probe from.</summary>
    public string ProbeDirectory => _probeCopy;

    public SecurityIdentifier Sid => (SecurityIdentifier)new NTAccount(Name).Translate(typeof(SecurityIdentifier));

    public ProcessResult RunProbe(params string[] arguments) => Probe.RunAs(this, _probeCopy, arguments);

    public void Dispose()
    {
        TestDirectory.DeleteTree(_probeCopy);
        _logonRight.Dispose();
        Processes.Run("net.exe", ["user", Name, "/delete"]);
        // Removes the profile directory the logon created; best effort, the account itself is already gone.
        Processes.Run(
            "powershell.exe",
            [
                "-NoProfile", "-NonInteractive", "-Command",
                string.Create(CultureInfo.InvariantCulture, $"Get-CimInstance Win32_UserProfile | Where-Object {{ $_.LocalPath -like '*\\{Name}*' }} | Remove-CimInstance -ErrorAction SilentlyContinue")
            ]);
        SecurePassword.Dispose();
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in System.IO.Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}

internal static class TestIdentity
{
    public static AgentLocalIdentity Create(int generation = 1, bool withPending = false, Guid? agentId = null)
    {
        var current = Credential(generation);
        return new AgentLocalIdentity(
            agentId ?? Guid.NewGuid(),
            new Uri("https://backup.example.test/"),
            Guid.NewGuid(),
            current,
            withPending ? Credential(generation + 1) : null);
    }

    public static AgentStoredCredential Credential(int generation) => new(Guid.NewGuid(), NewSecret(), generation);

    public static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}

internal static class Acl
{
    public static SecurityIdentifier Users { get; } = new(WellKnownSidType.BuiltinUsersSid, null);

    public static SecurityIdentifier System { get; } = new(WellKnownSidType.LocalSystemSid, null);

    public static SecurityIdentifier Administrators { get; } = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }

    /// <summary>Allows <paramref name="sid"/> to read the directory (inherited by everything in it), leaving the rest of its ACL as it is.</summary>
    public static void GrantRead(string directory, SecurityIdentifier sid)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    public static void GrantRead(FileInfo file, SecurityIdentifier sid)
    {
        var security = file.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Read, AccessControlType.Allow));
        file.SetAccessControl(security);
    }

    /// <summary>The identities holding an allow rule, whether set or inherited, read independently of the production code under test.</summary>
    public static IReadOnlySet<SecurityIdentifier> AllowedPrincipals(FileSystemSecurity security) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .ToHashSet();
}
