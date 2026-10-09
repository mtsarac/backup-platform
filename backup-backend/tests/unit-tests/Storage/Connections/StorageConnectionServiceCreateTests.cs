using System.Text.Json;
using BackupPlatform.Domain.Audit;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Storage;

namespace BackupPlatform.UnitTests.Storage.Connections;

public sealed class StorageConnectionServiceCreateTests
{
    private const string DistinctivePlaintext = "hunter2-PLAINTEXT-7f3a";

    private readonly ServiceHarness _h = new();

    private Task<StorageResult<StorageConnectionView>> TenantCreate(CreateStorageConnectionCommand command) =>
        _h.Service.CreateAsync(_h.TenantScope(), _h.Tenant, command, _h.Actor, CancellationToken.None);

    private Task<StorageResult<StorageConnectionView>> PlatformCreate(CreateStorageConnectionCommand command) =>
        _h.Service.CreateAsync(StorageScope.Platform, _h.Tenant, command, _h.Actor, CancellationToken.None);

    private static CreateStorageConnectionCommand Command(
        string? providerType, string? name = "Archive", IReadOnlyDictionary<string, string>? configuration = null,
        IReadOnlyDictionary<string, string>? secret = null, string? quotaScope = null, string? ownership = null) =>
        new(providerType, name, ownership, quotaScope, configuration, secret);

    private static IReadOnlyDictionary<string, string> Map(params (string Key, string Value)[] pairs) => pairs.ToDictionary(pair => pair.Key, pair => pair.Value);

    private async Task<InvalidStorageConnectionException> Invalid(Task<StorageResult<StorageConnectionView>> call) =>
        await Assert.ThrowsAsync<InvalidStorageConnectionException>(() => call);

    private void AssertNothingLeftBehind()
    {
        Assert.Empty(_h.Store.All);
        Assert.Empty(_h.Secrets.Values);
        Assert.Empty(_h.Store.Audit);
    }

    // ---- ownership and provider rules --------------------------------------------------------------------------------------

    [Fact]
    public async Task TenantCreatesATenantManagedConnectionWithTheTenantDefaultQuotaScope()
    {
        var result = await TenantCreate(Command(TestProviders.Fake, "  Offsite  ", Map(("host", "backup.example"), ("port", "2222")), Map(("password", DistinctivePlaintext))));

        Assert.Equal(StorageConnectionOutcome.Changed, result.Outcome);
        var view = result.Value!;
        Assert.Equal("Offsite", view.Name);
        Assert.Equal(TestProviders.Fake, view.ProviderType);
        Assert.Equal(StorageOwnership.TenantManaged, view.Ownership);
        Assert.Equal("tenant", view.QuotaScope);
        Assert.True(view.Enabled);
        Assert.False(view.IsDefault);
        Assert.Equal(_h.Tenant.Value, view.TenantId);
        Assert.Equal(new Dictionary<string, string> { ["host"] = "backup.example", ["port"] = "2222" }, view.Configuration);
        Assert.True(view.HasSecret);
        Assert.Equal(["password"], view.SecretFieldNames);
        Assert.Equal(new StorageConnectionPermissions(true, false, true, true, true, true), view.Permissions);
        var stored = Assert.Single(_h.Store.All);
        Assert.Equal(_h.Tenant, stored.TenantId);
    }

    [Fact]
    public async Task TenantCannotCreateAProviderOnlyThePlatformOffers()
    {
        var result = await TenantCreate(Command(TestProviders.PlatformOnly, secret: Map(("apiKey", DistinctivePlaintext))));

        Assert.Equal(StorageConnectionOutcome.Rejected, result.Outcome);
        Assert.Equal(StorageRejections.PlatformManagedProvider, result.Code);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task PlatformCannotCreateAProviderOnlyTenantsOwn()
    {
        var result = await PlatformCreate(Command(TestProviders.Fake, secret: Map(("password", DistinctivePlaintext)), configuration: Map(("host", "h"))));

        Assert.Equal(StorageConnectionOutcome.Rejected, result.Outcome);
        Assert.Equal(StorageRejections.TenantManagedProvider, result.Code);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task PlatformCreatesAPlatformManagedConnectionWithTheManagedDefaultQuotaScope()
    {
        var result = await PlatformCreate(Command(TestProviders.PlatformOnly, "Central 1", secret: Map(("apiKey", "k"))));

        Assert.Equal(StorageConnectionOutcome.Changed, result.Outcome);
        Assert.Equal(StorageOwnership.PlatformManaged, result.Value!.Ownership);
        Assert.Equal("managed", result.Value.QuotaScope);
        var audit = Assert.Single(_h.Store.Audit);
        Assert.Equal(AuditActorKind.PlatformAdmin, audit.ActorKind);
    }

    [Fact]
    public async Task PlatformMayChooseAQuotaScopeOnCreateAndItIsValidated()
    {
        var created = await PlatformCreate(Command(TestProviders.PlatformOnly, "Gold", secret: Map(("apiKey", "k")), quotaScope: "gold-tier"));
        var invalid = await Invalid(PlatformCreate(Command(TestProviders.PlatformOnly, "Bad", secret: Map(("apiKey", "k")), quotaScope: "Not Valid")));

        Assert.Equal("gold-tier", created.Value!.QuotaScope);
        Assert.Equal(("quotaScope", "invalid_value"), (invalid.Field, invalid.Code));
    }

    [Fact]
    public async Task TenantSendingAQuotaScopeIsRefusedAsNotAllowed()
    {
        var exception = await Invalid(TenantCreate(Command(TestProviders.Plain, quotaScope: "managed")));

        Assert.Equal(("quotaScope", "not_allowed"), (exception.Field, exception.Code));
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task OwnershipMustMatchTheCallersScopeWhenGiven()
    {
        var tenantWithPlatformOwnership = await Invalid(TenantCreate(Command(TestProviders.Plain, ownership: "PlatformManaged")));
        var platformWithTenantOwnership = await Invalid(PlatformCreate(Command(TestProviders.PlatformOnly, ownership: "TenantManaged", secret: Map(("apiKey", "k")))));
        var tenantExplicit = await TenantCreate(Command(TestProviders.Plain, "Explicit", ownership: "TenantManaged"));

        Assert.Equal(("ownership", "invalid_value"), (tenantWithPlatformOwnership.Field, tenantWithPlatformOwnership.Code));
        Assert.Equal(("ownership", "invalid_value"), (platformWithTenantOwnership.Field, platformWithTenantOwnership.Code));
        Assert.Equal(StorageConnectionOutcome.Changed, tenantExplicit.Outcome);
    }

    [Theory]
    [InlineData(TestProviders.Planned)]
    [InlineData("google-drive")]
    [InlineData("does-not-exist")]
    [InlineData("")]
    [InlineData(null)]
    public async Task PlannedUnknownOrMissingProviderTypesAreNotAvailableToAnyone(string? providerType)
    {
        var tenant = await Invalid(TenantCreate(Command(providerType)));
        var platform = await Invalid(PlatformCreate(Command(providerType)));

        Assert.Equal(("providerType", "provider_not_available"), (tenant.Field, tenant.Code));
        Assert.Equal(("providerType", "provider_not_available"), (platform.Field, platform.Code));
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task PlannedProvidersCannotBeCreated()
    {
        var planned = new StorageProviderDescriptor("future", "Future", StorageProviderAvailability.Planned, null, null, [], [StorageOwnership.TenantManaged],
            tenantCreatable: true, platformCreatable: true, maxConnectionsPerTenant: null, [], [], StorageTestMode.None);
        var service = new StorageConnectionService(
            _h.Store, new StorageProviderCatalog([planned]), _h.Secrets, [], _h.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<StorageConnectionService>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidStorageConnectionException>(() => service.CreateAsync(
            StorageScope.Platform, _h.Tenant, Command("future"), _h.Actor, CancellationToken.None));
        Assert.Equal("provider_not_available", exception.Code);
    }

    // ---- name --------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NameIsRequired(string? name)
    {
        var exception = await Invalid(TenantCreate(Command(TestProviders.Plain, name)));

        Assert.Equal(("name", "required"), (exception.Field, exception.Code));
    }

    [Fact]
    public async Task OverlongOrControlCharacterNamesAreInvalidValues()
    {
        var tooLong = await Invalid(TenantCreate(Command(TestProviders.Plain, new string('n', StorageConnection.MaximumNameLength + 1))));
        var control = await Invalid(TenantCreate(Command(TestProviders.Plain, "bad\nname")));

        Assert.Equal(("name", "invalid_value"), (tooLong.Field, tooLong.Code));
        Assert.Equal(("name", "invalid_value"), (control.Field, control.Code));
    }

    [Fact]
    public async Task ADuplicateNameIgnoringCaseIsReportedAsNameTakenAndStoresNothingNew()
    {
        await TenantCreate(Command(TestProviders.Plain, "Archive"));

        var result = await TenantCreate(Command(TestProviders.Plain, "  ARCHIVE "));

        Assert.Equal(StorageConnectionOutcome.NameTaken, result.Outcome);
        Assert.Null(result.Value);
        Assert.Single(_h.Store.All);
    }

    [Fact]
    public async Task TheSameNameInAnotherTenantIsFine()
    {
        await TenantCreate(Command(TestProviders.Plain, "Archive"));

        var result = await _h.Service.CreateAsync(_h.OtherTenantScope, _h.OtherTenant, Command(TestProviders.Plain, "Archive"), _h.Actor, CancellationToken.None);

        Assert.Equal(StorageConnectionOutcome.Changed, result.Outcome);
    }

    [Fact]
    public async Task ThePerTenantProviderLimitIsEnforcedByTheStoreAndReported()
    {
        // The fake descriptor allows two connections per tenant.
        await TenantCreate(Command(TestProviders.Fake, "A", Map(("host", "h")), Map(("password", "p"))));
        await TenantCreate(Command(TestProviders.Fake, "B", Map(("host", "h")), Map(("password", "p"))));

        var third = await TenantCreate(Command(TestProviders.Fake, "C", Map(("host", "h")), Map(("password", DistinctivePlaintext))));

        Assert.Equal(StorageConnectionOutcome.LimitReached, third.Outcome);
        Assert.Equal(2, _h.Store.All.Count);
        Assert.Equal(2, _h.Secrets.Values.Count);
        Assert.DoesNotContain(_h.Secrets.Values.Values, value => value.Contains(DistinctivePlaintext, StringComparison.Ordinal));
    }

    // ---- configuration -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnknownConfigurationFieldIsRefusedNotStored()
    {
        var exception = await Invalid(TenantCreate(Command(TestProviders.Fake, configuration: Map(("host", "h"), ("password", "sneaky")), secret: Map(("password", "p")))));

        Assert.Equal(("configuration.password", "unknown_field"), (exception.Field, exception.Code));
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task RequiredConfigurationFieldMustBePresentAndNonBlank()
    {
        var missing = await Invalid(TenantCreate(Command(TestProviders.Fake, configuration: null, secret: Map(("password", "p")))));
        var blank = await Invalid(TenantCreate(Command(TestProviders.Fake, configuration: Map(("host", "   ")), secret: Map(("password", "p")))));

        Assert.Equal(("configuration.host", "required"), (missing.Field, missing.Code));
        Assert.Equal(("configuration.host", "required"), (blank.Field, blank.Code));
    }

    [Fact]
    public async Task ConfigurationValueLongerThanTheFieldMaximumIsTooLongAfterTrimming()
    {
        var atLimit = await TenantCreate(Command(TestProviders.Fake, "Fits", Map(("host", "  " + new string('h', 20) + "  ")), Map(("password", "p"))));
        var over = await Invalid(TenantCreate(Command(TestProviders.Fake, "Over", Map(("host", new string('h', 21))), Map(("password", "p")))));

        Assert.Equal(new string('h', 20), atLimit.Value!.Configuration["host"]);
        Assert.Equal(("configuration.host", "too_long"), (over.Field, over.Code));
    }

    [Theory]
    [InlineData("port", "12a")]
    [InlineData("port", "-5")]
    [InlineData("port", "1.5")]
    [InlineData("port", "+5")]
    [InlineData("port", "99999999999999999999")]
    [InlineData("useTls", "yes")]
    [InlineData("useTls", "True")]
    [InlineData("useTls", "1")]
    [InlineData("host", "bad\u0007value")]
    public async Task ValuesThatDoNotMatchTheFieldKindAreInvalid(string field, string value)
    {
        var configuration = new Dictionary<string, string> { ["host"] = "h", [field] = value };

        var exception = await Invalid(TenantCreate(Command(TestProviders.Fake, configuration: configuration, secret: Map(("password", "p")))));

        Assert.Equal(($"configuration.{field}", "invalid_value"), (exception.Field, exception.Code));
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task TypedValuesAreAcceptedAndOptionalBlankFieldsAreOmitted()
    {
        var result = await TenantCreate(Command(TestProviders.Fake, configuration: Map(("host", "h"), ("port", " 443 "), ("useTls", "false")), secret: Map(("password", "p"))));
        var blankOptional = await TenantCreate(Command(TestProviders.Fake, "Second", configuration: Map(("host", "h"), ("port", "  ")), secret: Map(("password", "p"))));

        Assert.Equal(new Dictionary<string, string> { ["host"] = "h", ["port"] = "443", ["useTls"] = "false" }, result.Value!.Configuration);
        Assert.Equal(new Dictionary<string, string> { ["host"] = "h" }, blankOptional.Value!.Configuration);
    }

    // ---- secrets -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SecretForAProviderWithoutSecretFieldsIsNotSupported()
    {
        var exception = await Invalid(TenantCreate(Command(TestProviders.Plain, secret: Map(("password", DistinctivePlaintext)))));

        Assert.Equal(("secret", "secrets_not_supported"), (exception.Field, exception.Code));
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task UnknownSecretFieldIsRefusedWithoutStoringAnything()
    {
        var exception = await Invalid(TenantCreate(Command(TestProviders.Fake, configuration: Map(("host", "h")), secret: Map(("password", "p"), ("apiKey", DistinctivePlaintext)))));

        Assert.Equal(("secret.apiKey", "unknown_field"), (exception.Field, exception.Code));
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task MissingRequiredSecretFieldIsRefusedWhetherOrNotOtherSecretFieldsWereSent()
    {
        var onlyOptional = await Invalid(TenantCreate(Command(TestProviders.Fake, configuration: Map(("host", "h")), secret: Map(("token", DistinctivePlaintext)))));
        var none = await Invalid(TenantCreate(Command(TestProviders.Fake, configuration: Map(("host", "h")), secret: null)));
        var blank = await Invalid(TenantCreate(Command(TestProviders.Fake, configuration: Map(("host", "h")), secret: Map(("password", "")))));

        Assert.Equal(("secret.password", "required"), (onlyOptional.Field, onlyOptional.Code));
        Assert.Equal(("secret.password", "required"), (none.Field, none.Code));
        Assert.Equal(("secret.password", "required"), (blank.Field, blank.Code));
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task OverlongSecretValueIsRefused()
    {
        var exception = await Invalid(TenantCreate(Command(TestProviders.Fake, configuration: Map(("host", "h")), secret: Map(("password", new string('x', 8193))))));

        Assert.Equal(("secret.password", "too_long"), (exception.Field, exception.Code));
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task OptionalSecretFieldsAreRecordedByNameOnlyAndInSortedOrder()
    {
        var result = await TenantCreate(Command(TestProviders.Fake, configuration: Map(("host", "h")), secret: Map(("token", "t"), ("password", "p"))));

        Assert.Equal(["password", "token"], result.Value!.SecretFieldNames);
    }

    [Fact]
    public async Task SecretValuesNeverAppearInTheViewTheStoredRowOrTheAuditDetails()
    {
        var result = await TenantCreate(Command(TestProviders.Fake, configuration: Map(("host", "h")), secret: Map(("password", DistinctivePlaintext), ("token", DistinctivePlaintext + "-t"))));

        var stored = Assert.Single(_h.Store.All);
        var viewJson = JsonSerializer.Serialize(result.Value);
        var auditJson = JsonSerializer.Serialize(_h.Store.Audit.Select(item => item.Details));
        var rowJson = JsonSerializer.Serialize(new { stored.Name, stored.Configuration, stored.SecretReference, stored.SecretFieldNames });
        Assert.DoesNotContain(DistinctivePlaintext, viewJson, StringComparison.Ordinal);
        Assert.DoesNotContain(DistinctivePlaintext, auditJson, StringComparison.Ordinal);
        Assert.DoesNotContain(DistinctivePlaintext, rowJson, StringComparison.Ordinal);
        Assert.NotEmpty(_h.Store.Audit);
        // ...yet the secret really is retrievable through the secret store by the row's reference.
        var resolved = await _h.Secrets.ResolveAsync(new BackupPlatform.SecretProviders.Abstractions.SecretReference(stored.SecretReference!), CancellationToken.None);
        Assert.Contains(DistinctivePlaintext, resolved.Reveal(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreationIsAuditedOnceWithTheActorKindAndNoCredentials()
    {
        await TenantCreate(Command(TestProviders.Fake, "Audited", Map(("host", "h")), Map(("password", DistinctivePlaintext))));

        var audit = Assert.Single(_h.Store.Audit);
        Assert.Equal(StorageConnectionAuditActions.Created, audit.Action);
        Assert.Equal(AuditActorKind.TenantAdministrator, audit.ActorKind);
        Assert.Equal("Audited", audit.Details["name"]);
        Assert.Equal(TestProviders.Fake, audit.Details["providerType"]);
        Assert.Equal("TenantManaged", audit.Details["ownership"]);
        Assert.Equal(new[] { "password" }, Assert.IsAssignableFrom<IEnumerable<string>>(audit.Details["secretFields"]));
    }

    [Fact]
    public async Task ASecretWrittenBeforeANameClashIsDeletedAgainSoNoOrphanRemains()
    {
        await TenantCreate(Command(TestProviders.Fake, "Archive", Map(("host", "h")), Map(("password", "first"))));
        var firstReference = Assert.Single(_h.Secrets.Values).Key;
        _h.Log.Entries.Clear();

        var clash = await TenantCreate(Command(TestProviders.Fake, "archive", Map(("host", "h")), Map(("password", DistinctivePlaintext))));

        Assert.Equal(StorageConnectionOutcome.NameTaken, clash.Outcome);
        Assert.Equal(firstReference, Assert.Single(_h.Secrets.Values).Key);
        Assert.Equal(2, _h.Log.Entries.Count);
        Assert.StartsWith("secret.set:", _h.Log.Entries[0], StringComparison.Ordinal);
        Assert.Equal(_h.Log.Entries[0].Replace("set", "delete", StringComparison.Ordinal), _h.Log.Entries[1]);
    }

    [Fact]
    public async Task AStoreFailureDuringCreationDeletesTheNewSecretAndPropagates()
    {
        _h.Store.ThrowOnCreate = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TenantCreate(Command(TestProviders.Fake, configuration: Map(("host", "h")), secret: Map(("password", DistinctivePlaintext)))));

        Assert.Empty(_h.Secrets.Values);
    }

    [Fact]
    public async Task AFailingSecretCleanupNeverTurnsAnOutcomeIntoAnException()
    {
        await TenantCreate(Command(TestProviders.Fake, "Archive", Map(("host", "h")), Map(("password", "first"))));
        _h.Secrets.ThrowOnDelete = true;

        var clash = await TenantCreate(Command(TestProviders.Fake, "ARCHIVE", Map(("host", "h")), Map(("password", "second"))));

        Assert.Equal(StorageConnectionOutcome.NameTaken, clash.Outcome);
    }

    // ---- tenant boundary ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATenantScopedCallerCannotCreateInAnotherTenant()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _h.Service.CreateAsync(_h.TenantScope(), _h.OtherTenant, Command(TestProviders.Plain), _h.Actor, CancellationToken.None));

        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task APlatformCallerMayCreateInAnyTenant()
    {
        var result = await _h.Service.CreateAsync(StorageScope.Platform, _h.OtherTenant, Command(TestProviders.PlatformOnly, secret: Map(("apiKey", "k"))), _h.Actor, CancellationToken.None);

        Assert.Equal(_h.OtherTenant.Value, result.Value!.TenantId);
    }

    [Fact]
    public async Task ListingAnotherTenantAsATenantScopedCallerIsRefusedAndAMissingTenantListsNull()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.Service.ListAsync(_h.TenantScope(), _h.OtherTenant, CancellationToken.None));

        _h.Store.TenantNames[_h.Tenant] = "Acme";
        Assert.Null(await _h.Service.ListAsync(StorageScope.Platform, _h.OtherTenant, CancellationToken.None));
    }

    [Fact]
    public async Task TenantNameIsExposedOnPlatformViewsOnly()
    {
        _h.Store.TenantNames[_h.Tenant] = "Acme";
        var seeded = _h.Seed(TestProviders.Plain, StorageOwnership.TenantManaged);

        var asPlatform = await _h.Service.GetAsync(StorageScope.Platform, seeded.Id, CancellationToken.None);
        var asTenant = await _h.Service.GetAsync(_h.TenantScope(), seeded.Id, CancellationToken.None);

        Assert.Equal("Acme", asPlatform!.TenantName);
        Assert.Null(asTenant!.TenantName);
    }

    [Fact]
    public async Task AnotherTenantsConnectionIsInvisibleToATenantScopedGet()
    {
        var seeded = _h.Seed(TestProviders.Plain, StorageOwnership.TenantManaged);

        Assert.Null(await _h.Service.GetAsync(_h.OtherTenantScope, seeded.Id, CancellationToken.None));
        Assert.NotNull(await _h.Service.GetAsync(_h.TenantScope(), seeded.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ViewOfAConnectionWithAnUnknownProviderFallsBackToPlannedWithoutCapabilities()
    {
        var seeded = _h.Seed("legacy-thing", StorageOwnership.TenantManaged);

        var view = (await _h.Service.GetAsync(_h.TenantScope(), seeded.Id, CancellationToken.None))!;

        Assert.Equal(StorageProviderAvailability.Planned, view.Availability);
        Assert.Equal("legacy-thing", view.ProviderDisplayName);
        Assert.Empty(view.Capabilities);
        Assert.False(view.Permissions.CanTest);
        Assert.False(view.Permissions.CanManageSecret);
    }

    [Fact]
    public async Task ViewerScopeGetsAReadOnlyViewOfTheCreatedConnection()
    {
        var seeded = _h.Seed(TestProviders.Plain, StorageOwnership.TenantManaged);

        var view = (await _h.Service.GetAsync(_h.TenantScope(canManage: false), seeded.Id, CancellationToken.None))!;

        Assert.Equal(new StorageConnectionPermissions(false, false, false, false, false, false), view.Permissions);
    }
}
