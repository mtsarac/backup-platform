using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;

namespace BackupPlatform.UnitTests.Storage.Connections;

public sealed class StorageConnectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddMinutes(5);

    private static StorageConnection Create(
        string name = "Archive", string providerType = "fake", StorageOwnership ownership = StorageOwnership.TenantManaged,
        string? quotaScope = null, IReadOnlyDictionary<string, string>? configuration = null) =>
        new(Guid.NewGuid(), TenantId.New(), name, providerType, ownership, quotaScope, configuration, isDefault: false, T0);

    // ---- names -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void NameIsTrimmedAndNormalizedToLowerCaseForUniqueness()
    {
        var connection = Create("  Offsite Archive  ");

        Assert.Equal("Offsite Archive", connection.Name);
        Assert.Equal("offsite archive", connection.NormalizedName);
    }

    [Fact]
    public void NameOfExactlyMaximumLengthIsAcceptedAndOneMoreIsRejectedAfterTrimming()
    {
        Assert.Equal(StorageConnection.MaximumNameLength, Create(new string('a', StorageConnection.MaximumNameLength)).Name.Length);
        Assert.Equal(StorageConnection.MaximumNameLength, Create("  " + new string('a', StorageConnection.MaximumNameLength) + "  ").Name.Length);
        Assert.Throws<ArgumentException>(() => Create(new string('a', StorageConnection.MaximumNameLength + 1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tab\tinside")]
    [InlineData("new\nline")]
    [InlineData("nul\0char")]
    public void EmptyAndControlCharacterNamesAreRejected(string? name)
    {
        Assert.Throws<ArgumentException>(() => StorageConnection.Names(name));
        Assert.Throws<ArgumentException>(() => Create(name!));
    }

    // ---- construction: provider type / quota scope -------------------------------------------------------------------------

    [Theory]
    [InlineData("local", true)]
    [InlineData("google-drive", true)]
    [InlineData("s3", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("Local", false)]
    [InlineData("-lead", false)]
    [InlineData("trail-", false)]
    [InlineData("under_score", false)]
    [InlineData("white space", false)]
    [InlineData("umlaut-ü", false)]
    public void ProviderTypeGrammarIsLowercaseAlphanumericWithInnerHyphens(string value, bool valid)
    {
        Assert.Equal(valid, StorageProviderTypes.IsValid(value));
    }

    [Fact]
    public void ProviderTypeLengthIsBoundedAt32AndNullIsInvalid()
    {
        Assert.True(StorageProviderTypes.IsValid(new string('a', 32)));
        Assert.False(StorageProviderTypes.IsValid(new string('a', 33)));
        Assert.False(StorageProviderTypes.IsValid(null));
    }

    [Theory]
    [InlineData("managed", true)]
    [InlineData("tenant", true)]
    [InlineData("gold-tier-2", true)]
    [InlineData("9", true)]
    [InlineData("", false)]
    [InlineData("Managed", false)]
    [InlineData("-x", false)]
    [InlineData("x-", false)]
    [InlineData("a b", false)]
    [InlineData("a_b", false)]
    public void QuotaScopeGrammarMatchesTheSelectorLabelRule(string value, bool valid)
    {
        Assert.Equal(valid, StorageQuotaScopes.IsValid(value));
    }

    [Fact]
    public void QuotaScopeLengthIsBoundedAt64AndNullIsInvalid()
    {
        Assert.True(StorageQuotaScopes.IsValid(new string('a', 64)));
        Assert.False(StorageQuotaScopes.IsValid(new string('a', 65)));
        Assert.False(StorageQuotaScopes.IsValid(null));
    }

    [Theory]
    [InlineData(StorageOwnership.PlatformManaged, "managed")]
    [InlineData(StorageOwnership.TenantManaged, "tenant")]
    public void QuotaScopeDefaultsFromOwnershipWhenNotGiven(StorageOwnership ownership, string expected)
    {
        Assert.Equal(expected, StorageQuotaScopes.DefaultFor(ownership));
        Assert.Equal(expected, Create(ownership: ownership).QuotaScope);
    }

    [Fact]
    public void ExplicitQuotaScopeOverridesTheOwnershipDefault()
    {
        Assert.Equal("gold", Create(ownership: StorageOwnership.PlatformManaged, quotaScope: "gold").QuotaScope);
    }

    [Fact]
    public void InvalidConstructorArgumentsAreRejected()
    {
        var tenant = TenantId.New();

        Assert.Throws<ArgumentException>(() => new StorageConnection(Guid.Empty, tenant, "x", "fake", StorageOwnership.TenantManaged, null, null, false, T0));
        Assert.Throws<ArgumentNullException>(() => new StorageConnection(Guid.NewGuid(), null!, "x", "fake", StorageOwnership.TenantManaged, null, null, false, T0));
        Assert.Throws<ArgumentException>(() => new StorageConnection(Guid.NewGuid(), tenant, "x", "Bad Type", StorageOwnership.TenantManaged, null, null, false, T0));
        Assert.Throws<ArgumentException>(() => new StorageConnection(Guid.NewGuid(), tenant, "x", "fake", (StorageOwnership)99, null, null, false, T0));
        Assert.Throws<ArgumentException>(() => new StorageConnection(Guid.NewGuid(), tenant, "x", "fake", StorageOwnership.TenantManaged, "Bad Scope", null, false, T0));
    }

    // ---- configuration snapshot ---------------------------------------------------------------------------------------------

    [Fact]
    public void ConfigurationIsASnapshotThatLaterChangesToTheCallersDictionaryCannotReach()
    {
        var source = new Dictionary<string, string> { ["host"] = "a" };
        var connection = Create(configuration: source);

        source["host"] = "changed";
        source["extra"] = "x";

        Assert.Equal(new Dictionary<string, string> { ["host"] = "a" }, connection.Configuration);
    }

    [Fact]
    public void ConfigurationCannotBeModifiedThroughTheExposedDictionary()
    {
        var connection = Create(configuration: new Dictionary<string, string> { ["host"] = "a" });

        var writable = Assert.IsAssignableFrom<IDictionary<string, string>>(connection.Configuration);

        Assert.Throws<NotSupportedException>(() => writable["host"] = "b");
        Assert.Throws<NotSupportedException>(() => writable.Add("other", "b"));
        Assert.Equal("a", connection.Configuration["host"]);
    }

    [Fact]
    public void EmptyConfigurationIsAlsoNotWritable()
    {
        var writable = Assert.IsAssignableFrom<IDictionary<string, string>>(Create().Configuration);

        Assert.Throws<NotSupportedException>(() => writable.Add("host", "a"));
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("1host")]
    [InlineData("ho_st")]
    [InlineData("ho-st")]
    [InlineData("")]
    [InlineData("hôst")]
    public void ConfigurationFieldNamesMustBeShortLowerCaseLeadingIdentifiers(string key)
    {
        Assert.False(StorageConnection.IsFieldName(key));
        Assert.Throws<ArgumentException>(() => Create(configuration: new Dictionary<string, string> { [key] = "v" }));
    }

    [Fact]
    public void FieldNameLengthIsBoundedAt32AndCamelCaseIsAllowed()
    {
        Assert.True(StorageConnection.IsFieldName("h" + new string('A', 31)));
        Assert.False(StorageConnection.IsFieldName("h" + new string('A', 32)));
        Assert.True(StorageConnection.IsFieldName("useTls2"));
        Assert.False(StorageConnection.IsFieldName(null));
    }

    [Fact]
    public void ConfigurationValueLengthIsBoundedAndControlCharactersAreRejected()
    {
        var max = new string('v', StorageConnection.MaximumConfigurationValueLength);

        Assert.Equal(max, Create(configuration: new Dictionary<string, string> { ["host"] = max }).Configuration["host"]);
        Assert.Throws<ArgumentException>(() => Create(configuration: new Dictionary<string, string> { ["host"] = max + "v" }));
        Assert.Throws<ArgumentException>(() => Create(configuration: new Dictionary<string, string> { ["host"] = "a\nb" }));
        Assert.Throws<ArgumentException>(() => Create(configuration: new Dictionary<string, string> { ["host"] = null! }));
    }

    [Fact]
    public void ConfigurationEntryCountIsBounded()
    {
        IReadOnlyDictionary<string, string> Entries(int count) => Enumerable.Range(0, count).ToDictionary(index => $"key{index}", _ => "v");

        Assert.Equal(StorageConnection.MaximumConfigurationEntries, Create(configuration: Entries(StorageConnection.MaximumConfigurationEntries)).Configuration.Count);
        Assert.Throws<ArgumentException>(() => Create(configuration: Entries(StorageConnection.MaximumConfigurationEntries + 1)));
    }

    // ---- Rename / Reconfigure / ChangeQuotaScope / SetEnabled --------------------------------------------------------------

    [Fact]
    public void RenameToTheSameNameReportsNoChangeAndTouchesNothing()
    {
        var connection = Create("Archive");

        Assert.False(connection.Rename("  Archive ", T1));

        Assert.Equal("Archive", connection.Name);
        Assert.Equal(T0, connection.UpdatedAt);
    }

    [Fact]
    public void RenameToADifferentNameUpdatesDisplayNormalizedAndTimestamp()
    {
        var connection = Create("Archive");

        Assert.True(connection.Rename("  Cold STORE ", T1));

        Assert.Equal("Cold STORE", connection.Name);
        Assert.Equal("cold store", connection.NormalizedName);
        Assert.Equal(T1, connection.UpdatedAt);
    }

    [Fact]
    public void RenameThatOnlyChangesCaseIsAChangeButKeepsTheUniquenessKey()
    {
        var connection = Create("archive");

        Assert.True(connection.Rename("ARCHIVE", T1));

        Assert.Equal("ARCHIVE", connection.Name);
        Assert.Equal("archive", connection.NormalizedName);
    }

    [Fact]
    public void RenameToAnInvalidNameThrowsAndKeepsTheOldName()
    {
        var connection = Create("Archive");

        Assert.Throws<ArgumentException>(() => connection.Rename("   ", T1));

        Assert.Equal("Archive", connection.Name);
        Assert.Equal(T0, connection.UpdatedAt);
    }

    [Fact]
    public void ReconfigureWithEqualContentReportsNoChangeRegardlessOfOrder()
    {
        var connection = Create(configuration: new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });

        Assert.False(connection.Reconfigure(new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" }, T1));

        Assert.Equal(T0, connection.UpdatedAt);
    }

    [Fact]
    public void ReconfigureChangedValueAddedOrRemovedKeyAreChanges()
    {
        var connection = Create(configuration: new Dictionary<string, string> { ["a"] = "1" });

        Assert.True(connection.Reconfigure(new Dictionary<string, string> { ["a"] = "2" }, T1));
        Assert.Equal("2", connection.Configuration["a"]);
        Assert.Equal(T1, connection.UpdatedAt);

        Assert.True(connection.Reconfigure(new Dictionary<string, string> { ["a"] = "2", ["b"] = "x" }, T1.AddMinutes(1)));
        Assert.Equal(2, connection.Configuration.Count);

        Assert.True(connection.Reconfigure(new Dictionary<string, string> { ["b"] = "x" }, T1.AddMinutes(2)));
        Assert.Equal(new Dictionary<string, string> { ["b"] = "x" }, connection.Configuration);
    }

    [Fact]
    public void ReconfigureToNothingClearsTheConfigurationOnlyOnce()
    {
        var connection = Create(configuration: new Dictionary<string, string> { ["a"] = "1" });

        Assert.True(connection.Reconfigure(null, T1));
        Assert.Empty(connection.Configuration);
        Assert.False(connection.Reconfigure(new Dictionary<string, string>(), T1.AddMinutes(1)));
        Assert.Equal(T1, connection.UpdatedAt);
    }

    [Fact]
    public void ReconfigureWithAnInvalidEntryThrowsAndLeavesTheConfiguration()
    {
        var connection = Create(configuration: new Dictionary<string, string> { ["a"] = "1" });

        Assert.Throws<ArgumentException>(() => connection.Reconfigure(new Dictionary<string, string> { ["Bad"] = "1" }, T1));

        Assert.Equal(new Dictionary<string, string> { ["a"] = "1" }, connection.Configuration);
        Assert.Equal(T0, connection.UpdatedAt);
    }

    [Fact]
    public void ChangeQuotaScopeReportsChangeOnlyWhenTheLabelDiffers()
    {
        var connection = Create(quotaScope: "tenant");

        Assert.False(connection.ChangeQuotaScope("tenant", T1));
        Assert.Equal(T0, connection.UpdatedAt);

        Assert.True(connection.ChangeQuotaScope("gold", T1));
        Assert.Equal("gold", connection.QuotaScope);
        Assert.Equal(T1, connection.UpdatedAt);
    }

    [Fact]
    public void ChangeQuotaScopeRejectsAnInvalidLabelWithoutChangingAnything()
    {
        var connection = Create(quotaScope: "tenant");

        Assert.Throws<ArgumentException>(() => connection.ChangeQuotaScope("Not Valid", T1));

        Assert.Equal("tenant", connection.QuotaScope);
        Assert.Equal(T0, connection.UpdatedAt);
    }

    [Fact]
    public void SetEnabledToTheCurrentValueChangesNothingAndLeavesStateChangedAtUnset()
    {
        var connection = Create();

        Assert.False(connection.SetEnabled(true, T1));

        Assert.True(connection.Enabled);
        Assert.Null(connection.StateChangedAt);
        Assert.Equal(T0, connection.UpdatedAt);
    }

    [Fact]
    public void SetEnabledStampsStateChangedAtOnEveryRealTransition()
    {
        var connection = Create();

        Assert.True(connection.SetEnabled(false, T1));
        Assert.False(connection.Enabled);
        Assert.Equal(T1, connection.StateChangedAt);
        Assert.Equal(T1, connection.UpdatedAt);

        Assert.False(connection.SetEnabled(false, T1.AddMinutes(9)));
        Assert.Equal(T1, connection.StateChangedAt);

        Assert.True(connection.SetEnabled(true, T1.AddMinutes(10)));
        Assert.Equal(T1.AddMinutes(10), connection.StateChangedAt);
    }

    [Fact]
    public void RenameReconfigureAndQuotaChangesNeverTouchStateChangedAt()
    {
        var connection = Create();

        connection.Rename("Other", T1);
        connection.ChangeQuotaScope("gold", T1);
        connection.Reconfigure(new Dictionary<string, string> { ["a"] = "1" }, T1);

        Assert.Null(connection.StateChangedAt);
    }

    // ---- secrets -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void AttachSecretSortsAndDeduplicatesFieldNamesAndStampsTheTime()
    {
        var connection = Create();

        var previous = connection.AttachSecret("sc-1", ["token", "password", "token"], T1);

        Assert.Null(previous);
        Assert.Equal("sc-1", connection.SecretReference);
        Assert.True(connection.HasSecret);
        Assert.Equal(["password", "token"], connection.SecretFieldNames);
        Assert.Equal(T1, connection.SecretUpdatedAt);
        Assert.Equal(T1, connection.UpdatedAt);
    }

    [Fact]
    public void AttachSecretReturnsTheReferenceItReplaced()
    {
        var connection = Create();
        connection.AttachSecret("sc-1", ["password"], T0);

        var previous = connection.AttachSecret("sc-2", ["token"], T1);

        Assert.Equal("sc-1", previous);
        Assert.Equal("sc-2", connection.SecretReference);
        Assert.Equal(["token"], connection.SecretFieldNames);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/ed")]
    public void AttachSecretRejectsAnInvalidReference(string reference)
    {
        var connection = Create();

        Assert.Throws<ArgumentException>(() => connection.AttachSecret(reference, ["password"], T1));

        Assert.False(connection.HasSecret);
    }

    [Fact]
    public void AttachSecretRejectsBadOrMissingFieldNamesAndKeepsTheCurrentSecret()
    {
        var connection = Create();
        connection.AttachSecret("sc-1", ["password"], T0);

        Assert.Throws<ArgumentException>(() => connection.AttachSecret("sc-2", [], T1));
        Assert.Throws<ArgumentException>(() => connection.AttachSecret("sc-2", ["Bad-Name"], T1));
        Assert.Throws<ArgumentException>(() => connection.AttachSecret("sc-2", ["password", "1x"], T1));
        Assert.Throws<ArgumentNullException>(() => connection.AttachSecret("sc-2", null!, T1));
        Assert.Throws<ArgumentException>(() => connection.AttachSecret("sc-2", Enumerable.Range(0, 17).Select(index => $"f{index}"), T1));

        Assert.Equal("sc-1", connection.SecretReference);
        Assert.Equal(["password"], connection.SecretFieldNames);
        Assert.Equal(T0, connection.SecretUpdatedAt);
    }

    [Fact]
    public void DetachSecretReturnsThePreviousReferenceAndForgetsTheSecret()
    {
        var connection = Create();
        connection.AttachSecret("sc-1", ["password"], T0);

        var previous = connection.DetachSecret(T1);

        Assert.Equal("sc-1", previous);
        Assert.False(connection.HasSecret);
        Assert.Null(connection.SecretReference);
        Assert.Empty(connection.SecretFieldNames);
        Assert.Equal(T1, connection.SecretUpdatedAt);
        Assert.Equal(T1, connection.UpdatedAt);
    }

    [Fact]
    public void DetachSecretWithoutASecretIsANoOp()
    {
        var connection = Create();

        Assert.Null(connection.DetachSecret(T1));

        Assert.Null(connection.SecretUpdatedAt);
        Assert.Equal(T0, connection.UpdatedAt);
    }

    // ---- RecordTest --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(StorageTestStatus.Succeeded, "ok")]
    [InlineData(StorageTestStatus.Failed, "access_denied")]
    [InlineData(StorageTestStatus.NotTestable, "agent_side_storage")]
    [InlineData(StorageTestStatus.Failed, null)]
    public void RecordTestStoresStatusCodeAndTime(StorageTestStatus status, string? code)
    {
        var connection = Create();

        connection.RecordTest(status, code, T1);

        Assert.Equal(status, connection.LastTestStatus);
        Assert.Equal(code, connection.LastTestCode);
        Assert.Equal(T1, connection.LastTestedAt);
    }

    [Fact]
    public void RecordTestReplacesThePreviousResult()
    {
        var connection = Create();
        connection.RecordTest(StorageTestStatus.Succeeded, "ok", T0);

        connection.RecordTest(StorageTestStatus.Failed, null, T1);

        Assert.Equal(StorageTestStatus.Failed, connection.LastTestStatus);
        Assert.Null(connection.LastTestCode);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("free text: the host refused")]
    [InlineData("")]
    public void RecordTestRejectsACodeThatIsNotAStableReferenceAndKeepsThePreviousResult(string code)
    {
        var connection = Create();
        connection.RecordTest(StorageTestStatus.Succeeded, "ok", T0);

        Assert.Throws<ArgumentException>(() => connection.RecordTest(StorageTestStatus.Failed, code, T1));

        Assert.Equal(StorageTestStatus.Succeeded, connection.LastTestStatus);
        Assert.Equal("ok", connection.LastTestCode);
        Assert.Equal(T0, connection.LastTestedAt);
    }

    [Fact]
    public void RecordTestRejectsAnUnknownStatus()
    {
        var connection = Create();

        Assert.Throws<ArgumentException>(() => connection.RecordTest((StorageTestStatus)42, "ok", T1));

        Assert.Null(connection.LastTestStatus);
    }

    // ---- defaults ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void DefaultConnectionsAreLocalTenantManagedAndCentralPlatformManaged()
    {
        Assert.Equal(("local", "Local", StorageOwnership.TenantManaged, "tenant"),
            (StorageConnectionDefaults.Local.ProviderType, StorageConnectionDefaults.Local.Name, StorageConnectionDefaults.Local.Ownership, StorageConnectionDefaults.Local.QuotaScope));
        Assert.Equal(("central", "Managed Storage", StorageOwnership.PlatformManaged, "managed"),
            (StorageConnectionDefaults.Central.ProviderType, StorageConnectionDefaults.Central.Name, StorageConnectionDefaults.Central.Ownership, StorageConnectionDefaults.Central.QuotaScope));
        Assert.Equal([StorageConnectionDefaults.Local, StorageConnectionDefaults.Central], StorageConnectionDefaults.All);
    }

    [Fact]
    public void DefaultsAreFoundByProviderTypeAndOtherProvidersHaveNone()
    {
        Assert.Same(StorageConnectionDefaults.Central, StorageConnectionDefaults.For("central"));
        Assert.Same(StorageConnectionDefaults.Local, StorageConnectionDefaults.For("local"));
        Assert.Null(StorageConnectionDefaults.For("sftp"));
    }

    [Fact]
    public void AllNoOpChangesPreserveBothTimestampsAfterAStateTransition()
    {
        var connection = Create();
        var disabledAt = T0.AddMinutes(1);
        connection.SetEnabled(false, disabledAt);

        Assert.False(connection.Rename(" Archive ", T1));
        Assert.False(connection.Reconfigure(null, T1));
        Assert.False(connection.ChangeQuotaScope("tenant", T1));
        Assert.False(connection.SetEnabled(false, T1));

        Assert.Equal(disabledAt, connection.UpdatedAt);
        Assert.Equal(disabledAt, connection.StateChangedAt);
        Assert.Equal("Archive", connection.Name);
        Assert.Equal("tenant", connection.QuotaScope);
        Assert.Empty(connection.Configuration);
        Assert.False(connection.Enabled);
    }

    [Fact]
    public void RecordTestCodeLengthIsBoundedAndOverlongCodePreservesThePreviousResult()
    {
        var connection = Create();
        var code = new string('a', 128);
        connection.RecordTest(StorageTestStatus.Failed, code, T0);

        Assert.Throws<ArgumentException>(() => connection.RecordTest(StorageTestStatus.Succeeded, code + "a", T1));

        Assert.Equal(StorageTestStatus.Failed, connection.LastTestStatus);
        Assert.Equal(code, connection.LastTestCode);
        Assert.Equal(T0, connection.LastTestedAt);
    }
}
