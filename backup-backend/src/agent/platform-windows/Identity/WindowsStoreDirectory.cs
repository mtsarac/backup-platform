using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using BackupPlatform.Agent.Core.Identity;

namespace BackupPlatform.Agent.Platform.Windows.Identity;

/// <summary>
/// The agent's identity directory and the two questions that decide whether it may be used at all: is the path itself trustworthy
/// (no component of it is a symbolic link, junction or other reparse point) and is the directory reachable only by the accounts the
/// store trusts. Nothing here is silent: a directory is either verified, repaired by a rule that cannot widen access, or refused.
/// </summary>
/// <remarks>
/// <para>
/// Trusted principals: the account the process runs as (the service identity), LocalSystem and the local Administrators group. DPAPI
/// <c>CurrentUser</c> binds the blob to the service identity; it is not protection against a local administrator who can impersonate
/// that identity. The directory ACL keeps ordinary accounts from replacing, deleting or even listing the file.
/// </para>
/// <para>
/// A pre-existing directory is never assumed safe. Its owner must be trusted (the owner can always rewrite the ACL, so a directory owned
/// by anyone else stays under that account's control whatever its ACL says). A trusted owner with a loose ACL is repaired to the same
/// protected ACL a new directory gets, but only when the directory contains nothing except the store's own files: a store location
/// pointed at a directory that holds other data is refused rather than having its permissions rewritten.
/// </para>
/// <para>
/// Reparse points are checked for the directory and every ancestor, top-down, on each operation. This does not close the window between
/// the check and the use (only a handle-based walk could), which matters only to an attacker who can already rename directories on the
/// path; the ACL requirements above are what keep such an account out of the store directory itself.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsStoreDirectory
{
    internal const string IdentityFileName = "agent-identity.bin";

    /// <summary>Prefix and suffix of the temp files the store writes next to the identity file before swapping them in.</summary>
    internal const string TemporaryPrefix = "." + IdentityFileName + ".";
    internal const string TemporarySuffix = ".tmp";

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public WindowsStoreDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!System.IO.Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The store directory must be an absolute path.", nameof(path));
        }

        Location = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
    }

    /// <summary>The full, normalised directory path, without a trailing separator.</summary>
    public string Location { get; }

    public string FilePath => System.IO.Path.Combine(Location, IdentityFileName);

    /// <summary>
    /// Makes the directory usable and returns whether it exists. Without <paramref name="create"/> a missing directory is not an error
    /// (nothing is stored yet); with it, the directory is created with the protected ACL. An existing directory is verified and, where
    /// allowed (see the type remarks), repaired. Throws <see cref="AgentCredentialStoreException"/> when it cannot be trusted.
    /// </summary>
    public bool Prepare(bool create)
    {
        VerifyPathHasNoReparsePoints();
        var directory = new DirectoryInfo(Location);
        if (!directory.Exists)
        {
            if (!create)
            {
                return false;
            }

            Create(directory);
            VerifyPathHasNoReparsePoints();
            directory.Refresh();
        }

        VerifyDirectorySecurity(directory);
        return true;
    }

    /// <summary>Refuses a file that is a link or whose owner or ACL admits an account the store does not trust.</summary>
    public void VerifyIdentityFile(string path)
    {
        if (IsReparsePoint(path) is true)
        {
            throw new AgentCredentialStoreException("The agent identity location must not be a symbolic link or junction.");
        }

        try
        {
            var file = new FileInfo(path);
            if (Assess(file.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), CurrentUser()) != StoreAclProblem.None)
            {
                throw new AgentCredentialStoreException("The agent identity file has permissions that allow other accounts access.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new AgentCredentialStoreException("The permissions of the agent identity file could not be verified.", exception);
        }
    }

    /// <summary>True when <paramref name="path"/> is a reparse point itself (without following it), false when it is not, null when it does not exist.</summary>
    internal static bool? IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentCredentialStoreException("The agent identity location could not be inspected.", exception);
        }
    }

    internal static StoreAclProblem Assess(FileSystemSecurity security, SecurityIdentifier current)
    {
        ArgumentNullException.ThrowIfNull(security);
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsTrusted(owner, current))
        {
            return StoreAclProblem.UntrustedOwner;
        }

        // Allow rules decide who can reach the directory, whether set on it or inherited. Deny rules can only narrow access, so they
        // do not make a directory unsafe.
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && (rule.IdentityReference is not SecurityIdentifier sid || !IsTrusted(sid, current)))
            {
                return StoreAclProblem.UntrustedAccess;
            }
        }

        return StoreAclProblem.None;
    }

    internal static DirectorySecurity CreateProtectedSecurity(SecurityIdentifier current)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var principal in new[] { current, LocalSystem, Administrators }.Distinct())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                principal,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    internal static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new AgentCredentialStoreException("The account the agent runs as has no security identifier.");
    }

    private static bool IsTrusted(SecurityIdentifier sid, SecurityIdentifier current) =>
        sid == current || sid == LocalSystem || sid == Administrators;

    private void Create(DirectoryInfo directory)
    {
        try
        {
            directory.Create(CreateProtectedSecurity(CurrentUser()));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentCredentialStoreException("The agent identity directory could not be created.", exception);
        }
    }

    /// <summary>Walks the path top-down so that the first link on it is the one reported; components that do not exist yet are skipped.</summary>
    private void VerifyPathHasNoReparsePoints()
    {
        var root = System.IO.Path.GetPathRoot(Location) ?? string.Empty;
        var components = new Stack<string>();
        for (var current = Location; !string.IsNullOrEmpty(current) && !SamePath(current, root); current = System.IO.Path.GetDirectoryName(current))
        {
            components.Push(current);
        }

        foreach (var component in components)
        {
            if (IsReparsePoint(component) is true)
            {
                throw new AgentCredentialStoreException(
                    "The agent identity location must not be, or be reached through, a symbolic link, junction or mount point.");
            }
        }
    }

    private void VerifyDirectorySecurity(DirectoryInfo directory)
    {
        var current = CurrentUser();
        try
        {
            var problem = Assess(directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), current);
            if (problem == StoreAclProblem.None)
            {
                return;
            }

            if (problem == StoreAclProblem.UntrustedOwner)
            {
                throw new AgentCredentialStoreException(
                    "The agent identity directory is owned by an account the agent does not trust and cannot be made safe.");
            }

            if (!ContainsOnlyStoreFiles(directory))
            {
                throw new AgentCredentialStoreException(
                    "The agent identity directory has permissions that allow other accounts access and holds files that are not the agent's, so it was not changed.");
            }

            directory.SetAccessControl(CreateProtectedSecurity(current));
            if (Assess(directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), current) != StoreAclProblem.None)
            {
                throw new AgentCredentialStoreException("The agent identity directory permissions could not be restricted.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new AgentCredentialStoreException("The permissions of the agent identity directory could not be verified or restricted.", exception);
        }
    }

    private static bool ContainsOnlyStoreFiles(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            var name = entry.Name;
            var isStoreFile = entry is FileInfo
                && (name == IdentityFileName || name == ".enrollment.lock" || name == "client-certificate.pfx"
                    || (name.StartsWith("client-certificate.pfx.", StringComparison.Ordinal) && Guid.TryParseExact(name["client-certificate.pfx.".Length..], "N", out _))
                    || (name.StartsWith(TemporaryPrefix, StringComparison.Ordinal) && name.EndsWith(TemporarySuffix, StringComparison.Ordinal)));
            if (!isStoreFile)
            {
                return false;
            }
        }

        return true;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            System.IO.Path.TrimEndingDirectorySeparator(left),
            System.IO.Path.TrimEndingDirectorySeparator(right),
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>What is wrong with the permissions of a store location, if anything.</summary>
internal enum StoreAclProblem
{
    None = 0,

    /// <summary>The owner is not the agent's account, LocalSystem or Administrators; an owner can always rewrite the ACL, so this cannot be repaired from here.</summary>
    UntrustedOwner,

    /// <summary>An allow rule (set or inherited) admits an account the store does not trust.</summary>
    UntrustedAccess
}
