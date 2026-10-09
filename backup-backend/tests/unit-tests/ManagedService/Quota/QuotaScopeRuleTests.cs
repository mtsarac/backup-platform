using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Storage;
using BackupPlatform.ManagedService;
using BackupPlatform.ManagedService.Quota;

namespace BackupPlatform.UnitTests.ManagedService.Quota;

public sealed class QuotaScopeRuleTests
{
    /// <summary>A replica on a connection that looks like the system defaults: central+Backend is platform-managed ("managed"), everything else tenant-managed ("tenant").</summary>
    private static ReplicaStorageFacts Replica(string provider, BackupStorageExecutionLocation location = BackupStorageExecutionLocation.Backend)
    {
        var platform = provider == "central" && location == BackupStorageExecutionLocation.Backend;
        return new(provider, platform ? StorageOwnership.PlatformManaged : StorageOwnership.TenantManaged, platform ? "managed" : "tenant", Guid.NewGuid());
    }

    private static QuotaScopeRule Rule(QuotaScopeKind kind, params string[] values) => QuotaScopeRule.Create([new QuotaScopeSelector(kind, values)]);

    private static string CodeOf(Action action) => Assert.Throws<InvalidManagedServiceRequestException>(action).Code;

    [Fact]
    public void SelectorsReadTheConnectionsFactsAndNothingElse()
    {
        var id = Guid.NewGuid();
        // Same provider type, different connection data: a rule can tell them apart, which is the whole point of storing it on the connection.
        var managed = new ReplicaStorageFacts("central", StorageOwnership.PlatformManaged, "managed", id);
        var premium = new ReplicaStorageFacts("central", StorageOwnership.TenantManaged, "premium", Guid.NewGuid());

        Assert.True(Rule(QuotaScopeKind.QuotaScope, "managed").Matches(managed));
        Assert.False(Rule(QuotaScopeKind.QuotaScope, "managed").Matches(premium));
        Assert.True(Rule(QuotaScopeKind.QuotaScope, "premium").Matches(premium));
        Assert.True(Rule(QuotaScopeKind.OwnershipType, "PlatformManaged").Matches(managed));
        Assert.False(Rule(QuotaScopeKind.OwnershipType, "PlatformManaged").Matches(premium));
        Assert.True(Rule(QuotaScopeKind.ProviderType, "central").Matches(premium));
        Assert.True(Rule(QuotaScopeKind.StorageConnection, id.ToString("D")).Matches(managed));
        Assert.False(Rule(QuotaScopeKind.StorageConnection, id.ToString("D")).Matches(premium));
    }

    [Fact]
    public void ANewProviderTypeNeedsNoConditionInTheRules()
    {
        var future = new ReplicaStorageFacts("object-store-9", StorageOwnership.TenantManaged, "tenant-cloud", Guid.NewGuid());
        Assert.True(QuotaScopeRule.All.Matches(future));
        Assert.True(Rule(QuotaScopeKind.ProviderType, "object-store-9").Matches(future));
        Assert.True(Rule(QuotaScopeKind.QuotaScope, "tenant-cloud").Matches(future));
        Assert.True(Rule(QuotaScopeKind.OwnershipType, "TenantManaged").Matches(future));
        Assert.False(Rule(QuotaScopeKind.OwnershipType, "PlatformManaged").Matches(future));
    }

    [Fact]
    public void QuotaScopeLabelsAreCanonicalizedAndValidated()
    {
        Assert.Equal("""[{"kind":"quotaScope","values":["managed","tenant-cloud"]}]""", Rule(QuotaScopeKind.QuotaScope, " Tenant-Cloud ", "managed", "MANAGED").ToJson());
        foreach (var invalid in new[] { "has space", "-lead", "trail-", "under_score", new string('a', 65), "ünï" })
        {
            Assert.Equal("invalid_value", CodeOf(() => Rule(QuotaScopeKind.QuotaScope, invalid)));
        }
        Assert.Single(Rule(QuotaScopeKind.QuotaScope, new string('a', 64)).Selectors);
    }

    [Fact]
    public void StorageConnectionIdsAreCanonicalizedAndValidated()
    {
        var id = Guid.NewGuid();
        Assert.Equal($$"""[{"kind":"storageConnection","values":["{{id:D}}"]}]""", Rule(QuotaScopeKind.StorageConnection, id.ToString("D").ToUpperInvariant(), id.ToString("D")).ToJson());
        foreach (var invalid in new[] { "not-a-guid", Guid.Empty.ToString("D"), id.ToString("N"), id.ToString("B"), "1" })
        {
            Assert.Equal("invalid_value", CodeOf(() => Rule(QuotaScopeKind.StorageConnection, invalid)));
        }
    }

    [Fact]
    public void AllMatchesEveryReplica()
    {
        Assert.True(QuotaScopeRule.All.IncludesEverything);
        Assert.True(QuotaScopeRule.All.Matches(Replica("central")));
        Assert.True(QuotaScopeRule.All.Matches(Replica("local", BackupStorageExecutionLocation.Agent)));
        Assert.True(QuotaScopeRule.All.Matches(Replica("anything-new")));
        Assert.Equal("""[{"kind":"all"}]""", QuotaScopeRule.All.ToJson());
    }

    [Fact]
    public void ProviderTypeMatchesOnlyTheListedProviders()
    {
        var rule = Rule(QuotaScopeKind.ProviderType, "central", "sftp");
        Assert.True(rule.Matches(Replica("central")));
        Assert.True(rule.Matches(Replica("sftp")));
        Assert.False(rule.Matches(Replica("googledrive")));
        Assert.False(rule.Matches(Replica("local", BackupStorageExecutionLocation.Agent)));
        Assert.False(rule.IncludesEverything);
    }

    [Fact]
    public void OwnershipTypeMatchesTheDerivedOwnership()
    {
        var platform = Rule(QuotaScopeKind.OwnershipType, "PlatformManaged");
        Assert.True(platform.Matches(Replica("central")));
        Assert.False(platform.Matches(Replica("central", BackupStorageExecutionLocation.Agent)));
        Assert.False(platform.Matches(Replica("local", BackupStorageExecutionLocation.Agent)));
        Assert.False(platform.Matches(Replica("sftp")));

        var tenant = Rule(QuotaScopeKind.OwnershipType, "TenantManaged");
        Assert.False(tenant.Matches(Replica("central")));
        Assert.True(tenant.Matches(Replica("local", BackupStorageExecutionLocation.Agent)));
        Assert.True(tenant.Matches(Replica("googledrive")));
    }

    [Fact]
    public void SelectorsOfDifferentKindsAreAUnion()
    {
        var rule = QuotaScopeRule.Create([
            new QuotaScopeSelector(QuotaScopeKind.ProviderType, ["sftp"]),
            new QuotaScopeSelector(QuotaScopeKind.OwnershipType, ["PlatformManaged"])]);
        Assert.True(rule.Matches(Replica("sftp")));
        Assert.True(rule.Matches(Replica("central")));
        Assert.False(rule.Matches(Replica("googledrive")));
        Assert.False(rule.Matches(Replica("local", BackupStorageExecutionLocation.Agent)));
    }

    [Fact]
    public void TwoRulesThatSelectTheSameReplicasShareOneCanonicalForm()
    {
        var messy = QuotaScopeRule.Create([
            new QuotaScopeSelector(QuotaScopeKind.OwnershipType, [" platformmanaged ", "PlatformManaged"]),
            new QuotaScopeSelector(QuotaScopeKind.ProviderType, ["SFTP", "central", "sftp", " "]),
            new QuotaScopeSelector(QuotaScopeKind.ProviderType, ["googledrive"])]);
        var clean = QuotaScopeRule.FromJson("""[{"kind":"providerType","values":["central","googledrive","sftp"]},{"kind":"ownershipType","values":["PlatformManaged"]}]""");

        Assert.Equal(clean, messy);
        Assert.Equal(clean.GetHashCode(), messy.GetHashCode());
        Assert.Equal(clean.ToJson(), messy.ToJson());
        Assert.Equal("""[{"kind":"providerType","values":["central","googledrive","sftp"]},{"kind":"ownershipType","values":["PlatformManaged"]}]""", messy.ToJson());
    }

    [Fact]
    public void AllAbsorbsEveryOtherSelector()
    {
        var rule = QuotaScopeRule.Create([
            new QuotaScopeSelector(QuotaScopeKind.ProviderType, ["central"]),
            new QuotaScopeSelector(QuotaScopeKind.All, [])]);
        Assert.Equal(QuotaScopeRule.All, rule);
        Assert.True(rule.IncludesEverything);
        Assert.Single(rule.Selectors);
    }

    [Theory]
    [InlineData("""[{"kind":"all"}]""")]
    [InlineData("""[{"kind":"providerType","values":["central"]}]""")]
    [InlineData("""[{"kind":"ownershipType","values":["PlatformManaged","TenantManaged"]}]""")]
    [InlineData("""[{"kind":"providerType","values":["central","sftp"]},{"kind":"ownershipType","values":["TenantManaged"]}]""")]
    public void JsonRoundTripsUnchanged(string json)
    {
        var rule = QuotaScopeRule.FromJson(json);
        Assert.Equal(json, rule.ToJson());
        Assert.Equal(rule, QuotaScopeRule.FromJson(rule.ToJson()));
    }

    [Fact]
    public void OwnershipNamesAreStoredWithTheirCanonicalCasing()
    {
        Assert.Equal("""[{"kind":"ownershipType","values":["PlatformManaged"]}]""", Rule(QuotaScopeKind.OwnershipType, "platformmanaged").ToJson());
    }

    [Theory]
    [InlineData("QuotaScope")]
    [InlineData("StorageConnection")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("ProviderType")]
    [InlineData("ALL")]
    [InlineData("")]
    public void UnsupportedOrNumericKindNamesAreRejected(string kind)
    {
        Assert.Equal("unsupported_scope_kind", CodeOf(() => QuotaScopeRule.ParseKind(kind)));
        Assert.Equal("unsupported_scope_kind", CodeOf(() => QuotaScopeRule.FromJson($$"""[{"kind":"{{kind}}","values":["x"]}]""")));
    }

    [Fact]
    public void AMissingKindNameIsRejectedAsUnsupported() =>
        Assert.Equal("unsupported_scope_kind", CodeOf(() => QuotaScopeRule.ParseKind(null)));

    [Fact]
    public void AnUndefinedKindValueIsRejected() =>
        Assert.Equal("unsupported_scope_kind", CodeOf(() => QuotaScopeRule.Create([new QuotaScopeSelector((QuotaScopeKind)99, ["x"])])));

    [Theory]
    [InlineData("""[{"kind":1}]""")]
    [InlineData("""[{"values":["central"]}]""")]
    [InlineData("""[{"kind":"providerType","values":"central"}]""")]
    [InlineData("""[{"kind":"providerType","values":[1]}]""")]
    [InlineData("""{"kind":"all"}""")]
    [InlineData("""["all"]""")]
    [InlineData("not json")]
    public void MalformedJsonIsRejected(string json) =>
        Assert.Equal("invalid_value", CodeOf(() => QuotaScopeRule.FromJson(json)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    public void ARuleNeedsAtLeastOneSelector(string? json) =>
        Assert.Equal("required", CodeOf(() => QuotaScopeRule.FromJson(json)));

    [Fact]
    public void NullOrEmptySelectorListsAreRejected()
    {
        Assert.Equal("required", CodeOf(() => QuotaScopeRule.Create(null)));
        Assert.Equal("required", CodeOf(() => QuotaScopeRule.Create([])));
    }

    [Fact]
    public void AtMostEightSelectorsAreAccepted()
    {
        QuotaScopeSelector[] Selectors(int count) => Enumerable.Range(0, count).Select(_ => new QuotaScopeSelector(QuotaScopeKind.ProviderType, ["central"])).ToArray();

        Assert.Single(QuotaScopeRule.Create(Selectors(QuotaScopeRule.MaximumSelectors)).Selectors);
        Assert.Equal("too_many", CodeOf(() => QuotaScopeRule.Create(Selectors(QuotaScopeRule.MaximumSelectors + 1))));
    }

    [Fact]
    public void AtMostThirtyTwoValuesPerSelectorAreAccepted()
    {
        string[] Values(int count) => Enumerable.Range(0, count).Select(index => $"p{index}").ToArray();

        Assert.Equal(32, Rule(QuotaScopeKind.ProviderType, Values(QuotaScopeRule.MaximumValuesPerSelector)).Selectors[0].Values.Count);
        Assert.Equal("invalid_value", CodeOf(() => Rule(QuotaScopeKind.ProviderType, Values(QuotaScopeRule.MaximumValuesPerSelector + 1))));
    }

    [Fact]
    public void AllCannotCarryValues() =>
        Assert.Equal("unexpected_values", CodeOf(() => Rule(QuotaScopeKind.All, "central")));

    [Theory]
    [InlineData(QuotaScopeKind.ProviderType)]
    [InlineData(QuotaScopeKind.OwnershipType)]
    [InlineData(QuotaScopeKind.QuotaScope)]
    [InlineData(QuotaScopeKind.StorageConnection)]
    public void ValueSelectorsNeedAtLeastOneNonBlankValue(QuotaScopeKind kind)
    {
        Assert.Equal("invalid_value", CodeOf(() => Rule(kind)));
        Assert.Equal("invalid_value", CodeOf(() => Rule(kind, " ", "")));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("-leading")]
    [InlineData(".leading")]
    [InlineData("under/score")]
    [InlineData("ünï")]
    public void ProviderValuesMustBeProviderKeys(string value) =>
        Assert.Equal("invalid_value", CodeOf(() => Rule(QuotaScopeKind.ProviderType, value)));

    [Fact]
    public void ProviderKeysLongerThan64CharactersAreRejected()
    {
        Assert.Single(Rule(QuotaScopeKind.ProviderType, new string('a', 64)).Selectors);
        Assert.Equal("invalid_value", CodeOf(() => Rule(QuotaScopeKind.ProviderType, new string('a', 65))));
    }

    [Theory]
    [InlineData("Platform")]
    [InlineData("1")]
    [InlineData("PlatformManaged,TenantManaged")]
    public void OwnershipValuesMustBeKnownOwnerships(string value) =>
        Assert.Equal("invalid_value", CodeOf(() => Rule(QuotaScopeKind.OwnershipType, value)));
}
