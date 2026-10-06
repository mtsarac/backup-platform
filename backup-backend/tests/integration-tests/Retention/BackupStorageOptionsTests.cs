using BackupPlatform.Api.Backups;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// The storage the backend may delete from is the one place an operator mistake becomes data loss, so a bad setting must stop the host at startup
/// with a message that names the setting without echoing its value. (Lives with the API tests because the options type belongs to the API host.)
/// </summary>
public sealed class BackupStorageOptionsTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "backup-storage-options"));
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static BackupStorageOptions Central(string? root, params (string Tenant, string Scope)[] scopes) => new()
    {
        Central = new BackupCentralStorageSettings { RootDirectory = root, TenantScopes = scopes.ToDictionary(entry => entry.Tenant, entry => entry.Scope) }
    };

    [Fact]
    public void NothingConfiguredIsValidAndMeansTheBackendReachesNoStorage()
    {
        Assert.Empty(new BackupStorageOptions().Validate());
        Assert.Empty(new BackupStorageOptions { Local = new BackupLocalStorageSettings(), Central = new BackupCentralStorageSettings() }.Validate());
    }

    [Fact]
    public void AbsoluteRootsAndValidScopesAreAccepted()
    {
        var options = Central(Root, (TenantA.ToString(), "tenant-a"), (TenantB.ToString("N"), "tenant-b"));
        options.Local = new BackupLocalStorageSettings { RootDirectory = Root };

        Assert.Empty(options.Validate());
        options.ThrowIfInvalid();
    }

    [Theory]
    [InlineData("relative/dir")]
    [InlineData("./here")]
    [InlineData("")]
    public void ARelativeLocalRootIsRejected(string root)
    {
        var options = new BackupStorageOptions { Local = new BackupLocalStorageSettings { RootDirectory = root } };

        var problem = Assert.Single(options.Validate());
        Assert.Contains("Backups:Storage:Local:RootDirectory", problem);
        Assert.Throws<InvalidOperationException>(options.ThrowIfInvalid);
    }

    [Fact]
    public void ARelativeCentralRootIsRejectedWithAndWithoutTenantScopes()
    {
        var bare = Central("relative/dir");
        var scoped = Central("relative/dir", (TenantA.ToString(), "tenant-a"));

        Assert.Contains("Backups:Storage:Central:RootDirectory", Assert.Single(bare.Validate()));
        Assert.NotEmpty(scoped.Validate());
    }

    [Fact]
    public void TenantScopesWithoutARootAreRejected()
    {
        var problem = Assert.Single(Central(null, (TenantA.ToString(), "tenant-a")).Validate());

        Assert.Contains("Backups:Storage:Central:RootDirectory", problem);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void ATenantKeyThatIsNotATenantIdIsRejected(string tenant)
    {
        var problem = Assert.Single(Central(Root, (tenant, "tenant-a")).Validate());

        Assert.Contains("Backups:Storage:Central:TenantScopes", problem);
    }

    [Theory]
    [InlineData("Upper")]      // scopes are lowercase so two scopes never share a directory on a case-insensitive file system
    [InlineData("a/b")]        // one key segment only: a scope cannot descend or climb
    [InlineData("..")]
    [InlineData("")]
    public void AScopeThatIsNotOneLowercaseSegmentIsRejected(string scope)
    {
        var problem = Assert.Single(Central(Root, (TenantA.ToString(), scope)).Validate());

        Assert.Contains("Backups:Storage:Central:TenantScopes", problem);
    }

    [Fact]
    public void TwoTenantsOnOneScopeAreRejectedBecauseTheyWouldShareADirectory()
    {
        var problem = Assert.Single(Central(Root, (TenantA.ToString(), "shared"), (TenantB.ToString(), "shared")).Validate());

        Assert.Contains("same scope", problem);
    }

    [Fact]
    public void OneTenantWrittenTwoWaysIsRejected()
    {
        var problem = Assert.Single(Central(Root, (TenantA.ToString("D"), "tenant-a"), (TenantA.ToString("N"), "tenant-a-again")).Validate());

        Assert.Contains("same tenant", problem);
    }

    [Fact]
    public void ProblemMessagesNeverEchoConfiguredValues()
    {
        const string secret = "TopSecretMarker";
        var options = Central("relative/" + secret, (secret, secret));
        options.Local = new BackupLocalStorageSettings { RootDirectory = "relative/" + secret };

        var all = string.Join(' ', options.Validate()) + Assert.Throws<InvalidOperationException>(options.ThrowIfInvalid).Message;

        Assert.DoesNotContain(secret, all, StringComparison.OrdinalIgnoreCase);
    }
}
