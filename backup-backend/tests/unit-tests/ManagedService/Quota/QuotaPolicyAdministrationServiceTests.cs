using BackupPlatform.Domain.Identity;
using BackupPlatform.ManagedService;
using BackupPlatform.ManagedService.Administration;
using BackupPlatform.ManagedService.Quota;

namespace BackupPlatform.UnitTests.ManagedService.Quota;

public sealed class QuotaPolicyAdministrationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly UserId Actor = UserId.New();

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CapturingStore : IQuotaPolicyStore
    {
        public QuotaPolicy? Created { get; private set; }
        public (QuotaPolicyId Id, QuotaPolicyValues Values, UserId Actor, DateTimeOffset Now)? Updated { get; private set; }
        public int Calls { get; private set; }

        public Task<IReadOnlyList<QuotaPolicyView>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<QuotaPolicyView>>([]);

        public Task<ManagedServiceChange<QuotaPolicyChange>> CreateAsync(QuotaPolicy policy, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Calls++;
            Created = policy;
            return Task.FromResult(new ManagedServiceChange<QuotaPolicyChange>(ManagedServiceOutcome.Changed));
        }

        public Task<ManagedServiceChange<QuotaPolicyChange>> UpdateAsync(
            QuotaPolicyId id, QuotaPolicyValues values, UserId actor, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Calls++;
            Updated = (id, values, actor, now);
            return Task.FromResult(new ManagedServiceChange<QuotaPolicyChange>(ManagedServiceOutcome.Changed));
        }
    }

    private static readonly IReadOnlyList<QuotaScopeSelectorRequest> AllScope = [new("all", null)];

    private static (QuotaPolicyAdministrationService Service, CapturingStore Store) Setup()
    {
        var store = new CapturingStore();
        return (new QuotaPolicyAdministrationService(store, new FixedTime()), store);
    }

    private static Task<ManagedServiceChange<QuotaPolicyChange>> Create(
        QuotaPolicyAdministrationService service, string? key = "gold", string? name = "Gold", string? accounting = "LogicalArtifactBytes",
        IReadOnlyList<QuotaScopeSelectorRequest>? scopes = null, string? enforcement = "BlockNewBackups", int? warning = 80, int? critical = 90, bool? enabled = true) =>
        service.CreateAsync(key, name, accounting, scopes ?? AllScope, enforcement, warning, critical, enabled, Actor, CancellationToken.None);

    private static Task<ManagedServiceChange<QuotaPolicyChange>> Update(
        QuotaPolicyAdministrationService service, string? name = "Gold", string? accounting = "LogicalArtifactBytes",
        IReadOnlyList<QuotaScopeSelectorRequest>? scopes = null, string? enforcement = "BlockNewBackups", int? warning = 80, int? critical = 90, bool? enabled = true) =>
        service.UpdateAsync(QuotaPolicyId.New(), name, accounting, scopes ?? AllScope, enforcement, warning, critical, enabled, Actor, CancellationToken.None);

    private static InvalidManagedServiceRequestException Invalid(Func<Task> action) => Assert.Throws<InvalidManagedServiceRequestException>(() => action().GetAwaiter().GetResult());

    private static (string Field, string Code) Failure(Func<Task> action)
    {
        var error = Invalid(action);
        return (error.Field, error.Code);
    }

    [Fact]
    public async Task CreateBuildsAValidatedPolicyAtTheServiceClock()
    {
        var (service, store) = Setup();
        await Create(service, key: " gold ", name: "  Gold  Plus", accounting: " physicalreplicabytes ", enforcement: "WARNONLY", warning: 60, critical: 95, enabled: false,
            scopes: [new("providerType", ["SFTP", "central"]), new("ownershipType", ["platformmanaged"])]);

        var policy = store.Created!;
        Assert.Equal(("gold", "Gold Plus", QuotaAccountingMode.PhysicalReplicaBytes, QuotaEnforcementMode.WarnOnly), (policy.Key, policy.Name, policy.AccountingMode, policy.EnforcementMode));
        Assert.Equal((60, 95, false, 1, Now), (policy.WarningThresholdPercent, policy.CriticalThresholdPercent, policy.Enabled, policy.Version, policy.CreatedAt));
        Assert.Equal("""[{"kind":"providerType","values":["central","sftp"]},{"kind":"ownershipType","values":["PlatformManaged"]}]""", policy.IncludedStorageScopes.ToJson());
        Assert.False(policy.IsDefault);
    }

    [Fact]
    public async Task CreateDefaultsToEnabledWhenNotSpecified()
    {
        var (service, store) = Setup();
        await Create(service, enabled: null);
        Assert.True(store.Created!.Enabled);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("0")]
    [InlineData("Logical")]
    [InlineData("LogicalArtifactBytes,PhysicalReplicaBytes")]
    public void NumericOrUnknownAccountingModesAreRejected(string value)
    {
        var (service, store) = Setup();
        var error = Invalid(() => Create(service, accounting: value));
        Assert.Equal(("accountingMode", "invalid_value"), (error.Field, error.Code));
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("3")]
    [InlineData("Block")]
    public void NumericOrUnknownEnforcementModesAreRejected(string value)
    {
        var (service, store) = Setup();
        var error = Invalid(() => Create(service, enforcement: value));
        Assert.Equal(("enforcementMode", "invalid_value"), (error.Field, error.Code));
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ModesAreRequired(string? value)
    {
        var (service, _) = Setup();
        Assert.Equal(("accountingMode", "required"), Failure(() => Create(service, accounting: value)));
        Assert.Equal(("enforcementMode", "required"), Failure(() => Create(service, enforcement: value)));
    }

    [Theory]
    [InlineData("quotaScope")]
    [InlineData("storageConnection")]
    [InlineData("1")]
    [InlineData("unknown")]
    [InlineData(null)]
    public void UnsupportedScopeKindsAreRejectedBeforeAnythingIsStored(string? kind)
    {
        var (service, store) = Setup();
        var error = Invalid(() => Create(service, scopes: [new(kind, ["x"])]));
        Assert.Equal(("includedStorageScopes", "unsupported_scope_kind"), (error.Field, error.Code));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public void AnUnsupportedKindAmongSupportedOnesRejectsTheWholeRequest()
    {
        var (service, store) = Setup();
        var error = Invalid(() => Create(service, scopes: [new("providerType", ["central"]), new("quotaScope", ["gold"])]));
        Assert.Equal("unsupported_scope_kind", error.Code);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public void ANullSelectorEntryIsAnUnsupportedKind()
    {
        var (service, _) = Setup();
        Assert.Equal("unsupported_scope_kind", Invalid(() => Create(service, scopes: [null!])).Code);
    }

    [Fact]
    public void ScopesMustNotBeEmptyOrTooMany()
    {
        var (service, _) = Setup();
        Assert.Equal("required", Invalid(() => service.CreateAsync("gold", "Gold", "LogicalArtifactBytes", null, "BlockNewBackups", 80, 90, true, Actor, CancellationToken.None)).Code);
        Assert.Equal("required", Invalid(() => Create(service, scopes: [])).Code);
        var nine = Enumerable.Range(0, 9).Select(_ => new QuotaScopeSelectorRequest("providerType", ["central"])).ToList();
        Assert.Equal("too_many", Invalid(() => Create(service, scopes: nine)).Code);
    }

    [Fact]
    public void AllWithValuesIsRejected()
    {
        var (service, _) = Setup();
        Assert.Equal("unexpected_values", Invalid(() => Create(service, scopes: [new("all", ["central"])])).Code);
    }

    [Theory]
    [InlineData(null, 90, "warningThresholdPercent")]
    [InlineData(80, null, "criticalThresholdPercent")]
    public void ThresholdsAreRequired(int? warning, int? critical, string field)
    {
        var (service, store) = Setup();
        var error = Invalid(() => Create(service, warning: warning, critical: critical));
        Assert.Equal((field, "required"), (error.Field, error.Code));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public void ThresholdRulesAreEnforcedOnCreate()
    {
        var (service, _) = Setup();
        Assert.Equal("out_of_range", Invalid(() => Create(service, warning: 90, critical: 90)).Code);
    }

    [Fact]
    public void ABlankOrInvalidKeyIsRejectedOnCreate()
    {
        var (service, _) = Setup();
        Assert.Equal(("key", "required"), Failure(() => Create(service, key: null)));
        Assert.Equal("invalid_value", Invalid(() => Create(service, key: "Not Lowercase")).Code);
        Assert.Equal("required", Invalid(() => Create(service, name: null)).Code);
    }

    [Fact]
    public async Task UpdatePassesCanonicalValuesWithTheActorAndServiceClockToTheStore()
    {
        var (service, store) = Setup();
        var id = QuotaPolicyId.New();
        await service.UpdateAsync(id, "  Gold   2 ", "physicalReplicaBytes", [new("ownershipType", ["tenantmanaged"])], "warnonly", 70, 85, false, Actor, CancellationToken.None);

        var (storedId, values, actor, now) = store.Updated!.Value;
        Assert.Equal((id, Actor, Now), (storedId, actor, now));
        Assert.Equal(new QuotaPolicyValues("Gold 2", QuotaAccountingMode.PhysicalReplicaBytes,
            QuotaScopeRule.FromJson("""[{"kind":"ownershipType","values":["TenantManaged"]}]"""), QuotaEnforcementMode.WarnOnly, 70, 85, false), values);
    }

    [Fact]
    public void UpdateRequiresEnabledAndBothThresholds()
    {
        var (service, store) = Setup();
        Assert.Equal(("enabled", "required"), Failure(() => Update(service, enabled: null)));
        Assert.Equal("warningThresholdPercent", Invalid(() => Update(service, warning: null)).Field);
        Assert.Equal("criticalThresholdPercent", Invalid(() => Update(service, critical: null)).Field);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("nope")]
    public void UpdateRejectsNumericAndUnknownModes(string value)
    {
        var (service, store) = Setup();
        Assert.Equal("accountingMode", Invalid(() => Update(service, accounting: value)).Field);
        Assert.Equal("enforcementMode", Invalid(() => Update(service, enforcement: value)).Field);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("quotaScope")]
    [InlineData("storageConnection")]
    public void UpdateRejectsUnsupportedScopeKinds(string kind)
    {
        var (service, store) = Setup();
        Assert.Equal("unsupported_scope_kind", Invalid(() => Update(service, scopes: [new(kind, ["x"])])).Code);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public void UpdateValidatesTheNameAndThresholdOrder()
    {
        var (service, store) = Setup();
        Assert.Equal(("name", "required"), Failure(() => Update(service, name: " ")));
        Assert.Equal("criticalThresholdPercent", Invalid(() => Update(service, warning: 95, critical: 90)).Field);
        Assert.Equal(0, store.Calls);
    }
}
