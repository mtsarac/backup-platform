using System.Security.AccessControl;
using System.Security.Principal;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Platform.Windows.Identity;

namespace BackupPlatform.Agent.Platform.Windows.Tests;

/// <summary>What the store does with the ACLs and the path it is given: protect what it creates, never trust what already exists.</summary>
public sealed class WindowsStoreSecurityTests : IDisposable
{
    private const string FileName = "agent-identity.bin";
    private readonly TestDirectory _scratch = new();

    public void Dispose() => _scratch.Dispose();

    private static IReadOnlySet<SecurityIdentifier> Trusted => new HashSet<SecurityIdentifier> { Acl.CurrentUser(), Acl.System, Acl.Administrators };

    // ---- ACL of what the store creates ------------------------------------------------------------------------------------------

    [WindowsFact]
    public async Task ADirectoryTheStoreCreatesIsProtectedAndReachableOnlyByTheTrustedAccounts()
    {
        var directory = _scratch.Combine("new-store");
        await new WindowsAgentCredentialStore(directory).SaveAsync(TestIdentity.Create(), CancellationToken.None);

        var security = new DirectoryInfo(directory).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected, "The directory must not inherit permissions from its parent.");
        Assert.True(Acl.AllowedPrincipals(security).SetEquals(Trusted), "Only the account itself, SYSTEM and Administrators may have access.");
        Assert.DoesNotContain(Acl.AllowedPrincipals(security), sid => sid == Acl.Users);
    }

    [WindowsFact]
    public async Task TheIdentityFileInheritsTheProtectedAclAndItsOwnerIsTrusted()
    {
        var directory = _scratch.Combine("new-store");
        await new WindowsAgentCredentialStore(directory).SaveAsync(TestIdentity.Create(), CancellationToken.None);

        var file = new FileInfo(Path.Combine(directory, FileName));
        var security = file.GetAccessControl();
        Assert.True(Acl.AllowedPrincipals(security).SetEquals(Trusted));
        Assert.Contains((SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier))!, Trusted);
    }

    [WindowsFact]
    public async Task ReplacingTheFileKeepsTheProtectedAclOfTheDirectory()
    {
        var directory = _scratch.Combine("new-store");
        var store = new WindowsAgentCredentialStore(directory);
        await store.SaveAsync(TestIdentity.Create(), CancellationToken.None);
        await store.SaveAsync(TestIdentity.Create(generation: 2), CancellationToken.None);

        Assert.True(Acl.AllowedPrincipals(new FileInfo(Path.Combine(directory, FileName)).GetAccessControl()).SetEquals(Trusted));
    }

    // ---- existing directories ---------------------------------------------------------------------------------------------------

    [WindowsFact]
    public async Task AnExistingEmptyDirectoryWithALooseAclIsRepairedBeforeAnythingIsWritten()
    {
        var directory = _scratch.CreateDirectory("existing");
        Acl.GrantRead(directory, Acl.Users);
        Assert.Contains(Acl.Users, Acl.AllowedPrincipals(new DirectoryInfo(directory).GetAccessControl()));

        await new WindowsAgentCredentialStore(directory).SaveAsync(TestIdentity.Create(), CancellationToken.None);

        var security = new DirectoryInfo(directory).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        Assert.True(Acl.AllowedPrincipals(security).SetEquals(Trusted));
        Assert.True(Acl.AllowedPrincipals(new FileInfo(Path.Combine(directory, FileName)).GetAccessControl()).SetEquals(Trusted));
    }

    [WindowsFact]
    public async Task ALooseAclThatAppearsLaterIsRepairedOnTheNextReadAndTheIdentityIsStillReturned()
    {
        var directory = _scratch.Combine("store");
        var identity = TestIdentity.Create();
        var store = new WindowsAgentCredentialStore(directory);
        await store.SaveAsync(identity, CancellationToken.None);
        Acl.GrantRead(directory, Acl.Users);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(identity.Current, loaded!.Current);
        Assert.True(Acl.AllowedPrincipals(new DirectoryInfo(directory).GetAccessControl()).SetEquals(Trusted));
        Assert.True(Acl.AllowedPrincipals(new FileInfo(Path.Combine(directory, FileName)).GetAccessControl()).SetEquals(Trusted));
    }

    [WindowsFact]
    public async Task ALooseDirectoryThatHoldsOtherFilesIsRefusedAndLeftExactlyAsItWas()
    {
        var directory = _scratch.CreateDirectory("shared");
        var foreign = Path.Combine(directory, "notes.txt");
        await File.WriteAllTextAsync(foreign, "not the agent's");
        Acl.GrantRead(directory, Acl.Users);
        var before = new DirectoryInfo(directory).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        var store = new WindowsAgentCredentialStore(directory);

        var save = await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.SaveAsync(TestIdentity.Create(), CancellationToken.None));
        Assert.Contains("was not changed", save.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.LoadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.DeleteAsync(CancellationToken.None));

        Assert.Equal(before, new DirectoryInfo(directory).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Assert.Equal("not the agent's", await File.ReadAllTextAsync(foreign));
        Assert.Equal(["notes.txt"], TestDirectory.Names(directory));
    }

    [WindowsFact]
    public async Task ALooseDirectoryWithASubdirectoryIsNotMistakenForAStore()
    {
        var directory = _scratch.CreateDirectory("shared");
        Directory.CreateDirectory(Path.Combine(directory, "sub"));
        Acl.GrantRead(directory, Acl.Users);

        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => new WindowsAgentCredentialStore(directory).SaveAsync(TestIdentity.Create(), CancellationToken.None));
        Assert.Contains(Acl.Users, Acl.AllowedPrincipals(new DirectoryInfo(directory).GetAccessControl()));
    }

    [WindowsFact]
    public async Task AFileWithALooseAclIsRefusedOnLoadAndReplacedByTheNextSave()
    {
        var directory = _scratch.Combine("store");
        var store = new WindowsAgentCredentialStore(directory);
        await store.SaveAsync(TestIdentity.Create(), CancellationToken.None);
        var file = new FileInfo(Path.Combine(directory, FileName));
        Acl.GrantRead(file, Acl.Users);

        var exception = await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.LoadAsync(CancellationToken.None));
        Assert.Contains("allow other accounts", exception.Message, StringComparison.Ordinal);

        var replacement = TestIdentity.Create(generation: 2);
        await store.SaveAsync(replacement, CancellationToken.None);
        Assert.Equal(replacement.Current, (await store.LoadAsync(CancellationToken.None))!.Current);
        Assert.True(Acl.AllowedPrincipals(new FileInfo(file.FullName).GetAccessControl()).SetEquals(Trusted));
    }

    [WindowsFact]
    public void TheAclAssessmentFlagsEveryUntrustedAllowRuleAndIgnoresDenyRules()
    {
        var current = Acl.CurrentUser();
        var security = WindowsStoreDirectory.CreateProtectedSecurity(current);
        Assert.Equal(StoreAclProblem.UntrustedOwner, WindowsStoreDirectory.Assess(WithOwner(security, Acl.Users), current));

        var directory = _scratch.CreateDirectory("probe");
        var info = new DirectoryInfo(directory);
        info.SetAccessControl(WindowsStoreDirectory.CreateProtectedSecurity(current));
        Assert.Equal(StoreAclProblem.None, WindowsStoreDirectory.Assess(info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), current));

        var withDeny = info.GetAccessControl();
        withDeny.AddAccessRule(new FileSystemAccessRule(Acl.Users, FileSystemRights.Write, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
        info.SetAccessControl(withDeny);
        Assert.Equal(StoreAclProblem.None, WindowsStoreDirectory.Assess(info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), current));

        foreach (var sid in new[] { Acl.Users, new SecurityIdentifier(WellKnownSidType.WorldSid, null), new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null) })
        {
            var loose = info.GetAccessControl();
            loose.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadData, AccessControlType.Allow));
            info.SetAccessControl(loose);
            var problem = WindowsStoreDirectory.Assess(info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), current);
            Assert.True(problem == StoreAclProblem.UntrustedAccess, $"{sid.Value} was not flagged: {problem}");
            info.SetAccessControl(WindowsStoreDirectory.CreateProtectedSecurity(current));
        }
    }

    // ---- reparse points ---------------------------------------------------------------------------------------------------------

    [WindowsFact]
    public async Task AJunctionAsTheStoreDirectoryIsRefusedForEveryOperationAndItsTargetIsUntouched()
    {
        var target = _scratch.CreateDirectory("target");
        var link = _scratch.Combine("link");
        Links.Junction(link, target);

        await AssertEveryOperationRefusedAsync(link);

        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [WindowsSymlinkFact]
    public async Task ASymbolicLinkAsTheStoreDirectoryIsRefusedForEveryOperationAndItsTargetIsUntouched()
    {
        var target = _scratch.CreateDirectory("target");
        var link = _scratch.Combine("link");
        Links.Symbolic(link, target, directory: true);

        await AssertEveryOperationRefusedAsync(link);

        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [WindowsSymlinkFact]
    public async Task ADanglingSymbolicLinkAsTheStoreDirectoryIsRefusedAndNothingIsCreatedAtItsTarget()
    {
        var target = _scratch.Combine("does-not-exist-yet");
        var link = _scratch.Combine("link");
        Links.Symbolic(link, target, directory: true);

        await AssertEveryOperationRefusedAsync(link);

        Assert.False(Directory.Exists(target));
    }

    [WindowsSymlinkFact]
    public async Task ASymbolicLinkAsTheIdentityFileIsRefusedAndItsTargetIsNeverReadOrReplaced()
    {
        var directory = _scratch.Combine("store");
        var store = new WindowsAgentCredentialStore(directory);
        await store.SaveAsync(TestIdentity.Create(), CancellationToken.None);
        var outside = _scratch.Combine("outside.bin");
        await File.WriteAllBytesAsync(outside, [1, 2, 3, 4]);
        var file = Path.Combine(directory, FileName);
        File.Delete(file);
        Links.Symbolic(file, outside, directory: false);

        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.LoadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.SaveAsync(TestIdentity.Create(), CancellationToken.None));
        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.DeleteAsync(CancellationToken.None));

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(outside));
        Assert.NotEqual(0, (int)(File.GetAttributes(file) & FileAttributes.ReparsePoint));
        Assert.Empty(Directory.GetFiles(directory, ".agent-identity.bin.*"));
    }

    [WindowsSymlinkFact]
    public async Task ADanglingSymbolicLinkAsTheIdentityFileIsRefusedNotTreatedAsNothingStored()
    {
        var directory = _scratch.CreateDirectory("store");
        var store = new WindowsAgentCredentialStore(directory);
        await store.SaveAsync(TestIdentity.Create(), CancellationToken.None);
        var file = Path.Combine(directory, FileName);
        File.Delete(file);
        var target = _scratch.Combine("never-created.bin");
        Links.Symbolic(file, target, directory: false);

        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.LoadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.SaveAsync(TestIdentity.Create(), CancellationToken.None));

        Assert.False(File.Exists(target));
    }

    // ---- reparse points among the ancestors -------------------------------------------------------------------------------------

    [WindowsFact]
    public async Task AJunctionAmongTheAncestorsIsNotFollowedAndNothingIsWrittenBehindIt()
    {
        var target = _scratch.CreateDirectory("elsewhere");
        var link = _scratch.Combine("safe-looking");
        Links.Junction(link, target);
        var store = new WindowsAgentCredentialStore(Path.Combine(link, "AgentStore"));

        var exception = await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.SaveAsync(TestIdentity.Create(), CancellationToken.None));

        Assert.Contains("reached through", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(target));
        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => new WindowsAgentCredentialStore(Path.Combine(link, "AgentStore")).DeleteAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [WindowsSymlinkFact]
    public async Task ASymbolicLinkAmongTheAncestorsIsNotFollowedAndNothingIsWrittenBehindIt()
    {
        var target = _scratch.CreateDirectory("elsewhere");
        var link = _scratch.Combine("safe-looking");
        Links.Symbolic(link, target, directory: true);

        var store = new WindowsAgentCredentialStore(Path.Combine(link, "nested", "AgentStore"));
        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.SaveAsync(TestIdentity.Create(), CancellationToken.None));

        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [WindowsFact]
    public async Task AnIdentityPlantedBehindAnAncestorLinkIsNeverLoaded()
    {
        var target = _scratch.CreateDirectory("elsewhere");
        // What an attacker would prepare: a store directory with a blob in it, reachable through the link.
        await new WindowsAgentCredentialStore(Path.Combine(target, "AgentStore")).SaveAsync(TestIdentity.Create(), CancellationToken.None);
        var link = _scratch.Combine("safe-looking");
        Links.Junction(link, target);

        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => new WindowsAgentCredentialStore(Path.Combine(link, "AgentStore")).LoadAsync(CancellationToken.None));
    }

    [WindowsSymlinkFact]
    public async Task ADanglingAncestorLinkIsRefusedAndNothingIsCreatedAtItsTarget()
    {
        var target = _scratch.Combine("not-there");
        var link = _scratch.Combine("dangling");
        Links.Symbolic(link, target, directory: true);

        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => new WindowsAgentCredentialStore(Path.Combine(link, "AgentStore")).SaveAsync(TestIdentity.Create(), CancellationToken.None));

        Assert.False(Directory.Exists(target));
    }

    [WindowsFact]
    public async Task APathWithoutAnyLinkAmongItsAncestorsIsAccepted()
    {
        var store = new WindowsAgentCredentialStore(Path.Combine(_scratch.Root, "a", "b", "c", "AgentStore"));
        var identity = TestIdentity.Create();

        await store.SaveAsync(identity, CancellationToken.None);

        Assert.Equal(identity.Current, (await store.LoadAsync(CancellationToken.None))!.Current);
    }

    // -----------------------------------------------------------------------------------------------------------------------------

    private static async Task AssertEveryOperationRefusedAsync(string directory)
    {
        var store = new WindowsAgentCredentialStore(directory);
        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.LoadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.SaveAsync(TestIdentity.Create(), CancellationToken.None));
        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.DeleteAsync(CancellationToken.None));
    }

    private static FileSystemSecurity WithOwner(DirectorySecurity security, SecurityIdentifier owner)
    {
        security.SetOwner(owner);
        return security;
    }
}
