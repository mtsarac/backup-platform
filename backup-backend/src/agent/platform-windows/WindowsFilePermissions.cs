using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using BackupPlatform.BackupProviders.Abstractions;

namespace BackupPlatform.Agent.Platform.Windows;

/// <summary>Preserves Windows access restrictions independently of the restore destination's inherited ACL.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFilePermissions : IWindowsFilePermissions
{
    /// <inheritdoc />
    public string CaptureAccessControl(string path, bool isDirectory)
    {
        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        // Freeze effective inherited rules; another parent must never broaden access to restored data.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        var descriptor = security.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        ValidateAccessControl(descriptor);
        return descriptor;
    }

    /// <inheritdoc />
    public void ValidateAccessControl(string descriptor)
    {
        try
        {
            var security = new RawSecurityDescriptor(descriptor);
            if (security.Owner is not null || security.Group is not null || security.SystemAcl is not null
                || security.DiscretionaryAcl is null
                || !security.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclPresent)
                || !security.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected))
                throw new InvalidDataException("The file manifest must contain only a protected, non-null Windows DACL.");
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("The file manifest contains an invalid Windows DACL.", error);
        }
    }

    /// <inheritdoc />
    public void ApplyAccessControl(string path, bool isDirectory, string descriptor)
    {
        ValidateAccessControl(descriptor);
        if (isDirectory)
        {
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorSddlForm(descriptor, AccessControlSections.Access);
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else
        {
            var security = new FileSecurity();
            security.SetSecurityDescriptorSddlForm(descriptor, AccessControlSections.Access);
            new FileInfo(path).SetAccessControl(security);
        }
    }

    /// <inheritdoc />
    public void CreatePrivateDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var current = identity.User ?? throw new UnauthorizedAccessException("The agent account has no Windows security identifier.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(path);
        if (directory.Exists) directory.SetAccessControl(security);
        else directory.Create(security);
    }
}
