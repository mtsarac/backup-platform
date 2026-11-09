using System.Security.AccessControl;
using System.Security.Principal;
using BackupPlatform.BackupProviders.FileSystem;

namespace BackupPlatform.Agent.Platform.Windows.Tests;

/// <summary>
/// Negative contract for restore ACLs: access granted on the destination parent never reaches a restored file or directory.
/// Every ACL is read back through <see cref="FileSystemSecurity"/>, independently of the production capture code.
/// </summary>
public sealed class WindowsRestoreAclNegativeTests
{
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier Guests = new(WellKnownSidType.BuiltinGuestsSid, null);
    private static readonly SecurityIdentifier AuthenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);

    [WindowsFact]
    public async Task Restore_never_applies_destination_parent_allow_rules_to_restored_files_or_directories()
    {
        var root = Path.Combine(Path.GetTempPath(), "bp-windows-acl-neg-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var staging = Path.Combine(root, "staging");
        var target = Path.Combine(root, "target");
        var permissions = new WindowsFilePermissions();
        Directory.CreateDirectory(root);
        permissions.CreatePrivateDirectory(source);
        permissions.CreatePrivateDirectory(staging);
        permissions.CreatePrivateDirectory(target);
        try
        {
            var sourceDirectory = new DirectoryInfo(source);
            var sourceSecurity = sourceDirectory.GetAccessControl();
            sourceSecurity.AddAccessRule(new FileSystemAccessRule(Guests, FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny));
            sourceDirectory.SetAccessControl(sourceSecurity);
            Directory.CreateDirectory(Path.Combine(source, "private", "nested"));
            await File.WriteAllTextAsync(Path.Combine(source, "private", "data.txt"), "restricted data");
            await File.WriteAllTextAsync(Path.Combine(source, "private", "nested", "deep.txt"), "deeper data");
            await File.WriteAllTextAsync(Path.Combine(source, "top.txt"), "top data");

            // Precondition: neither destination principal has any access to the source, so any allow for them on a restored item came from the destination.
            foreach (var path in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories).Append(source))
            {
                var allowed = Acl.AllowedPrincipals(Security(path));
                Assert.DoesNotContain(Everyone, allowed);
                Assert.DoesNotContain(AuthenticatedUsers, allowed);
            }

            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            var targetDirectory = new DirectoryInfo(target);
            var targetSecurity = targetDirectory.GetAccessControl();
            foreach (var principal in new[] { Everyone, AuthenticatedUsers })
                targetSecurity.AddAccessRule(new FileSystemAccessRule(principal, FileSystemRights.FullControl,
                    inheritance, PropagationFlags.None, AccessControlType.Allow));
            targetDirectory.SetAccessControl(targetSecurity);

            var provider = new FileSystemBackupProvider([], permissions);
            var (manifest, archivePath) = await provider.CreateBackupAsync(new(source, ["**"], []), staging, null, default);
            Assert.Contains(manifest.Files, entry => entry.IsDirectory);
            Assert.Contains(manifest.Files, entry => !entry.IsDirectory);

            await provider.RestoreAsync(archivePath, target, false, null, null, default);

            foreach (var entry in manifest.Files)
            {
                var restored = Path.Combine(target, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                var security = Security(restored);
                var kind = entry.IsDirectory ? "directory" : "file";
                Assert.True(security.AreAccessRulesProtected, $"restored {kind} {entry.RelativePath} must have a protected DACL");
                var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
                Assert.DoesNotContain(rules, rule => rule.IsInherited);
                Assert.Contains(rules, rule => rule.IdentityReference.Equals(Guests) && rule.AccessControlType == AccessControlType.Deny);
                var allowed = Acl.AllowedPrincipals(security);
                Assert.DoesNotContain(Everyone, allowed);
                Assert.DoesNotContain(AuthenticatedUsers, allowed);
                // The restored allow set is exactly what the source had.
                Assert.Equal(Acl.AllowedPrincipals(Security(Path.Combine(source, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)))), allowed);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [WindowsFact]
    public void Capture_followed_by_apply_on_a_broader_parent_keeps_the_item_protected_and_without_the_parent_allow()
    {
        var root = Path.Combine(Path.GetTempPath(), "bp-windows-acl-neg-" + Guid.NewGuid().ToString("N"));
        var permissions = new WindowsFilePermissions();
        Directory.CreateDirectory(root);
        try
        {
            var narrow = Path.Combine(root, "narrow");
            var broad = Path.Combine(root, "broad");
            permissions.CreatePrivateDirectory(narrow);
            permissions.CreatePrivateDirectory(broad);
            var narrowFile = Path.Combine(narrow, "a.txt");
            var broadFile = Path.Combine(broad, "a.txt");
            File.WriteAllText(narrowFile, "x");
            File.WriteAllText(broadFile, "x");
            var broadInfo = new DirectoryInfo(broad);
            var broadSecurity = broadInfo.GetAccessControl();
            broadSecurity.AddAccessRule(new FileSystemAccessRule(Everyone, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            broadInfo.SetAccessControl(broadSecurity);
            // A file created under the broad parent now inherits Everyone; that is the exposure restore must not copy.
            File.Delete(broadFile);
            File.WriteAllText(broadFile, "x");
            Assert.Contains(Everyone, Acl.AllowedPrincipals(Security(broadFile)));

            permissions.ApplyAccessControl(broadFile, isDirectory: false, permissions.CaptureAccessControl(narrowFile, isDirectory: false));

            var security = Security(broadFile);
            Assert.True(security.AreAccessRulesProtected);
            Assert.DoesNotContain(Everyone, Acl.AllowedPrincipals(security));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static FileSystemSecurity Security(string path) =>
        Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
}
