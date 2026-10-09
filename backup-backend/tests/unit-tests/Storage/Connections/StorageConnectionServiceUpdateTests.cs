using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Storage;

namespace BackupPlatform.UnitTests.Storage.Connections;

public sealed class StorageConnectionServiceUpdateTests
{
    private readonly ServiceHarness _h = new();

    private static UpdateStorageConnectionCommand Update(string? name = null, string? quotaScope = null, IReadOnlyDictionary<string, string>? configuration = null) =>
        new(name, quotaScope, configuration);

    private Task<StorageResult<StorageConnectionView>> TenantUpdate(Guid id, UpdateStorageConnectionCommand command) =>
        _h.Service.UpdateAsync(_h.TenantScope(), id, command, _h.Actor, CancellationToken.None);

    private Task<StorageResult<StorageConnectionView>> PlatformUpdate(Guid id, UpdateStorageConnectionCommand command) =>
        _h.Service.UpdateAsync(StorageScope.Platform, id, command, _h.Actor, CancellationToken.None);

    private StorageConnection SeedFake(string name = "Archive", string host = "h") =>
        _h.Seed(TestProviders.Fake, StorageOwnership.TenantManaged, name, configuration: new Dictionary<string, string> { ["host"] = host });

    // ---- permission matrix -------------------------------------------------------------------------------------------------

    private static StorageConnection Connection(string providerType, StorageOwnership ownership) =>
        new(Guid.NewGuid(), TenantId.New(), "c", providerType, ownership, null, null, false, ServiceHarness.Start);

    private static StorageConnectionPermissions Permissions(StorageScope scope, StorageOwnership ownership, StorageProviderDescriptor? descriptor) =>
        StorageConnectionService.PermissionsFor(scope, Connection(descriptor?.ProviderType ?? "unknown", ownership), descriptor);

    private static readonly StorageConnectionPermissions None = new(false, false, false, false, false, false);

    [Fact]
    public void TenantManagesItsOwnConnectionsButNeverSetsTheQuotaScope()
    {
        var scope = StorageScope.Tenant(TenantId.New(), true);

        Assert.Equal(new(true, false, true, true, true, true), Permissions(scope, StorageOwnership.TenantManaged, TestProviders.FakeDescriptor));
    }

    [Fact]
    public void TenantCannotManageSecretsOfAProviderWithoutSecretFieldsButCanTestIt()
    {
        var scope = StorageScope.Tenant(TenantId.New(), true);

        Assert.Equal(new(true, false, true, true, false, true), Permissions(scope, StorageOwnership.TenantManaged, TestProviders.PlainDescriptor));
    }

    [Fact]
    public void PlatformManagedConnectionsAreReadOnlyForTenants()
    {
        var scope = StorageScope.Tenant(TenantId.New(), true);

        Assert.Equal(None, Permissions(scope, StorageOwnership.PlatformManaged, TestProviders.PlatformOnlyDescriptor));
    }

    [Fact]
    public void PlatformFullyManagesPlatformManagedConnections()
    {
        Assert.Equal(new(true, true, true, true, true, true), Permissions(StorageScope.Platform, StorageOwnership.PlatformManaged, TestProviders.PlatformOnlyDescriptor));
    }

    [Fact]
    public void PlatformCannotManageSecretsOfAPlatformConnectionWhoseProviderHasNoSecretFields()
    {
        Assert.Equal(new(true, true, true, true, false, true), Permissions(StorageScope.Platform, StorageOwnership.PlatformManaged, BuiltInStorageProviders.Central));
    }

    [Fact]
    public void PlatformOnlyViewsTogglesAndSetsTheQuotaScopeOfTenantManagedConnections()
    {
        Assert.Equal(new(false, true, true, false, false, false), Permissions(StorageScope.Platform, StorageOwnership.TenantManaged, TestProviders.FakeDescriptor));
    }

    [Fact]
    public void ViewerScopeGetsNothingEvenOnTenantManagedConnectionsAndEvenForThePlatformFlagCombination()
    {
        Assert.Equal(None, Permissions(StorageScope.Tenant(TenantId.New(), false), StorageOwnership.TenantManaged, TestProviders.FakeDescriptor));
        Assert.Equal(None, Permissions(new StorageScope(null, false), StorageOwnership.PlatformManaged, TestProviders.PlatformOnlyDescriptor));
    }

    [Theory]
    [InlineData(StorageOwnership.TenantManaged, true)]
    [InlineData(StorageOwnership.PlatformManaged, false)]
    public void APlannedOrUnknownProviderCanNeitherBeTestedNorHaveSecretsButCanStillBeToggledByTheOwner(StorageOwnership ownership, bool tenantOwner)
    {
        var scope = tenantOwner ? StorageScope.Tenant(TenantId.New(), true) : StorageScope.Platform;

        foreach (var descriptor in new StorageProviderDescriptor?[] { TestProviders.PlannedDescriptor, null })
        {
            var permissions = Permissions(scope, ownership, descriptor);

            Assert.False(permissions.CanTest);
            Assert.False(permissions.CanManageSecret);
            Assert.True(permissions.CanToggle);
        }
    }

    // ---- update ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TenantCanRenameAndReconfigureItsConnectionAndIsToldBothInOneAuditEntry()
    {
        var connection = SeedFake("Archive", "old");
        _h.Clock.Advance(TimeSpan.FromHours(1));

        var result = await TenantUpdate(connection.Id, Update("  Cold  ", configuration: new Dictionary<string, string> { ["host"] = "new", ["port"] = "22" }));

        Assert.Equal(StorageConnectionOutcome.Changed, result.Outcome);
        Assert.Equal("Cold", result.Value!.Name);
        Assert.Equal(new Dictionary<string, string> { ["host"] = "new", ["port"] = "22" }, result.Value.Configuration);
        Assert.Equal(ServiceHarness.Start.AddHours(1), result.Value.UpdatedAt);
        var audit = Assert.Single(_h.Store.Audit);
        Assert.Equal(StorageConnectionAuditActions.Updated, audit.Action);
        Assert.Equal(new[] { "name", "configuration", "configuration.host", "configuration.port" }, Assert.IsAssignableFrom<IEnumerable<string>>(audit.Details["changed"]));
        Assert.False(audit.AffectsQuota);
    }

    [Fact]
    public async Task UpdateThatChangesNothingIsUnchangedWithoutAuditOrTimestampMovement()
    {
        var connection = SeedFake("Archive", "h");
        _h.Clock.Advance(TimeSpan.FromHours(1));

        var sameName = await TenantUpdate(connection.Id, Update("Archive"));
        var sameConfig = await TenantUpdate(connection.Id, Update(configuration: new Dictionary<string, string> { ["host"] = "h" }));
        var empty = await TenantUpdate(connection.Id, Update());

        Assert.All([sameName, sameConfig, empty], result => Assert.Equal(StorageConnectionOutcome.Unchanged, result.Outcome));
        Assert.Empty(_h.Store.Audit);
        Assert.Equal(ServiceHarness.Start, connection.UpdatedAt);
    }

    [Fact]
    public async Task OnlyTheConfigurationKeysThatActuallyChangedAreNamedInTheAudit()
    {
        var connection = _h.Seed(TestProviders.Fake, StorageOwnership.TenantManaged, "Archive", configuration: new Dictionary<string, string> { ["host"] = "h", ["port"] = "1" });

        await TenantUpdate(connection.Id, Update(configuration: new Dictionary<string, string> { ["host"] = "h", ["port"] = "2" }));

        var audit = Assert.Single(_h.Store.Audit);
        Assert.Equal(new[] { "configuration", "configuration.port" }, Assert.IsAssignableFrom<IEnumerable<string>>(audit.Details["changed"]));
    }

    [Fact]
    public async Task UpdateValidatesNameAndConfigurationLikeCreate()
    {
        var connection = SeedFake();

        var blank = await Assert.ThrowsAsync<InvalidStorageConnectionException>(() => TenantUpdate(connection.Id, Update("  ")));
        var control = await Assert.ThrowsAsync<InvalidStorageConnectionException>(() => TenantUpdate(connection.Id, Update("a\tb")));
        var unknown = await Assert.ThrowsAsync<InvalidStorageConnectionException>(() => TenantUpdate(connection.Id, Update(configuration: new Dictionary<string, string> { ["host"] = "h", ["secretish"] = "x" })));
        var required = await Assert.ThrowsAsync<InvalidStorageConnectionException>(() => TenantUpdate(connection.Id, Update(configuration: new Dictionary<string, string>())));

        Assert.Equal(("name", "required"), (blank.Field, blank.Code));
        Assert.Equal(("name", "invalid_value"), (control.Field, control.Code));
        Assert.Equal(("configuration.secretish", "unknown_field"), (unknown.Field, unknown.Code));
        Assert.Equal(("configuration.host", "required"), (required.Field, required.Code));
        Assert.Equal("Archive", connection.Name);
    }

    [Fact]
    public async Task RenameToAnExistingNameIsNameTakenAndTheWholeMutationIsRolledBack()
    {
        SeedFake("Existing");
        var connection = SeedFake("Archive", "old");

        var result = await TenantUpdate(connection.Id, Update("  EXISTING ", configuration: new Dictionary<string, string> { ["host"] = "new" }));

        Assert.Equal(StorageConnectionOutcome.NameTaken, result.Outcome);
        Assert.Equal("Archive", connection.Name);
        Assert.Equal("old", connection.Configuration["host"]);
        Assert.Equal(ServiceHarness.Start, connection.UpdatedAt);
        Assert.Empty(_h.Store.Audit);
    }

    [Fact]
    public async Task TenantCannotEditAPlatformManagedConnection()
    {
        var connection = _h.Seed(TestProviders.PlatformOnly, StorageOwnership.PlatformManaged, "Managed");

        var result = await TenantUpdate(connection.Id, Update("Hijacked"));

        Assert.Equal(StorageConnectionOutcome.Rejected, result.Outcome);
        Assert.Equal(StorageRejections.PlatformManaged, result.Code);
        Assert.Equal("Managed", connection.Name);
        Assert.Empty(_h.Store.Audit);
    }

    [Fact]
    public async Task PlatformCannotEditATenantManagedConnectionDefinition()
    {
        var connection = SeedFake("Archive", "h");

        var rename = await PlatformUpdate(connection.Id, Update("Renamed"));
        var reconfigure = await PlatformUpdate(connection.Id, Update(configuration: new Dictionary<string, string> { ["host"] = "evil" }));

        Assert.Equal((StorageConnectionOutcome.Rejected, StorageRejections.TenantManaged), (rename.Outcome, rename.Code));
        Assert.Equal((StorageConnectionOutcome.Rejected, StorageRejections.TenantManaged), (reconfigure.Outcome, reconfigure.Code));
        Assert.Equal("Archive", connection.Name);
        Assert.Equal("h", connection.Configuration["host"]);
    }

    [Fact]
    public async Task PlatformCanSetTheQuotaScopeOfATenantManagedConnectionAndItIsAuditedAsAffectingQuota()
    {
        var connection = SeedFake();

        var result = await PlatformUpdate(connection.Id, Update(quotaScope: "  gold  "));

        Assert.Equal(StorageConnectionOutcome.Changed, result.Outcome);
        Assert.Equal("gold", result.Value!.QuotaScope);
        var audit = Assert.Single(_h.Store.Audit);
        Assert.Equal(StorageConnectionAuditActions.QuotaScopeChanged, audit.Action);
        Assert.Equal("tenant", audit.Details["previousQuotaScope"]);
        Assert.Equal("gold", audit.Details["quotaScope"]);
        Assert.True(audit.AffectsQuota);
    }

    [Fact]
    public async Task QuotaScopeUpdateWithTheSameLabelIsUnchanged()
    {
        var connection = SeedFake();

        var result = await PlatformUpdate(connection.Id, Update(quotaScope: "tenant"));

        Assert.Equal(StorageConnectionOutcome.Unchanged, result.Outcome);
        Assert.Empty(_h.Store.Audit);
    }

    [Fact]
    public async Task InvalidQuotaScopeLabelIsRefused()
    {
        var connection = SeedFake();

        var exception = await Assert.ThrowsAsync<InvalidStorageConnectionException>(() => PlatformUpdate(connection.Id, Update(quotaScope: "Not Valid")));

        Assert.Equal(("quotaScope", "invalid_value"), (exception.Field, exception.Code));
        Assert.Equal("tenant", connection.QuotaScope);
    }

    [Fact]
    public async Task TenantSettingTheQuotaScopeIsNotAllowedEvenOnItsOwnConnectionAndBeforeAnyPermissionCheck()
    {
        var own = SeedFake();
        var managed = _h.Seed(TestProviders.PlatformOnly, StorageOwnership.PlatformManaged, "Managed");

        var onOwn = await Assert.ThrowsAsync<InvalidStorageConnectionException>(() => TenantUpdate(own.Id, Update(quotaScope: "gold")));
        var onManaged = await Assert.ThrowsAsync<InvalidStorageConnectionException>(() => TenantUpdate(managed.Id, Update("X", "gold")));

        Assert.Equal(("quotaScope", "not_allowed"), (onOwn.Field, onOwn.Code));
        Assert.Equal(("quotaScope", "not_allowed"), (onManaged.Field, onManaged.Code));
        Assert.Equal("tenant", own.QuotaScope);
        Assert.Equal("managed", managed.QuotaScope);
    }

    [Fact]
    public async Task APlatformUpdateOfTenantOwnedDefinitionRejectsTheWholeRequestEvenWhenItAlsoCarriesAnAllowedQuotaScope()
    {
        var connection = SeedFake("Archive");

        var result = await PlatformUpdate(connection.Id, Update("Renamed", "gold"));

        Assert.Equal((StorageConnectionOutcome.Rejected, StorageRejections.TenantManaged), (result.Outcome, result.Code));
        Assert.Equal("Archive", connection.Name);
        Assert.Equal("tenant", connection.QuotaScope);
        Assert.Empty(_h.Store.Audit);
    }

    [Fact]
    public async Task PlatformRenamingAndRescopingAPlatformConnectionWritesTwoAuditEntriesAndFlagsQuotaOnce()
    {
        var connection = _h.Seed(TestProviders.PlatformOnly, StorageOwnership.PlatformManaged, "Managed");

        var result = await PlatformUpdate(connection.Id, Update("Managed 2", "gold"));

        Assert.Equal(StorageConnectionOutcome.Changed, result.Outcome);
        Assert.Equal([StorageConnectionAuditActions.Updated, StorageConnectionAuditActions.QuotaScopeChanged], _h.Store.Audit.Select(item => item.Action));
        Assert.All(_h.Store.Audit, item => Assert.True(item.AffectsQuota));
    }

    [Fact]
    public async Task RenamingAlonePlatformSideDoesNotFlagQuotaAndRescopingAloneEmitsNoUpdatedEntry()
    {
        var connection = _h.Seed(TestProviders.PlatformOnly, StorageOwnership.PlatformManaged, "Managed");

        await PlatformUpdate(connection.Id, Update("Managed 2"));
        var renameAudit = Assert.Single(_h.Store.Audit);
        _h.Store.Audit.Clear();
        await PlatformUpdate(connection.Id, Update(quotaScope: "gold"));
        var rescopeAudit = Assert.Single(_h.Store.Audit);

        Assert.Equal((StorageConnectionAuditActions.Updated, false), (renameAudit.Action, renameAudit.AffectsQuota));
        Assert.Equal((StorageConnectionAuditActions.QuotaScopeChanged, true), (rescopeAudit.Action, rescopeAudit.AffectsQuota));
    }

    [Fact]
    public async Task ViewerScopeCannotChangeAnything()
    {
        var connection = SeedFake("Archive");

        var result = await _h.Service.UpdateAsync(_h.TenantScope(canManage: false), connection.Id, Update("Renamed"), _h.Actor, CancellationToken.None);

        Assert.Equal(StorageConnectionOutcome.Rejected, result.Outcome);
        Assert.Equal("Archive", connection.Name);
    }

    [Fact]
    public async Task UpdatingAConnectionOfAnotherTenantIsNotFoundForATenantScopedCaller()
    {
        var foreign = _h.Seed(TestProviders.Fake, StorageOwnership.TenantManaged, "Foreign", _h.OtherTenant, configuration: new Dictionary<string, string> { ["host"] = "h" });

        var result = await TenantUpdate(foreign.Id, Update("Mine now"));

        Assert.Equal(StorageConnectionOutcome.NotFound, result.Outcome);
        Assert.Null(result.Value);
        Assert.Equal("Foreign", foreign.Name);
        Assert.Equal(StorageConnectionOutcome.NotFound, (await TenantUpdate(Guid.NewGuid(), Update("x"))).Outcome);
    }

    // ---- enable / disable --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DisablingStampsStateChangeAuditsOnceAndRepeatingItIsUnchanged()
    {
        var connection = SeedFake();
        _h.Clock.Advance(TimeSpan.FromMinutes(7));

        var first = await _h.Service.SetEnabledAsync(_h.TenantScope(), connection.Id, false, _h.Actor, CancellationToken.None);
        _h.Clock.Advance(TimeSpan.FromMinutes(7));
        var second = await _h.Service.SetEnabledAsync(_h.TenantScope(), connection.Id, false, _h.Actor, CancellationToken.None);

        Assert.Equal(StorageConnectionOutcome.Changed, first.Outcome);
        Assert.False(first.Value!.Enabled);
        Assert.Equal(ServiceHarness.Start.AddMinutes(7), first.Value.StateChangedAt);
        Assert.Equal(StorageConnectionOutcome.Unchanged, second.Outcome);
        Assert.Equal(ServiceHarness.Start.AddMinutes(7), second.Value!.StateChangedAt);
        var audit = Assert.Single(_h.Store.Audit);
        Assert.Equal(StorageConnectionAuditActions.Disabled, audit.Action);
        Assert.False(audit.AffectsQuota);
    }

    [Fact]
    public async Task EnablingAnEnabledConnectionIsUnchangedWithoutAuditAndReEnablingAuditsEnabled()
    {
        var connection = SeedFake();

        var noop = await _h.Service.SetEnabledAsync(_h.TenantScope(), connection.Id, true, _h.Actor, CancellationToken.None);
        await _h.Service.SetEnabledAsync(_h.TenantScope(), connection.Id, false, _h.Actor, CancellationToken.None);
        var enabled = await _h.Service.SetEnabledAsync(_h.TenantScope(), connection.Id, true, _h.Actor, CancellationToken.None);

        Assert.Equal(StorageConnectionOutcome.Unchanged, noop.Outcome);
        Assert.Null(noop.Value!.StateChangedAt);
        Assert.True(enabled.Value!.Enabled);
        Assert.Equal([StorageConnectionAuditActions.Disabled, StorageConnectionAuditActions.Enabled], _h.Store.Audit.Select(item => item.Action));
    }

    [Fact]
    public async Task PlatformCanToggleATenantManagedConnectionButTenantCannotToggleAPlatformManagedOne()
    {
        var tenantOwned = SeedFake();
        var platformOwned = _h.Seed(TestProviders.PlatformOnly, StorageOwnership.PlatformManaged, "Managed");

        var platform = await _h.Service.SetEnabledAsync(StorageScope.Platform, tenantOwned.Id, false, _h.Actor, CancellationToken.None);
        var tenant = await _h.Service.SetEnabledAsync(_h.TenantScope(), platformOwned.Id, false, _h.Actor, CancellationToken.None);

        Assert.Equal(StorageConnectionOutcome.Changed, platform.Outcome);
        Assert.False(tenantOwned.Enabled);
        Assert.Equal((StorageConnectionOutcome.Rejected, StorageRejections.PlatformManaged), (tenant.Outcome, tenant.Code));
        Assert.True(platformOwned.Enabled);
    }

    [Fact]
    public async Task ViewerScopeCannotToggleAndAnotherTenantsConnectionIsNotFound()
    {
        var connection = SeedFake();
        var foreign = _h.Seed(TestProviders.Fake, StorageOwnership.TenantManaged, "Foreign", _h.OtherTenant);

        var viewer = await _h.Service.SetEnabledAsync(_h.TenantScope(canManage: false), connection.Id, false, _h.Actor, CancellationToken.None);
        var crossTenant = await _h.Service.SetEnabledAsync(_h.TenantScope(), foreign.Id, false, _h.Actor, CancellationToken.None);

        Assert.Equal(StorageConnectionOutcome.Rejected, viewer.Outcome);
        Assert.True(connection.Enabled);
        Assert.Equal(StorageConnectionOutcome.NotFound, crossTenant.Outcome);
        Assert.True(foreign.Enabled);
    }

    // ---- delete ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TenantDeletesItsOwnConnection()
    {
        var connection = SeedFake();

        var result = await _h.Service.DeleteAsync(_h.TenantScope(), connection.Id, _h.Actor, CancellationToken.None);

        Assert.Equal(StorageConnectionOutcome.Changed, result.Outcome);
        Assert.Empty(_h.Store.All);
        Assert.Equal(StorageConnectionAuditActions.Deleted, Assert.Single(_h.Store.Audit).Action);
    }

    [Fact]
    public async Task ConnectionsStillInUseAreKeptAndReported()
    {
        var connection = SeedFake();
        _h.Store.InUse.Add(connection.Id);

        var result = await _h.Service.DeleteAsync(_h.TenantScope(), connection.Id, _h.Actor, CancellationToken.None);

        Assert.Equal(StorageConnectionOutcome.InUse, result.Outcome);
        Assert.Single(_h.Store.All);
    }

    [Fact]
    public async Task PlatformCannotDeleteATenantManagedConnectionAndTenantCannotDeleteAPlatformManagedOne()
    {
        var tenantOwned = SeedFake();
        var platformOwned = _h.Seed(TestProviders.PlatformOnly, StorageOwnership.PlatformManaged, "Managed");

        var platform = await _h.Service.DeleteAsync(StorageScope.Platform, tenantOwned.Id, _h.Actor, CancellationToken.None);
        var tenant = await _h.Service.DeleteAsync(_h.TenantScope(), platformOwned.Id, _h.Actor, CancellationToken.None);
        var platformOwn = await _h.Service.DeleteAsync(StorageScope.Platform, platformOwned.Id, _h.Actor, CancellationToken.None);

        Assert.Equal((StorageConnectionOutcome.Rejected, StorageRejections.TenantManaged), (platform.Outcome, platform.Code));
        Assert.Equal((StorageConnectionOutcome.Rejected, StorageRejections.PlatformManaged), (tenant.Outcome, tenant.Code));
        Assert.Equal(StorageConnectionOutcome.Changed, platformOwn.Outcome);
        Assert.Same(tenantOwned, Assert.Single(_h.Store.All));
    }

    [Fact]
    public async Task DeletingAnotherTenantsConnectionIsNotFound()
    {
        var foreign = _h.Seed(TestProviders.Fake, StorageOwnership.TenantManaged, "Foreign", _h.OtherTenant);

        var result = await _h.Service.DeleteAsync(_h.TenantScope(), foreign.Id, _h.Actor, CancellationToken.None);

        Assert.Equal(StorageConnectionOutcome.NotFound, result.Outcome);
        Assert.Single(_h.Store.All);
    }
}
