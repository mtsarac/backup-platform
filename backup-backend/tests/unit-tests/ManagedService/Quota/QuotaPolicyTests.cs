using System.Reflection;
using BackupPlatform.ManagedService;
using BackupPlatform.ManagedService.Quota;

namespace BackupPlatform.UnitTests.ManagedService.Quota;

public sealed class QuotaPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddHours(3);

    private static QuotaPolicy Create(
        string key = "gold", string name = "Gold", QuotaAccountingMode accounting = QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule? scope = null,
        QuotaEnforcementMode enforcement = QuotaEnforcementMode.BlockNewBackups, int warning = 80, int critical = 90, bool enabled = true) =>
        new(QuotaPolicyId.New(), key, name, accounting, scope ?? QuotaScopeRule.All, enforcement, warning, critical, enabled, Now);

    private static QuotaPolicy DefaultPolicy()
    {
        var policy = new QuotaPolicy(QuotaPolicy.DefaultId, QuotaPolicy.DefaultKey, "Default", QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule.All, QuotaEnforcementMode.BlockNewBackups, 80, 90, true, Now);
        typeof(QuotaPolicy).GetProperty(nameof(QuotaPolicy.IsDefault), BindingFlags.Instance | BindingFlags.Public)!.SetValue(policy, true);
        return policy;
    }

    private static bool Update(
        QuotaPolicy policy, string name = "Gold", QuotaAccountingMode accounting = QuotaAccountingMode.LogicalArtifactBytes, QuotaScopeRule? scope = null,
        QuotaEnforcementMode enforcement = QuotaEnforcementMode.BlockNewBackups, int warning = 80, int critical = 90, bool enabled = true) =>
        policy.Update(name, accounting, scope ?? QuotaScopeRule.All, enforcement, warning, critical, enabled, Later);

    private static InvalidManagedServiceRequestException Invalid(Action action) => Assert.Throws<InvalidManagedServiceRequestException>(action);

    [Fact]
    public void ANewPolicyStartsAtVersionOneWithUtcTimestampsAndIsNotTheDefault()
    {
        var policy = new QuotaPolicy(QuotaPolicyId.New(), "gold", "  Gold   Plus ", QuotaAccountingMode.PhysicalReplicaBytes, QuotaScopeRule.All,
            QuotaEnforcementMode.WarnOnly, 60, 95, false, new DateTimeOffset(2026, 10, 8, 14, 0, 0, TimeSpan.FromHours(2)));

        Assert.Equal(1, policy.Version);
        Assert.Equal("Gold Plus", policy.Name);
        Assert.Equal("gold plus", policy.NormalizedName);
        Assert.Equal((QuotaAccountingMode.PhysicalReplicaBytes, QuotaEnforcementMode.WarnOnly, 60, 95, false), (policy.AccountingMode, policy.EnforcementMode, policy.WarningThresholdPercent, policy.CriticalThresholdPercent, policy.Enabled));
        Assert.False(policy.IsDefault);
        Assert.Equal(Now, policy.CreatedAt);
        Assert.Equal(TimeSpan.Zero, policy.CreatedAt.Offset);
        Assert.Equal(policy.CreatedAt, policy.UpdatedAt);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(80, 90)]
    [InlineData(98, 99)]
    [InlineData(99, 100)]
    [InlineData(50, 100)]
    public void ValidThresholdPairsAreAccepted(int warning, int critical)
    {
        var policy = Create(warning: warning, critical: critical);
        Assert.Equal((warning, critical), (policy.WarningThresholdPercent, policy.CriticalThresholdPercent));
    }

    [Theory]
    [InlineData(0, 50, "warningThresholdPercent")]
    [InlineData(-1, 50, "warningThresholdPercent")]
    [InlineData(100, 100, "warningThresholdPercent")]
    [InlineData(101, 102, "warningThresholdPercent")]
    [InlineData(50, 50, "criticalThresholdPercent")]
    [InlineData(60, 50, "criticalThresholdPercent")]
    [InlineData(50, 101, "criticalThresholdPercent")]
    [InlineData(1, 1, "criticalThresholdPercent")]
    [InlineData(1, 0, "criticalThresholdPercent")]
    [InlineData(99, 99, "criticalThresholdPercent")]
    public void ThresholdsMustSatisfyOneLessThanWarningLessThanCriticalAtMostOneHundred(int warning, int critical, string field)
    {
        var error = Invalid(() => Create(warning: warning, critical: critical));
        Assert.Equal((field, "out_of_range"), (error.Field, error.Code));
        Assert.Equal(field, Invalid(() => QuotaPolicy.Thresholds(warning, critical)).Field);
    }

    [Theory]
    [InlineData("gold")]
    [InlineData("g")]
    [InlineData("0-premium")]
    [InlineData("tier.1_a-b")]
    public void KeysAreLowercaseReferences(string key) => Assert.Equal(key, Create(key: key).Key);

    [Fact]
    public void AKeyIsTrimmed() => Assert.Equal("gold", Create(key: "  gold ").Key);

    [Theory]
    [InlineData("Gold")]
    [InlineData("has space")]
    [InlineData("-gold")]
    [InlineData(".gold")]
    [InlineData("_gold")]
    [InlineData("gold/plus")]
    public void MalformedKeysAreRejected(string key)
    {
        var error = Invalid(() => Create(key: key));
        Assert.Equal(("key", "invalid_value"), (error.Field, error.Code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AKeyIsRequired(string? key)
    {
        var error = Invalid(() => Create(key: key!));
        Assert.Equal(("key", "required"), (error.Field, error.Code));
    }

    [Fact]
    public void KeysAreAtMostSixtyFourCharacters()
    {
        Assert.Equal(64, Create(key: new string('a', 64)).Key.Length);
        Assert.Equal("invalid_value", Invalid(() => Create(key: new string('a', 65))).Code);
    }

    [Theory]
    [InlineData("", "required")]
    [InlineData("   ", "required")]
    [InlineData("bad\u0007name", "invalid_characters")]
    public void NamesFollowThePlanNameRules(string name, string code)
    {
        var error = Invalid(() => Create(name: name));
        Assert.Equal(("name", code), (error.Field, error.Code));
    }

    [Fact]
    public void NamesAreAtMostOneHundredCharactersAfterTrimming()
    {
        Assert.Equal(100, Create(name: " " + new string('n', 100) + " ").Name.Length);
        Assert.Equal("too_long", Invalid(() => Create(name: new string('n', 101))).Code);
    }

    [Fact]
    public void UndefinedEnumValuesAreRejected()
    {
        Assert.Equal("accountingMode", Invalid(() => Create(accounting: (QuotaAccountingMode)0)).Field);
        Assert.Equal("accountingMode", Invalid(() => Create(accounting: (QuotaAccountingMode)3)).Field);
        Assert.Equal("enforcementMode", Invalid(() => Create(enforcement: (QuotaEnforcementMode)0)).Field);
        Assert.Equal("enforcementMode", Invalid(() => Create(enforcement: (QuotaEnforcementMode)4)).Field);
    }

    [Fact]
    public void ANullScopeIsRejected()
    {
        var error = Invalid(() => new QuotaPolicy(QuotaPolicyId.New(), "gold", "Gold", QuotaAccountingMode.LogicalArtifactBytes, null!, QuotaEnforcementMode.BlockNewBackups, 80, 90, true, Now));
        Assert.Equal(("includedStorageScopes", "required"), (error.Field, error.Code));
    }

    [Fact]
    public void UpdateWithEveryValueCurrentReturnsFalseAndChangesNothing()
    {
        var policy = Create(name: "Gold", scope: QuotaScopeRule.FromJson("""[{"kind":"providerType","values":["central","sftp"]}]"""));

        var changed = policy.Update(" Gold ", QuotaAccountingMode.LogicalArtifactBytes,
            QuotaScopeRule.FromJson("""[{"kind":"providerType","values":["sftp","central"]}]"""), QuotaEnforcementMode.BlockNewBackups, 80, 90, true, Later);

        Assert.False(changed);
        Assert.Equal(1, policy.Version);
        Assert.Equal(Now, policy.UpdatedAt);
    }

    public static TheoryData<string> Changes => ["name", "accounting", "scope", "enforcement", "warning", "critical", "disable"];

    [Theory]
    [MemberData(nameof(Changes))]
    public void AnyChangedValueBumpsTheVersionOnceAndAppliesIt(string change)
    {
        var policy = Create();
        var changed = change switch
        {
            "name" => Update(policy, name: "Gold 2"),
            "accounting" => Update(policy, accounting: QuotaAccountingMode.PhysicalReplicaBytes),
            "scope" => Update(policy, scope: QuotaScopeRule.FromJson("""[{"kind":"ownershipType","values":["PlatformManaged"]}]""")),
            "enforcement" => Update(policy, enforcement: QuotaEnforcementMode.WarnOnly),
            "warning" => Update(policy, warning: 70),
            "critical" => Update(policy, critical: 95),
            _ => Update(policy, enabled: false)
        };

        Assert.True(changed);
        Assert.Equal(2, policy.Version);
        Assert.Equal(Later, policy.UpdatedAt);
        Assert.Equal(Now, policy.CreatedAt);
        Assert.Equal("gold", policy.Key);
        Assert.Equal(change != "name" ? "Gold" : "Gold 2", policy.Name);
        Assert.Equal(change != "accounting" ? QuotaAccountingMode.LogicalArtifactBytes : QuotaAccountingMode.PhysicalReplicaBytes, policy.AccountingMode);
        Assert.Equal(change != "enforcement" ? QuotaEnforcementMode.BlockNewBackups : QuotaEnforcementMode.WarnOnly, policy.EnforcementMode);
        Assert.Equal(change != "scope", policy.IncludedStorageScopes.IncludesEverything);
        Assert.Equal(change == "warning" ? 70 : 80, policy.WarningThresholdPercent);
        Assert.Equal(change == "critical" ? 95 : 90, policy.CriticalThresholdPercent);
        Assert.Equal(change != "disable", policy.Enabled);
    }

    [Fact]
    public void EachRealChangeBumpsTheVersionAgainAndARepeatDoesNot()
    {
        var policy = Create();
        Assert.True(Update(policy, warning: 70));
        Assert.False(Update(policy, warning: 70));
        Assert.True(Update(policy, warning: 75));
        Assert.Equal(3, policy.Version);
    }

    [Fact]
    public void ARejectedUpdateLeavesThePolicyUntouched()
    {
        var policy = Create();
        Assert.Equal("criticalThresholdPercent", Invalid(() => Update(policy, name: "Other", warning: 90, critical: 90)).Field);
        Assert.Equal("accountingMode", Invalid(() => Update(policy, name: "Other", accounting: (QuotaAccountingMode)9)).Field);
        Assert.Equal("name", Invalid(() => Update(policy, name: " ")).Field);
        Assert.Equal((1, "Gold", 80, 90), (policy.Version, policy.Name, policy.WarningThresholdPercent, policy.CriticalThresholdPercent));
        Assert.Equal(Now, policy.UpdatedAt);
    }

    [Fact]
    public void TheDefaultPolicyCannotBeDisabled()
    {
        var policy = DefaultPolicy();
        Assert.True(policy.IsDefault);

        var error = Invalid(() => Update(policy, name: "Default", enabled: false));
        Assert.Equal(("enabled", "default_policy_required"), (error.Field, error.Code));
        Assert.True(policy.Enabled);
        Assert.Equal(1, policy.Version);
    }

    [Fact]
    public void TheDefaultPolicyCanStillBeTunedWhileEnabled()
    {
        var policy = DefaultPolicy();
        Assert.True(Update(policy, name: "Default", warning: 60, critical: 85));
        Assert.Equal((2, 60, 85, true), (policy.Version, policy.WarningThresholdPercent, policy.CriticalThresholdPercent, policy.Enabled));
    }

    [Fact]
    public void AnOrdinaryPolicyCanBeDisabledAndEnabledAgain()
    {
        var policy = Create();
        Assert.True(Update(policy, enabled: false));
        Assert.False(policy.Enabled);
        Assert.True(Update(policy, enabled: true));
        Assert.Equal((3, true), (policy.Version, policy.Enabled));
    }

    [Fact]
    public void TheSeededDefaultIdAndKeyMatchTheMigration()
    {
        Assert.Equal(Guid.Parse("14e8fce9-11ed-47a9-aa04-b6ead7a8cd3d"), QuotaPolicy.DefaultId.Value);
        Assert.Equal("default", QuotaPolicy.DefaultKey);
        Assert.Equal((80, 90), (QuotaPolicy.DefaultWarningThresholdPercent, QuotaPolicy.DefaultCriticalThresholdPercent));
    }
}

public sealed class QuotaOptionsTests
{
    [Fact]
    public void TheDefaultsAreValid() => new QuotaOptions().ThrowIfInvalid();

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(7 * 24 * 60)]
    public void ReservationTimeToLiveAcceptsOneMinuteToSevenDays(int minutes) =>
        new QuotaOptions { ReservationTimeToLive = TimeSpan.FromMinutes(minutes) }.ThrowIfInvalid();

    [Theory]
    [InlineData(0L)]
    [InlineData(59_999L)]
    [InlineData(-300_000L)]
    [InlineData(7 * 24 * 3_600_000L + 1)]
    public void ReservationTimeToLiveOutsideOneMinuteToSevenDaysIsRejected(long milliseconds)
    {
        var options = new QuotaOptions { ReservationTimeToLive = TimeSpan.FromMilliseconds(milliseconds) };
        Assert.Contains("ReservationTimeToLive", Assert.Throws<InvalidOperationException>(options.ThrowIfInvalid).Message);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(999L)]
    [InlineData(-1L)]
    [InlineData(24 * 3_600_000L + 1)]
    public void MaintenanceIntervalOutsideOneSecondToOneDayIsRejected(long milliseconds)
    {
        var options = new QuotaOptions { MaintenanceInterval = TimeSpan.FromMilliseconds(milliseconds) };
        Assert.Contains("MaintenanceInterval", Assert.Throws<InvalidOperationException>(options.ThrowIfInvalid).Message);
    }

    [Fact]
    public void MaintenanceIntervalAtTheBoundsIsAccepted()
    {
        new QuotaOptions { MaintenanceInterval = TimeSpan.FromSeconds(1) }.ThrowIfInvalid();
        new QuotaOptions { MaintenanceInterval = TimeSpan.FromDays(1) }.ThrowIfInvalid();
    }

    [Fact]
    public void ReconcileAfterZeroIsAllowedButNegativeOrOverThirtyDaysIsNot()
    {
        new QuotaOptions { ReconcileAfter = TimeSpan.Zero }.ThrowIfInvalid();
        new QuotaOptions { ReconcileAfter = TimeSpan.FromDays(30) }.ThrowIfInvalid();
        Assert.Throws<InvalidOperationException>(new QuotaOptions { ReconcileAfter = TimeSpan.FromTicks(-1) }.ThrowIfInvalid);
        Assert.Throws<InvalidOperationException>(new QuotaOptions { ReconcileAfter = TimeSpan.FromDays(30) + TimeSpan.FromTicks(1) }.ThrowIfInvalid);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(1000, true)]
    [InlineData(0, false)]
    [InlineData(1001, false)]
    [InlineData(-1, false)]
    public void MaintenanceBatchSizeIsBetweenOneAndOneThousand(int size, bool valid)
    {
        var options = new QuotaOptions { MaintenanceBatchSize = size };
        if (valid)
        {
            options.ThrowIfInvalid();
        }
        else
        {
            Assert.Contains("MaintenanceBatchSize", Assert.Throws<InvalidOperationException>(options.ThrowIfInvalid).Message);
        }
    }
}
