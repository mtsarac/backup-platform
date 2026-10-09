using System.Text.Json;
using BackupPlatform.Domain.Storage;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.Storage;

namespace BackupPlatform.UnitTests.Storage.Connections;

public sealed class StorageConnectionServiceSecretTests
{
    private const string Old = "old-PLAINTEXT-1111";
    private const string New = "new-PLAINTEXT-2222";

    private readonly ServiceHarness _h = new();

    private static IReadOnlyDictionary<string, string> Values(params (string Key, string Value)[] pairs) => pairs.ToDictionary(pair => pair.Key, pair => pair.Value);

    private Task<StorageResult<StorageConnectionView>> SetSecret(Guid id, IReadOnlyDictionary<string, string>? values, StorageScope? scope = null) =>
        _h.Service.SetSecretAsync(scope ?? _h.TenantScope(), id, values, _h.Actor, CancellationToken.None);

    private Task<StorageResult<StorageConnectionView>> ClearSecret(Guid id, StorageScope? scope = null) =>
        _h.Service.ClearSecretAsync(scope ?? _h.TenantScope(), id, _h.Actor, CancellationToken.None);

    private string SecretOf(StorageConnection connection) => connection.SecretReference!;

    // ---- rotation ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RotationWritesANewReferenceAndDeletesTheOldOneOnlyAfterTheRowCommitted()
    {
        var connection = await _h.CreateFakeAsync("Archive", Old);
        var oldReference = SecretOf(connection);

        var result = await SetSecret(connection.Id, Values(("password", New)));

        Assert.Equal(StorageConnectionOutcome.Changed, result.Outcome);
        var newReference = SecretOf(connection);
        Assert.NotEqual(oldReference, newReference);
        Assert.Equal([$"secret.set:{newReference}", "store.mutate", $"secret.delete:{oldReference}"], _h.Log.Entries);
        Assert.Equal([newReference], _h.Secrets.Values.Keys);
        Assert.Contains(New, _h.Secrets.Values[newReference], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotationIsAuditedAsRotatedWithFieldNamesOnlyAndNoPlaintext()
    {
        var connection = await _h.CreateFakeAsync("Archive", Old);

        var result = await SetSecret(connection.Id, Values(("password", New), ("token", New + "-t")));

        var audit = Assert.Single(_h.Store.Audit);
        Assert.Equal(StorageConnectionAuditActions.SecretRotated, audit.Action);
        Assert.Equal(true, audit.Details["replacedExisting"]);
        Assert.Equal(new[] { "password", "token" }, Assert.IsAssignableFrom<IEnumerable<string>>(audit.Details["secretFields"]));
        var json = JsonSerializer.Serialize(new { audit.Details, result.Value });
        Assert.DoesNotContain("PLAINTEXT", json, StringComparison.Ordinal);
        Assert.Equal(["password", "token"], result.Value!.SecretFieldNames);
        Assert.True(result.Value.HasSecret);
        Assert.False(audit.AffectsQuota);
    }

    [Fact]
    public async Task SettingASecretOnAConnectionWithoutOneRecordsThatNothingWasReplaced()
    {
        var connection = _h.Seed(TestProviders.Fake, StorageOwnership.TenantManaged, "Archive", configuration: Values(("host", "h")));

        var result = await SetSecret(connection.Id, Values(("password", New)));

        Assert.Equal(StorageConnectionOutcome.Changed, result.Outcome);
        Assert.Equal(false, Assert.Single(_h.Store.Audit).Details["replacedExisting"]);
        Assert.Single(_h.Secrets.Values);
        Assert.DoesNotContain(_h.Log.Entries, entry => entry.StartsWith("secret.delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailingStoreDuringRotationDiscardsTheNewSecretAndKeepsTheOldOneReferenced()
    {
        var connection = await _h.CreateFakeAsync("Archive", Old);
        var oldReference = SecretOf(connection);
        _h.Store.ThrowOnMutate = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => SetSecret(connection.Id, Values(("password", New))));

        Assert.Equal(oldReference, SecretOf(connection));
        Assert.Equal([oldReference], _h.Secrets.Values.Keys);
        Assert.Contains(Old, _h.Secrets.Values[oldReference], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StorageConnectionOutcome.NotFound)]
    [InlineData(StorageConnectionOutcome.ConcurrentUpdate)]
    [InlineData(StorageConnectionOutcome.NameTaken)]
    [InlineData(StorageConnectionOutcome.Rejected)]
    [InlineData(StorageConnectionOutcome.Unchanged)]
    public async Task ARotationTheStoreDoesNotCommitLeavesNoOrphanAndDoesNotDeleteTheOldSecret(StorageConnectionOutcome forced)
    {
        var connection = await _h.CreateFakeAsync("Archive", Old);
        var oldReference = SecretOf(connection);
        _h.Store.MutateOutcomeOverride = forced;

        var result = await SetSecret(connection.Id, Values(("password", New)));

        Assert.Equal(forced, result.Outcome);
        Assert.Equal(forced == StorageConnectionOutcome.Rejected ? "concurrent_rejection" : null, result.Code);
        Assert.Equal(oldReference, SecretOf(connection));
        Assert.Equal([oldReference], _h.Secrets.Values.Keys);
    }

    [Fact]
    public async Task AFailureToDeleteTheSupersededSecretDoesNotFailTheCommittedRotation()
    {
        var connection = await _h.CreateFakeAsync("Archive", Old);
        _h.Secrets.ThrowOnDelete = true;

        var result = await SetSecret(connection.Id, Values(("password", New)));

        Assert.Equal(StorageConnectionOutcome.Changed, result.Outcome);
        Assert.Contains(New, _h.Secrets.Values[SecretOf(connection)], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidSecretInputIsRefusedBeforeAnythingIsWritten()
    {
        var connection = await _h.CreateFakeAsync("Archive", Old);
        var reference = SecretOf(connection);

        var unknown = await Assert.ThrowsAsync<InvalidStorageConnectionException>(() => SetSecret(connection.Id, Values(("password", New), ("apiKey", "x"))));
        var missing = await Assert.ThrowsAsync<InvalidStorageConnectionException>(() => SetSecret(connection.Id, Values(("token", New))));
        var none = await Assert.ThrowsAsync<InvalidStorageConnectionException>(() => SetSecret(connection.Id, null));

        Assert.Equal(("secret.apiKey", "unknown_field"), (unknown.Field, unknown.Code));
        Assert.Equal(("secret.password", "required"), (missing.Field, missing.Code));
        Assert.Equal(("secret.password", "required"), (none.Field, none.Code));
        Assert.Equal(reference, SecretOf(connection));
        Assert.Empty(_h.Log.Entries);
    }

    [Fact]
    public async Task ASecretForAProviderWithoutSecretFieldsIsNotSupportedForSetAndClear()
    {
        var connection = _h.Seed(TestProviders.Plain, StorageOwnership.TenantManaged, "Plain");

        var set = await Assert.ThrowsAsync<InvalidStorageConnectionException>(() => SetSecret(connection.Id, Values(("password", New))));
        var clear = await Assert.ThrowsAsync<InvalidStorageConnectionException>(() => ClearSecret(connection.Id));

        Assert.Equal(("secret", "secrets_not_supported"), (set.Field, set.Code));
        Assert.Equal(("secret", "secrets_not_supported"), (clear.Field, clear.Code));
        Assert.Empty(_h.Secrets.Values);
    }

    // ---- who may manage secrets --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task OwnershipRefusalTakesPrecedenceOverSecretsNotSupported(bool platformCaller, bool clear)
    {
        var ownership = platformCaller ? StorageOwnership.TenantManaged : StorageOwnership.PlatformManaged;
        var connection = _h.Seed(platformCaller ? TestProviders.Plain : StorageProviderTypes.Central, ownership, "No secrets");
        var scope = platformCaller ? StorageScope.Platform : _h.TenantScope();

        var result = clear
            ? await ClearSecret(connection.Id, scope)
            : await SetSecret(connection.Id, Values(("password", New)), scope);

        Assert.Equal(StorageConnectionOutcome.Rejected, result.Outcome);
        Assert.Equal(platformCaller ? StorageRejections.TenantManaged : StorageRejections.PlatformManaged, result.Code);
        Assert.Empty(_h.Secrets.Values);
        Assert.Empty(_h.Store.Audit);
    }

    [Fact]
    public async Task PlatformCannotSetOrClearTheSecretOfATenantManagedConnection()
    {
        var connection = await _h.CreateFakeAsync("Archive", Old);
        var reference = SecretOf(connection);

        var set = await SetSecret(connection.Id, Values(("password", New)), StorageScope.Platform);
        var clear = await ClearSecret(connection.Id, StorageScope.Platform);

        Assert.Equal((StorageConnectionOutcome.Rejected, StorageRejections.TenantManaged), (set.Outcome, set.Code));
        Assert.Equal((StorageConnectionOutcome.Rejected, StorageRejections.TenantManaged), (clear.Outcome, clear.Code));
        Assert.Equal(reference, SecretOf(connection));
        Assert.Equal([reference], _h.Secrets.Values.Keys);
        Assert.Empty(_h.Log.Entries);
    }

    [Fact]
    public async Task TenantCannotSetOrClearTheSecretOfAPlatformManagedConnectionButPlatformCan()
    {
        var connection = _h.Seed(TestProviders.PlatformOnly, StorageOwnership.PlatformManaged, "Managed");

        var tenantSet = await SetSecret(connection.Id, Values(("apiKey", New)));
        var tenantClear = await ClearSecret(connection.Id);
        var platformSet = await SetSecret(connection.Id, Values(("apiKey", New)), StorageScope.Platform);
        var platformClear = await ClearSecret(connection.Id, StorageScope.Platform);

        Assert.Equal((StorageConnectionOutcome.Rejected, StorageRejections.PlatformManaged), (tenantSet.Outcome, tenantSet.Code));
        Assert.Equal((StorageConnectionOutcome.Rejected, StorageRejections.PlatformManaged), (tenantClear.Outcome, tenantClear.Code));
        Assert.Equal(StorageConnectionOutcome.Changed, platformSet.Outcome);
        Assert.Equal(StorageConnectionOutcome.Changed, platformClear.Outcome);
        Assert.Empty(_h.Secrets.Values);
    }

    [Fact]
    public async Task ViewerScopeCannotManageSecrets()
    {
        var connection = await _h.CreateFakeAsync("Archive", Old);

        var set = await SetSecret(connection.Id, Values(("password", New)), _h.TenantScope(canManage: false));
        var clear = await ClearSecret(connection.Id, _h.TenantScope(canManage: false));

        Assert.Equal(StorageConnectionOutcome.Rejected, set.Outcome);
        Assert.Equal(StorageConnectionOutcome.Rejected, clear.Outcome);
        Assert.True(connection.HasSecret);
    }

    [Fact]
    public async Task SecretOperationsOnAnotherTenantsConnectionAreNotFoundAndWriteNothing()
    {
        var foreign = _h.Seed(TestProviders.Fake, StorageOwnership.TenantManaged, "Foreign", _h.OtherTenant);

        var set = await SetSecret(foreign.Id, Values(("password", New)));
        var clear = await ClearSecret(foreign.Id);

        Assert.Equal(StorageConnectionOutcome.NotFound, set.Outcome);
        Assert.Equal(StorageConnectionOutcome.NotFound, clear.Outcome);
        Assert.Empty(_h.Secrets.Values);
        Assert.False(foreign.HasSecret);
    }

    // ---- clear / delete ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ClearingTheSecretDetachesItDeletesTheCiphertextAndIsAudited()
    {
        var connection = await _h.CreateFakeAsync("Archive", Old);
        var reference = SecretOf(connection);

        var result = await ClearSecret(connection.Id);

        Assert.Equal(StorageConnectionOutcome.Changed, result.Outcome);
        Assert.False(result.Value!.HasSecret);
        Assert.Empty(result.Value.SecretFieldNames);
        Assert.Empty(_h.Secrets.Values);
        Assert.Equal(["store.mutate", $"secret.delete:{reference}"], _h.Log.Entries);
        Assert.Equal(StorageConnectionAuditActions.SecretCleared, Assert.Single(_h.Store.Audit).Action);
    }

    [Fact]
    public async Task ClearingWhenThereIsNoSecretIsUnchangedWithoutAudit()
    {
        var connection = _h.Seed(TestProviders.Fake, StorageOwnership.TenantManaged, "Archive");

        var result = await ClearSecret(connection.Id);

        Assert.Equal(StorageConnectionOutcome.Unchanged, result.Outcome);
        Assert.Empty(_h.Store.Audit);
    }

    [Fact]
    public async Task ClearingKeepsTheCiphertextWhenTheStoreDoesNotCommit()
    {
        var connection = await _h.CreateFakeAsync("Archive", Old);
        _h.Store.MutateOutcomeOverride = StorageConnectionOutcome.ConcurrentUpdate;

        var result = await ClearSecret(connection.Id);

        Assert.Equal(StorageConnectionOutcome.ConcurrentUpdate, result.Outcome);
        Assert.Single(_h.Secrets.Values);
        Assert.True(connection.HasSecret);
    }

    [Fact]
    public async Task DeletingAConnectionDeletesItsSecret()
    {
        var connection = await _h.CreateFakeAsync("Archive", Old);

        var result = await _h.Service.DeleteAsync(_h.TenantScope(), connection.Id, _h.Actor, CancellationToken.None);

        Assert.Equal(StorageConnectionOutcome.Changed, result.Outcome);
        Assert.Empty(_h.Secrets.Values);
    }

    [Fact]
    public async Task ARefusedDeleteKeepsTheSecret()
    {
        var connection = await _h.CreateFakeAsync("Archive", Old);
        _h.Store.InUse.Add(connection.Id);

        var result = await _h.Service.DeleteAsync(_h.TenantScope(), connection.Id, _h.Actor, CancellationToken.None);

        Assert.Equal(StorageConnectionOutcome.InUse, result.Outcome);
        Assert.Single(_h.Secrets.Values);
    }
}
