using System.Security.AccessControl;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Platform.Windows.Identity;

namespace BackupPlatform.Agent.Platform.Windows.Tests;

/// <summary>
/// Different Windows accounts, proven with a real second account: DPAPI CurrentUser protection is what separates the agent's service
/// identity from every other identity, and a directory owned by another account is never used. These tests create a throw-away local
/// account and therefore need an elevated session; otherwise they are skipped with that reason, never passed.
/// </summary>
public sealed class WindowsStoreAccountTests : IClassFixture<WindowsStoreAccountTests.AccountFixture>, IDisposable
{
    private readonly AccountFixture _fixture;
    // Below ProgramData (not the temp directory of the running user) so that the other account can reach it.
    private readonly TestDirectory _scratch = new(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    public WindowsStoreAccountTests(AccountFixture fixture) => _fixture = fixture;

    public void Dispose() => _scratch.Dispose();

    /// <summary>One account for the whole class, created lazily so skipped runs create nothing.</summary>
    public sealed class AccountFixture : IDisposable
    {
        private readonly Lazy<LocalAccount> _account = new(() => new LocalAccount());

        public LocalAccount Account => _account.Value;

        public void Dispose()
        {
            if (_account.IsValueCreated)
            {
                _account.Value.Dispose();
            }
        }
    }

    [WindowsAdminFact]
    public void TheOtherAccountReallyRunsAsADifferentIdentity()
    {
        var other = _fixture.Account.RunProbe("whoami");
        var own = Probe.Run("whoami");

        Assert.Equal($"OK {_fixture.Account.Sid}", other.Output);
        Assert.Equal($"OK {Acl.CurrentUser()}", own.Output);
        Assert.NotEqual(own.Output, other.Output);
    }

    [WindowsAdminFact]
    public async Task AnotherAccountThatCanReadTheFileStillCannotDecryptIt()
    {
        var directory = _scratch.Combine("store");
        await new WindowsAgentCredentialStore(directory).SaveAsync(TestIdentity.Create(), CancellationToken.None);
        var file = Path.Combine(directory, "agent-identity.bin");

        // Deliberately weaken the ACL (the way a misconfiguration or a copy would) so that file permissions are not what stops the
        // other account: only DPAPI is left.
        Acl.GrantRead(directory, _fixture.Account.Sid);
        Acl.GrantRead(new FileInfo(file), _fixture.Account.Sid);
        Acl.GrantRead(_scratch.Root, _fixture.Account.Sid);

        var control = Probe.Run("unprotect", file);
        var other = _fixture.Account.RunProbe("unprotect", file);

        Assert.True(control.ExitCode == 0 && control.Output.StartsWith("OK ", StringComparison.Ordinal), $"The owning account must be able to decrypt (control): {control.Output}");
        Assert.Equal(12, other.ExitCode);
        Assert.Equal("DPAPI-REFUSED", other.Output);
    }

    [WindowsAdminFact]
    public async Task AnotherAccountsStoreRefusesTheDirectoryAndNeverReturnsTheIdentity()
    {
        var directory = _scratch.Combine("store");
        var identity = TestIdentity.Create();
        await new WindowsAgentCredentialStore(directory).SaveAsync(identity, CancellationToken.None);
        Acl.GrantRead(directory, _fixture.Account.Sid);
        Acl.GrantRead(new FileInfo(Path.Combine(directory, "agent-identity.bin")), _fixture.Account.Sid);
        Acl.GrantRead(_scratch.Root, _fixture.Account.Sid);
        var directoryInfo = new DirectoryInfo(directory);
        var before = directoryInfo.GetAccessControl().GetSecurityDescriptorBinaryForm();

        var other = _fixture.Account.RunProbe("load", directory);

        Assert.NotEqual(0, other.ExitCode);
        Assert.DoesNotContain(identity.Current.Secret, other.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("OK ", other.Output, StringComparison.Ordinal);
        // A read by another account must not alter the ACL. Restore the deliberately weakened ACL before using the hardened store.
        Assert.Equal(before, directoryInfo.GetAccessControl().GetSecurityDescriptorBinaryForm());
        directoryInfo.SetAccessControl(WindowsStoreDirectory.CreateProtectedSecurity(Acl.CurrentUser()));
        var fileInfo = new FileInfo(Path.Combine(directory, "agent-identity.bin"));
        var fileSecurity = fileInfo.GetAccessControl();
        fileSecurity.RemoveAccessRuleAll(new FileSystemAccessRule(_fixture.Account.Sid, FileSystemRights.Read, AccessControlType.Allow));
        fileInfo.SetAccessControl(fileSecurity);
        Assert.Equal(identity.Current, (await new WindowsAgentCredentialStore(directory).LoadAsync(CancellationToken.None))!.Current);
    }

    [WindowsAdminFact]
    public async Task ADirectoryOwnedByAnotherAccountIsRefusedEvenThoughItsAclLooksFine()
    {
        // The other account creates the directory (so it owns it) inside a parent that grants it that right.
        Acl.GrantRead(_scratch.Root, _fixture.Account.Sid);
        GrantModify(_scratch.Root, _fixture.Account.Sid);
        var directory = _scratch.Combine("owned-by-other");
        var created = _fixture.Account.RunProbe("mkdir", directory);
        Assert.True(created.ExitCode == 0, created.Output);
        var owner = new DirectoryInfo(directory).GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier));
        Assert.Equal(_fixture.Account.Sid, owner);

        var store = new WindowsAgentCredentialStore(directory);
        var exception = await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.SaveAsync(TestIdentity.Create(), CancellationToken.None));

        Assert.Contains("owned by an account the agent does not trust", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(directory));
        await Assert.ThrowsAsync<AgentCredentialStoreException>(() => store.LoadAsync(CancellationToken.None));
    }

    private static void GrantModify(string directory, System.Security.Principal.SecurityIdentifier sid)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            sid,
            System.Security.AccessControl.FileSystemRights.Modify,
            System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
            System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}
