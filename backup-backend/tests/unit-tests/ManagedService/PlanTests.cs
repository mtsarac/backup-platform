using BackupPlatform.ManagedService;
using BackupPlatform.ManagedService.Plans;

namespace BackupPlatform.UnitTests.ManagedService;

public sealed class PlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static Plan NewPlan(string name = "5 GB", long? limit = 5 * StorageLimits.BytesPerGigabyte) =>
        new(PlanId.New(), name, limit, null, true, Now);

    [Fact]
    public void ACommercialGigabyteIsDecimal()
    {
        Assert.Equal(1_000_000_000L, StorageLimits.BytesPerGigabyte);
        Assert.Equal(5_000_000_000L, 5 * StorageLimits.BytesPerGigabyte);
        Assert.NotEqual(1L << 30, StorageLimits.BytesPerGigabyte);
        Assert.Equal(5_000_000_000L, NewPlan().StorageLimitBytes);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(1_000_000_000L)]
    [InlineData(StorageLimits.MaximumBytes)]
    public void ExplicitLimitsInsideTheRangeAreAccepted(long bytes) => Assert.Equal(bytes, NewPlan(limit: bytes).StorageLimitBytes);

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    [InlineData(StorageLimits.MaximumBytes + 1)]
    [InlineData(long.MaxValue)]
    public void LimitsOutsideTheRangeAreRejectedWithoutOverflow(long bytes)
    {
        var failure = Assert.Throws<InvalidManagedServiceRequestException>(() => NewPlan(limit: bytes));
        Assert.Equal(("storageLimit", "out_of_range"), (failure.Field, failure.Code));
    }

    [Fact]
    public void ANullLimitIsAnUnlimitedPlan()
    {
        var plan = NewPlan("Custom enterprise", null);
        Assert.Null(plan.StorageLimitBytes);
        Assert.True(plan.IsUnlimited);
    }

    [Fact]
    public void NamesAreTrimmedCollapsedAndNormalizedCaseInsensitively()
    {
        var plan = NewPlan("  Big   Plan\t50\nGB ");
        Assert.Equal("Big Plan 50 GB", plan.Name);
        Assert.Equal("big plan 50 gb", plan.NormalizedName);
        Assert.Equal(plan.NormalizedName, NewPlan("BIG PLAN 50 gb").NormalizedName);
    }

    [Theory]
    [InlineData(null, "required")]
    [InlineData("", "required")]
    [InlineData("   \t ", "required")]
    [InlineData("bad\u0000name", "invalid_characters")]
    [InlineData("tab\u0007bell", "invalid_characters")]
    public void InvalidNamesNameTheFieldAndCode(string? name, string code)
    {
        var failure = Assert.Throws<InvalidManagedServiceRequestException>(() => NewPlan(name!));
        Assert.Equal(("name", code), (failure.Field, failure.Code));
    }

    [Fact]
    public void NameLengthIsBounded()
    {
        Assert.Equal(ManagedServiceValidation.MaximumPlanNameLength, NewPlan(new string('a', ManagedServiceValidation.MaximumPlanNameLength)).Name.Length);
        var failure = Assert.Throws<InvalidManagedServiceRequestException>(() => NewPlan(new string('a', ManagedServiceValidation.MaximumPlanNameLength + 1)));
        Assert.Equal("too_long", failure.Code);
    }

    [Theory]
    [InlineData("standard-quota.v1")]
    [InlineData("a")]
    public void QuotaPolicyReferencesAreOpaqueLowercaseKeys(string reference) =>
        Assert.Equal(reference, new Plan(PlanId.New(), "Plan", 1, reference, true, Now).QuotaPolicyReference);

    [Theory]
    [InlineData("Upper")]
    [InlineData("has space")]
    [InlineData("-leading")]
    [InlineData("{\"json\":true}")]
    public void QuotaPolicyReferencesRejectAnythingThatIsNotAKey(string reference)
    {
        var failure = Assert.Throws<InvalidManagedServiceRequestException>(() => new Plan(PlanId.New(), "Plan", 1, reference, true, Now));
        Assert.Equal("quotaPolicyReference", failure.Field);
    }

    [Fact]
    public void ABlankReferenceMeansNone() => Assert.Null(new Plan(PlanId.New(), "Plan", 1, "  ", true, Now).QuotaPolicyReference);

    [Fact]
    public void NewPlansAreNeverSystemPlansAndCarryTheCurrentPolicyVersion()
    {
        var plan = NewPlan();
        Assert.False(plan.IsSystem);
        Assert.Null(plan.SystemKey);
        Assert.Equal(Plan.CurrentPolicyConfigurationVersion, plan.PolicyConfigurationVersion);
    }

    [Fact]
    public void UpdateReportsChangeOnlyWhenSomethingDiffersAndStampsUpdatedAt()
    {
        var plan = NewPlan();
        var later = Now.AddHours(1);
        Assert.False(plan.Update("5  GB ", 5_000_000_000L, null, later));
        Assert.Equal(Now, plan.UpdatedAt);
        Assert.True(plan.Update("Five", 6_000_000_000L, "ref-1", later));
        Assert.Equal(("Five", 6_000_000_000L, "ref-1", later), (plan.Name, plan.StorageLimitBytes, plan.QuotaPolicyReference, plan.UpdatedAt));
        Assert.Equal(Now, plan.CreatedAt);
    }

    [Fact]
    public void DisablingAndEnablingIsIdempotent()
    {
        var plan = NewPlan();
        Assert.False(plan.SetEnabled(true, Now.AddMinutes(1)));
        Assert.True(plan.SetEnabled(false, Now.AddMinutes(2)));
        Assert.False(plan.Enabled);
        Assert.False(plan.SetEnabled(false, Now.AddMinutes(3)));
        Assert.Equal(Now.AddMinutes(2), plan.UpdatedAt);
    }
}
