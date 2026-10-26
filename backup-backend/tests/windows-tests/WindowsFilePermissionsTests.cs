using System.Security.AccessControl;
using System.Security.Principal;
using BackupPlatform.BackupProviders.FileSystem;

namespace BackupPlatform.Agent.Platform.Windows.Tests;

public sealed class WindowsFilePermissionsTests
{
    [WindowsFact]
    public async Task Restore_preserves_inherited_allow_and_deny_rules_without_inheriting_broader_destination_access()
    {
        var root = Path.Combine(Path.GetTempPath(), "bp-windows-files-" + Guid.NewGuid().ToString("N"));
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
            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            var guests = new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null);
            var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            var sourceDirectory = new DirectoryInfo(source);
            var sourceSecurity = sourceDirectory.GetAccessControl();
            sourceSecurity.AddAccessRule(new FileSystemAccessRule(guests, FileSystemRights.ReadAndExecute,
                inheritance, PropagationFlags.None, AccessControlType.Deny));
            sourceDirectory.SetAccessControl(sourceSecurity);
            Directory.CreateDirectory(Path.Combine(source, "private"));
            await File.WriteAllTextAsync(Path.Combine(source, "private", "data.txt"), "restricted data");
            var originalFile = new FileInfo(Path.Combine(source, "private", "data.txt"));
            Assert.Contains(originalFile.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>(), rule => rule.IsInherited && rule.AccessControlType == AccessControlType.Deny);

            var targetDirectory = new DirectoryInfo(target);
            var targetSecurity = targetDirectory.GetAccessControl();
            targetSecurity.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.FullControl,
                inheritance, PropagationFlags.None, AccessControlType.Allow));
            targetDirectory.SetAccessControl(targetSecurity);
            var provider = new FileSystemBackupProvider([], permissions);
            var (manifest, archivePath) = await provider.CreateBackupAsync(new(source, ["**"], []), staging, null, default);
            Assert.All(manifest.Files, entry => Assert.NotNull(entry.WindowsAccessControl));

            await provider.RestoreAsync(archivePath, target, false, null, null, default);

            foreach (var entry in manifest.Files)
            {
                var restored = Path.Combine(target, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Assert.Equal(entry.WindowsAccessControl, permissions.CaptureAccessControl(restored, entry.IsDirectory));
                FileSystemSecurity security = entry.IsDirectory
                    ? new DirectoryInfo(restored).GetAccessControl()
                    : new FileInfo(restored).GetAccessControl();
                Assert.True(security.AreAccessRulesProtected);
                var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
                Assert.DoesNotContain(rules, rule => rule.IdentityReference.Equals(everyone) && rule.AccessControlType == AccessControlType.Allow);
                Assert.Contains(rules, rule => rule.IdentityReference.Equals(guests) && rule.AccessControlType == AccessControlType.Deny);
            }
            Assert.Equal("restricted data", await File.ReadAllTextAsync(Path.Combine(target, "private", "data.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [WindowsFact]
    public void Restore_rejects_unprotected_null_or_privileged_security_descriptors()
    {
        var permissions = new WindowsFilePermissions();
        foreach (var descriptor in new[] { "D:(A;;FA;;;WD)", "D:PNO_ACCESS_CONTROL", "O:SYD:P(A;;FA;;;SY)", "invalid" })
            Assert.Throws<InvalidDataException>(() => permissions.ValidateAccessControl(descriptor));
    }
}
