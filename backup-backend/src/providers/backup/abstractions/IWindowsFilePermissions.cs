namespace BackupPlatform.BackupProviders.Abstractions;

/// <summary>Windows-only filesystem operations supplied by the agent's Windows platform adapter.</summary>
public interface IWindowsFilePermissions
{
    /// <summary>Captures the effective DACL as protected SDDL so a restore parent cannot add inherited access.</summary>
    string CaptureAccessControl(string path, bool isDirectory);

    /// <summary>Rejects malformed descriptors and anything other than a protected, non-null DACL.</summary>
    void ValidateAccessControl(string descriptor);

    /// <summary>Restores the captured DACL without changing ownership or privileged audit policy.</summary>
    void ApplyAccessControl(string path, bool isDirectory, string descriptor);

    /// <summary>Creates an exclusively owned directory with a private ACL, or resets its ACL during rollback.</summary>
    void CreatePrivateDirectory(string path);
}
