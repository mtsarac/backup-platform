using BackupPlatform.Domain.Backups;
using BackupPlatform.ManagedService;
using BackupPlatform.ManagedService.Quota;

namespace BackupPlatform.UnitTests.ManagedService.Quota;

public sealed class QuotaScopeRuleTests
{
    private static ReplicaStorageFacts Replica(string provider, BackupStorageExecutionLocation location = BackupStorageExecutionLocation.Backend) => new(provider, location);

    private static QuotaScopeRule Rule(QuotaScopeKind kind, params string[] values) => QuotaScopeRule.Create([new QuotaScopeSelector(kind, values)]);

    private static string CodeOf(Action action) => Assert.Throws<InvalidManagedServiceRequestException>(action).Code;

    [Theory]
    [InlineData("central", BackupStorageExecutionLocation.Backend, StorageOwnership.PlatformManaged)]
    [InlineData("central", BackupStorageExecutionLocation.Agent, StorageOwnership.TenantManaged)]
    [InlineData("local", BackupStorageExecutionLocation.Agent, StorageOwnership.TenantManaged)]
    [InlineData("local", BackupStorageExecutionLocation.Backend, StorageOwnership.TenantManaged)]
    [InlineData("googledrive", BackupStorageExecutionLocation.Backend, StorageOwnership.TenantManaged)]
    [InlineData("sftp", BackupStorageExecutionLocation.Backend, StorageOwnership.TenantManaged)]
    [InlineData("Central", BackupStorageExecutionLocation.Backend, StorageOwnership.TenantManaged)]
    public void OnlyTheCentralProviderOnTheBackendIsPlatformManaged(string provider, BackupStorageExecutionLocation location, StorageOwnership expected)
    {
        Assert.Equal(expected, StorageOwnershipClassifier.Classify(provider, location));
        Assert.Equal(expected, Replica(provider, location).Ownership);
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
    [InlineData("quotaScope")]
    [InlineData("storageConnection")]
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
