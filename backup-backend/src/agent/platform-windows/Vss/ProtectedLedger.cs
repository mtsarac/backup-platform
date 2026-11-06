using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BackupPlatform.Agent.Platform.Windows.Vss;

/// <summary>
/// The ownership ledger decides which snapshots the agent may delete, so only the agent's own account, SYSTEM and Administrators may write it.
/// It is created with exactly that protected ACL and verified (owner-independent) before every sweep; a link or a loosened ACL makes the sweep refuse.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProtectedLedger
{
    internal const string Marker = "bp-vss-owned-v1";
    private const FileSystemRights Writes = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    public static void Ensure(string directory)
    {
        if (Directory.Exists(directory)) { Verify(directory); return; }
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in Trusted())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).Create(security);
        Verify(directory);
    }

    public static void Verify(string directory)
    {
        var info = new DirectoryInfo(directory);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("The VSS ownership ledger is a link.");
        var trusted = Trusted().Select(sid => sid.Value).ToHashSet(StringComparer.Ordinal);
        // The owner can rewrite the ACL whatever it says, so a foreign owner makes the ACL meaningless.
        if (info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !trusted.Contains(owner.Value))
            throw new UnauthorizedAccessException("The VSS ownership ledger is owned by an untrusted account.");
        foreach (FileSystemAccessRule rule in info.GetAccessControl(AccessControlSections.Access).GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & Writes) != 0 && !trusted.Contains(rule.IdentityReference.Value))
                throw new UnauthorizedAccessException("The VSS ownership ledger is writable by an untrusted account.");
    }

    /// <summary>An entry written by the agent starts with the marker; anything else in the directory is ignored.</summary>
    public static bool IsEntry(string path)
    {
        try { using var reader = new StreamReader(path); return reader.ReadLine() == Marker; }
        catch (IOException) { return false; }
    }

    private static IEnumerable<SecurityIdentifier> Trusted() =>
    [
        WindowsIdentity.GetCurrent().User!,
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
    ];
}
