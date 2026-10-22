using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BackupPlatform.Application.Platform;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Storage;

public sealed record CreateStorageConnectionCommand(
    string? ProviderType, string? Name, string? Ownership, string? QuotaScope,
    IReadOnlyDictionary<string, string>? Configuration, IReadOnlyDictionary<string, string>? Secret);

public sealed record UpdateStorageConnectionCommand(string? Name, string? QuotaScope, IReadOnlyDictionary<string, string>? Configuration);

public sealed record StorageResult<T>(StorageConnectionOutcome Outcome, T? Value = default, string? Code = null) where T : class
{
    public bool Succeeded => Outcome is StorageConnectionOutcome.Changed or StorageConnectionOutcome.Unchanged;
}

/// <summary>The codes a rejected operation reports (<see cref="StorageConnectionOutcome.Rejected"/>).</summary>
public static class StorageRejections
{
    /// <summary>A tenant tried to change something only the platform operates.</summary>
    public const string PlatformManaged = "platform_managed";
    /// <summary>The platform tried to change something only the owning tenant operates (credentials, configuration).</summary>
    public const string TenantManaged = "tenant_managed";
    /// <summary>A tenant tried to create a connection of a provider only the platform offers.</summary>
    public const string PlatformManagedProvider = "platform_managed_provider";
    /// <summary>The platform tried to create a connection of a provider only tenants own.</summary>
    public const string TenantManagedProvider = "tenant_managed_provider";
}

/// <summary>
/// Connection management for tenants and for the platform, written against <see cref="StorageProviderDescriptor"/> only: nothing here knows a
/// provider by name. Credentials enter through <see cref="ISecretStore"/> and leave only as a reference and field NAMES; a view, an audit event
/// and a log line never carry a value.
/// </summary>
/// <remarks>
/// Secret/row consistency: a new secret is written under a fresh reference BEFORE the row points at it, and the previous secret is deleted only
/// AFTER the row's change committed. A failure in between leaves, at worst, an unreferenced ciphertext, never a connection pointing at a missing or
/// half-rotated secret.
/// </remarks>
public sealed class StorageConnectionService(
    IStorageConnectionStore store, IStorageProviderCatalog catalog, ISecretStore secrets, IEnumerable<IStorageConnectionTester> testers, TimeProvider time,
    ILogger<StorageConnectionService> logger)
{
    private readonly Dictionary<string, IStorageConnectionTester> _testers = testers.ToDictionary(tester => tester.ProviderType, StringComparer.Ordinal);

    public IReadOnlyList<StorageProviderDescriptor> Providers => catalog.Descriptors;

    public async Task<IReadOnlyList<StorageConnectionView>?> ListAsync(StorageScope scope, TenantId tenantId, CancellationToken cancellationToken)
    {
        EnsureWithin(scope, tenantId);
        var records = await store.ListAsync(tenantId, cancellationToken);
        return records?.Select(record => ToView(scope, record)).ToList();
    }

    public async Task<PagedResult<StorageConnectionView>> SearchAsync(StorageConnectionFilter filter, int page, int pageSize, CancellationToken cancellationToken)
    {
        var result = await store.SearchAsync(filter, page, pageSize, cancellationToken);
        return new PagedResult<StorageConnectionView>([.. result.Items.Select(record => ToView(StorageScope.Platform, record))], result.TotalCount, result.Page, result.PageSize);
    }

    public async Task<StorageConnectionView?> GetAsync(StorageScope scope, Guid id, CancellationToken cancellationToken)
    {
        var record = await store.FindAsync(id, scope.TenantId, cancellationToken);
        return record is null ? null : ToView(scope, record);
    }

    public async Task<StorageResult<StorageConnectionView>> CreateAsync(
        StorageScope scope, TenantId tenantId, CreateStorageConnectionCommand command, UserId actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureWithin(scope, tenantId);
        var descriptor = catalog.Find(command.ProviderType ?? string.Empty);
        if (descriptor is not { IsAvailable: true })
        {
            throw new InvalidStorageConnectionException("providerType", "provider_not_available");
        }
        var ownership = ResolveOwnership(scope, descriptor, command.Ownership, out var rejection);
        if (rejection is not null)
        {
            return new(StorageConnectionOutcome.Rejected, Code: rejection);
        }
        string name;
        try { name = StorageConnection.Names(command.Name).Display; }
        catch (ArgumentException) { throw new InvalidStorageConnectionException("name", string.IsNullOrWhiteSpace(command.Name) ? "required" : "invalid_value"); }
        string? quotaScope = null;
        if (command.QuotaScope is not null)
        {
            if (!scope.IsPlatform) throw new InvalidStorageConnectionException("quotaScope", "not_allowed");
            quotaScope = ValidateQuotaScope(command.QuotaScope);
        }
        var configuration = ValidateConfiguration(descriptor, command.Configuration);
        var secret = command.Secret is { Count: > 0 } || descriptor.ProviderType == StorageProviderTypes.Sftp
            ? ValidateSecret(descriptor, command.Secret, configuration) : null;
        if (secret is null && descriptor.SecretFields.Any(field => field.Required))
        {
            throw new InvalidStorageConnectionException($"secret.{descriptor.SecretFields.First(field => field.Required).Name}", "required");
        }

        if (descriptor.ProviderType == StorageProviderTypes.GoogleDrive)
            configuration["reauthorizationRequired"] = "true";
        var now = time.GetUtcNow();
        var id = Guid.NewGuid();
        var connection = new StorageConnection(id, tenantId, name, descriptor.ProviderType, ownership, quotaScope, configuration, isDefault: false, now);
        string? reference = null;
        if (secret is not null)
        {
            reference = NewReference(id);
            await secrets.SetAsync(new SecretReference(reference), secret.Value, cancellationToken);
            connection.AttachSecret(reference, secret.FieldNames, now);
        }
        var details = Details(connection);
        details["secretFields"] = secret?.FieldNames;
        StorageConnectionResult result;
        try
        {
            result = await store.CreateAsync(connection, descriptor.MaxConnectionsPerTenant, new StorageActor(actorId, scope.ActorKind), details, now, cancellationToken);
        }
        catch
        {
            await DiscardAsync(reference);
            throw;
        }
        if (result.Outcome != StorageConnectionOutcome.Changed)
        {
            await DiscardAsync(reference);
        }
        return Map(scope, result);
    }

    public async Task<StorageResult<StorageConnectionView>> UpdateAsync(
        StorageScope scope, Guid id, UpdateStorageConnectionCommand command, UserId actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var record = await store.FindAsync(id, scope.TenantId, cancellationToken);
        if (record is null)
        {
            return new(StorageConnectionOutcome.NotFound);
        }
        var descriptor = catalog.Find(record.Connection.ProviderType);
        var permissions = PermissionsFor(scope, record.Connection, descriptor);
        if (command.QuotaScope is not null && !scope.IsPlatform)
        {
            throw new InvalidStorageConnectionException("quotaScope", "not_allowed");
        }
        var changesDefinition = command.Name is not null || command.Configuration is not null;
        if (changesDefinition && !permissions.CanEdit)
        {
            return new(StorageConnectionOutcome.Rejected, Code: Refusal(scope));
        }
        if (command.QuotaScope is not null && !permissions.CanSetQuotaScope)
        {
            return new(StorageConnectionOutcome.Rejected, Code: Refusal(scope));
        }
        string? newName = null;
        if (command.Name is not null)
        {
            try { newName = StorageConnection.Names(command.Name).Display; }
            catch (ArgumentException) { throw new InvalidStorageConnectionException("name", string.IsNullOrWhiteSpace(command.Name) ? "required" : "invalid_value"); }
        }
        var newScope = command.QuotaScope is null ? null : ValidateQuotaScope(command.QuotaScope);
        var newConfiguration = command.Configuration is null || descriptor is null ? null : ValidateConfiguration(descriptor, command.Configuration);
        if (command.Configuration is not null && descriptor is null)
        {
            throw new InvalidStorageConnectionException("configuration", "unknown_field");
        }
        var expectedConfiguration = record.Connection.Configuration;
        var expectedSecret = record.Connection.SecretReference;
        StorageTestResult? updateTest = null;
        if (record.Connection.ProviderType == StorageProviderTypes.Sftp && newConfiguration is not null
            && newConfiguration["hostKeyFingerprint"] != expectedConfiguration.GetValueOrDefault("hostKeyFingerprint"))
        {
            updateTest = await RunTesterAsync(SftpConnectionRules.Candidate(record.Connection, newConfiguration), descriptor, cancellationToken);
            if (updateTest.Status != StorageTestStatus.Succeeded)
                return new(StorageConnectionOutcome.Rejected, Code: updateTest.Code ?? "rotation_test_failed");
        }

        var result = await store.MutateAsync(id, scope.TenantId, (connection, now) =>
        {
            if (connection.ProviderType == StorageProviderTypes.Sftp && newConfiguration is not null
                && (connection.SecretReference != expectedSecret || !SftpConnectionRules.SameConfiguration(connection.Configuration, expectedConfiguration)))
                return StorageMutationResult.Rejected("concurrent_update");
            var audit = new List<StorageAuditEntry>();
            var changed = new List<string>();
            if (newName is not null)
            {
                if (connection.Rename(newName, now)) { changed.Add("name"); }
            }
            if (newConfiguration is not null)
            {
                if (connection.ProviderType == StorageProviderTypes.GoogleDrive)
                    newConfiguration["reauthorizationRequired"] = connection.Configuration.GetValueOrDefault("reauthorizationRequired")
                        ?? (connection.HasSecret ? "false" : "true");
                var before = connection.Configuration.Keys.ToHashSet(StringComparer.Ordinal);
                var keysChanged = newConfiguration.Keys.Union(before, StringComparer.Ordinal)
                    .Where(key => !connection.Configuration.TryGetValue(key, out var current) || !newConfiguration.TryGetValue(key, out var next) || current != next).Order(StringComparer.Ordinal).ToList();
                if (connection.Reconfigure(newConfiguration, now)) { changed.Add("configuration"); changed.AddRange(keysChanged.Select(key => $"configuration.{key}")); }
                if (updateTest is not null) connection.RecordTest(updateTest.Status, updateTest.Code, now);
            }
            if (changed.Count > 0)
            {
                var details = Details(connection);
                details["changed"] = changed;
                audit.Add(new StorageAuditEntry(StorageConnectionAuditActions.Updated, details));
            }
            var quotaChanged = false;
            if (newScope is not null)
            {
                var previous = connection.QuotaScope;
                if (connection.ChangeQuotaScope(newScope, now))
                {
                    quotaChanged = true;
                    var details = Details(connection);
                    details["previousQuotaScope"] = previous;
                    audit.Add(new StorageAuditEntry(StorageConnectionAuditActions.QuotaScopeChanged, details));
                }
            }
            return audit.Count == 0 ? StorageMutationResult.NoChange : new StorageMutationResult(true, audit, quotaChanged);
        }, new StorageActor(actorId, scope.ActorKind), time.GetUtcNow(), cancellationToken);
        return Map(scope, result);
    }

    public async Task<StorageResult<StorageConnectionView>> SetSecretAsync(
        StorageScope scope, Guid id, IReadOnlyDictionary<string, string>? values, UserId actorId, CancellationToken cancellationToken)
    {
        var record = await store.FindAsync(id, scope.TenantId, cancellationToken);
        if (record is null)
        {
            return new(StorageConnectionOutcome.NotFound);
        }
        var descriptor = catalog.Find(record.Connection.ProviderType);
        if (!PermissionsFor(scope, record.Connection, descriptor).CanManageSecret)
        {
            if (PermissionsFor(scope, record.Connection, descriptor).CanEdit && descriptor is { IsAvailable: true, SecretFields.Count: 0 })
                throw new InvalidStorageConnectionException("secret", "secrets_not_supported");
            return new(StorageConnectionOutcome.Rejected, Code: Refusal(scope));
        }
        var expectedConfiguration = record.Connection.Configuration;
        var expectedReference = record.Connection.SecretReference;
        var secret = ValidateSecret(descriptor!, values, expectedConfiguration);
        var reference = NewReference(id);
        await secrets.SetAsync(new SecretReference(reference), secret.Value, cancellationToken);
        StorageTestResult? rotationTest = null;
        if (record.Connection.ProviderType == StorageProviderTypes.Sftp)
        {
            try
            {
                rotationTest = await RunTesterAsync(SftpConnectionRules.Candidate(record.Connection, expectedConfiguration, reference, secret.FieldNames), descriptor, cancellationToken);
                if (rotationTest.Status != StorageTestStatus.Succeeded)
                {
                    await DiscardAsync(reference);
                    return new(StorageConnectionOutcome.Rejected, Code: rotationTest.Code ?? "rotation_test_failed");
                }
            }
            catch
            {
                await DiscardAsync(reference);
                throw;
            }
        }
        string? superseded = null;
        StorageConnectionResult result;
        try
        {
            result = await store.MutateAsync(id, scope.TenantId, (connection, now) =>
            {
                if (connection.ProviderType == StorageProviderTypes.Sftp
                    && (connection.SecretReference != expectedReference || !SftpConnectionRules.SameConfiguration(connection.Configuration, expectedConfiguration)))
                    return StorageMutationResult.Rejected("concurrent_update");
                var hadSecret = connection.HasSecret;
                superseded = connection.AttachSecret(reference, secret.FieldNames, now);
                if (rotationTest is not null) connection.RecordTest(rotationTest.Status, rotationTest.Code, now);
                var details = Details(connection);
                details["secretFields"] = secret.FieldNames;
                details["replacedExisting"] = hadSecret;
                return new StorageMutationResult(true, [new StorageAuditEntry(StorageConnectionAuditActions.SecretRotated, details)]);
            }, new StorageActor(actorId, scope.ActorKind), time.GetUtcNow(), cancellationToken);
        }
        catch
        {
            await DiscardAsync(reference);
            throw;
        }
        if (result.Outcome != StorageConnectionOutcome.Changed)
        {
            await DiscardAsync(reference);
        }
        else
        {
            await DiscardAsync(superseded);
        }
        return Map(scope, result);
    }

    public async Task<StorageResult<StorageConnectionView>> ClearSecretAsync(StorageScope scope, Guid id, UserId actorId, CancellationToken cancellationToken)
    {
        var record = await store.FindAsync(id, scope.TenantId, cancellationToken);
        if (record is null)
        {
            return new(StorageConnectionOutcome.NotFound);
        }
        var descriptor = catalog.Find(record.Connection.ProviderType);
        if (!PermissionsFor(scope, record.Connection, descriptor).CanManageSecret)
        {
            if (PermissionsFor(scope, record.Connection, descriptor).CanEdit && descriptor is { IsAvailable: true, SecretFields.Count: 0 })
                throw new InvalidStorageConnectionException("secret", "secrets_not_supported");
            return new(StorageConnectionOutcome.Rejected, Code: Refusal(scope));
        }
        string? removed = null;
        var result = await store.MutateAsync(id, scope.TenantId, (connection, now) =>
        {
            removed = connection.DetachSecret(now);
            var configurationChanged = false;
            if (connection.ProviderType == StorageProviderTypes.GoogleDrive)
                configurationChanged = connection.Reconfigure(new Dictionary<string, string>(connection.Configuration, StringComparer.Ordinal)
                {
                    ["reauthorizationRequired"] = "true"
                }, now);
            return removed is null && !configurationChanged
                ? StorageMutationResult.NoChange
                : new StorageMutationResult(true, [new StorageAuditEntry(StorageConnectionAuditActions.SecretCleared, Details(connection))]);
        }, new StorageActor(actorId, scope.ActorKind), time.GetUtcNow(), cancellationToken);
        if (result.Outcome == StorageConnectionOutcome.Changed)
        {
            await DiscardAsync(removed);
        }
        return Map(scope, result);
    }

    public async Task<StorageResult<StorageConnectionView>> SetEnabledAsync(StorageScope scope, Guid id, bool enabled, UserId actorId, CancellationToken cancellationToken)
    {
        var record = await store.FindAsync(id, scope.TenantId, cancellationToken);
        if (record is null)
        {
            return new(StorageConnectionOutcome.NotFound);
        }
        if (!PermissionsFor(scope, record.Connection, catalog.Find(record.Connection.ProviderType)).CanToggle)
        {
            return new(StorageConnectionOutcome.Rejected, Code: Refusal(scope));
        }
        var result = await store.MutateAsync(id, scope.TenantId, (connection, now) =>
        {
            // Recheck under the tenant lock: a prefetched view must not let an enable race bypass a platform lock.
            if (!scope.IsPlatform && enabled && connection.AdministrativelyDisabled)
                return StorageMutationResult.Rejected("administratively_disabled");
            var changed = scope.IsPlatform
                ? connection.SetAdministrativelyDisabled(!enabled, now)
                : connection.SetEnabled(enabled, now);
            if (!changed) return StorageMutationResult.NoChange;
            var details = Details(connection);
            details["control"] = scope.IsPlatform ? "platform" : "owner";
            return new StorageMutationResult(true, [new StorageAuditEntry(
                enabled ? StorageConnectionAuditActions.Enabled : StorageConnectionAuditActions.Disabled, details)]);
        }, new StorageActor(actorId, scope.ActorKind), time.GetUtcNow(), cancellationToken);
        return Map(scope, result);
    }

    public async Task<StorageResult<StorageConnectionView>> DeleteAsync(StorageScope scope, Guid id, UserId actorId, CancellationToken cancellationToken)
    {
        var record = await store.FindAsync(id, scope.TenantId, cancellationToken);
        if (record is null)
        {
            return new(StorageConnectionOutcome.NotFound);
        }
        if (!scope.IsPlatform && record.Connection.AdministrativelyDisabled)
            return new(StorageConnectionOutcome.Rejected, Code: "administratively_disabled");
        if (!PermissionsFor(scope, record.Connection, catalog.Find(record.Connection.ProviderType)).CanDelete)
        {
            return new(StorageConnectionOutcome.Rejected, Code: Refusal(scope));
        }
        var result = await store.DeleteAsync(id, scope.TenantId, new StorageActor(actorId, scope.ActorKind), time.GetUtcNow(), cancellationToken);
        if (result.Outcome == StorageConnectionOutcome.Changed)
        {
            await DiscardAsync(result.Record?.Connection.SecretReference);
        }
        return Map(scope, result);
    }

    public async Task<StorageResult<StorageTestOutcome>> TestAsync(StorageScope scope, Guid id, UserId actorId, CancellationToken cancellationToken)
    {
        var record = await store.FindAsync(id, scope.TenantId, cancellationToken);
        if (record is null)
        {
            return new(StorageConnectionOutcome.NotFound);
        }
        var descriptor = catalog.Find(record.Connection.ProviderType);
        if (!scope.CanManage || scope.IsPlatform != (record.Connection.Ownership == StorageOwnership.PlatformManaged))
        {
            return new(StorageConnectionOutcome.Rejected, Code: Refusal(scope));
        }
        var watch = Stopwatch.StartNew();
        var testedConfiguration = record.Connection.Configuration;
        var testedSecret = record.Connection.SecretReference;
        var test = await RunTesterAsync(record.Connection, descriptor, cancellationToken);
        watch.Stop();
        StorageConnectionResult result = await store.MutateAsync(id, scope.TenantId, (connection, now) =>
        {
            IReadOnlyDictionary<string, string> currentConfiguration = connection.Configuration;
            IReadOnlyDictionary<string, string> expectedConfiguration = testedConfiguration;
            if (connection.ProviderType == StorageProviderTypes.GoogleDrive)
            {
                var current = new Dictionary<string, string>(currentConfiguration, StringComparer.Ordinal);
                var expected = new Dictionary<string, string>(expectedConfiguration, StringComparer.Ordinal);
                current.Remove("reauthorizationRequired");
                expected.Remove("reauthorizationRequired");
                currentConfiguration = current;
                expectedConfiguration = expected;
            }
            if (connection.SecretReference != testedSecret || !SftpConnectionRules.SameConfiguration(currentConfiguration, expectedConfiguration))
                return StorageMutationResult.Rejected("concurrent_update");
            connection.RecordTest(test.Status, test.Code, now);
            var details = Details(connection);
            details["status"] = test.Status.ToString();
            details["code"] = test.Code;
            return new StorageMutationResult(true, [new StorageAuditEntry(StorageConnectionAuditActions.Tested, details)]);
        }, new StorageActor(actorId, scope.ActorKind), time.GetUtcNow(), cancellationToken);
        if (result.Record is null || result.Outcome is not (StorageConnectionOutcome.Changed or StorageConnectionOutcome.Unchanged))
        {
            return new(result.Outcome, Code: result.Code);
        }
        var view = ToView(scope, result.Record);
        return new(StorageConnectionOutcome.Changed, new StorageTestOutcome(view, view.LastTest!, watch.Elapsed.TotalMilliseconds));
    }

    private async Task<StorageTestResult> RunTesterAsync(StorageConnection connection, StorageProviderDescriptor? descriptor, CancellationToken cancellationToken)
    {
        if (descriptor is not { IsAvailable: true } || !_testers.TryGetValue(connection.ProviderType, out var tester))
        {
            return new StorageTestResult(StorageTestStatus.Failed, StorageTestCodes.ProviderNotImplemented, StorageTestMessages.For(StorageTestCodes.ProviderNotImplemented));
        }
        try
        {
            return await tester.TestAsync(connection, secrets, cancellationToken);
        }
        catch (SecretUnavailableException)
        {
            return new StorageTestResult(StorageTestStatus.Failed, StorageTestCodes.SecretUnavailable, StorageTestMessages.For(StorageTestCodes.SecretUnavailable));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Provider exceptions can contain credentials; log only their type and the connection identity.
            logger.LogWarning("Storage connection test for {ConnectionId} ({ProviderType}) failed unexpectedly ({ExceptionType}).",
                connection.Id, connection.ProviderType, exception.GetType().Name);
            return new StorageTestResult(StorageTestStatus.Failed, StorageTestCodes.Unexpected, StorageTestMessages.For(StorageTestCodes.Unexpected));
        }
    }

    /// <summary>What the caller may do with this connection from this scope. Computed here once so no client has to derive it.</summary>
    public static StorageConnectionPermissions PermissionsFor(StorageScope scope, StorageConnection connection, StorageProviderDescriptor? descriptor)
    {
        if (!scope.CanManage)
        {
            return new(false, false, false, false, false, false);
        }
        var hasSecretFields = descriptor is { IsAvailable: true, SecretFields.Count: > 0 };
        var testable = descriptor is { IsAvailable: true };
        var platformManaged = connection.Ownership == StorageOwnership.PlatformManaged;
        return scope.IsPlatform
            ? new(CanEdit: platformManaged, CanSetQuotaScope: true, CanToggle: true, CanDelete: platformManaged, CanManageSecret: platformManaged && hasSecretFields, CanTest: platformManaged && testable)
            : new(CanEdit: !platformManaged, CanSetQuotaScope: false, CanToggle: !platformManaged, CanDelete: !platformManaged && !connection.AdministrativelyDisabled, CanManageSecret: !platformManaged && hasSecretFields, CanTest: !platformManaged && testable);
    }

    public StorageConnectionView ToView(StorageScope scope, StorageConnectionRecord record)
    {
        var connection = record.Connection;
        var descriptor = catalog.Find(connection.ProviderType);
        StorageTestSummary? lastTest = connection is { LastTestStatus: { } status, LastTestedAt: { } testedAt }
            ? new StorageTestSummary(status, connection.LastTestCode, StorageTestMessages.For(connection.LastTestCode), testedAt)
            : null;
        return new StorageConnectionView(
            connection.Id, connection.TenantId.Value, scope.IsPlatform ? record.TenantName : null, connection.Name, connection.ProviderType,
            descriptor?.DisplayName ?? connection.ProviderType, descriptor?.Availability ?? StorageProviderAvailability.Planned, connection.Ownership,
            connection.QuotaScope, connection.Enabled, connection.IsDefault, EffectiveCapabilities(connection, descriptor), connection.Configuration, connection.HasSecret,
            connection.SecretFieldNames, connection.SecretUpdatedAt, lastTest, record.ReplicaCount, record.StoredBytes, connection.CreatedAt, connection.UpdatedAt,
            connection.StateChangedAt, PermissionsFor(scope, connection, descriptor), connection.AdministrativelyDisabled, connection.EffectiveEnabled);
    }
    private static IReadOnlyList<string> EffectiveCapabilities(StorageConnection connection, StorageProviderDescriptor? descriptor)
    {
        var capabilities = descriptor?.Capabilities?.ToList() ?? [];
        if (connection.ProviderType == StorageProviderTypes.S3 && connection.Configuration.TryGetValue("objectLockMode", out var mode)
            && !string.IsNullOrWhiteSpace(mode) && !capabilities.Contains(StorageCapabilityIds.ImmutableStorage.Value))
            capabilities.Add(StorageCapabilityIds.ImmutableStorage.Value);
        return capabilities.Order(StringComparer.Ordinal).ToList();
    }

    private StorageResult<StorageConnectionView> Map(StorageScope scope, StorageConnectionResult result) =>
        new(result.Outcome, result.Record is null ? null : ToView(scope, result.Record), result.Code);

    private static string Refusal(StorageScope scope) => scope.IsPlatform ? StorageRejections.TenantManaged : StorageRejections.PlatformManaged;

    private static void EnsureWithin(StorageScope scope, TenantId tenantId)
    {
        if (scope.TenantId is { } scoped && scoped != tenantId)
        {
            throw new InvalidOperationException("A tenant-scoped request cannot address another tenant.");
        }
    }

    private static StorageOwnership ResolveOwnership(StorageScope scope, StorageProviderDescriptor descriptor, string? requested, out string? rejection)
    {
        rejection = null;
        StorageOwnership ownership;
        if (scope.IsPlatform)
        {
            if (!descriptor.PlatformCreatable)
            {
                rejection = StorageRejections.TenantManagedProvider;
                return StorageOwnership.PlatformManaged;
            }
            ownership = StorageOwnership.PlatformManaged;
            if (requested is not null && !string.Equals(requested, nameof(StorageOwnership.PlatformManaged), StringComparison.Ordinal))
            {
                throw new InvalidStorageConnectionException("ownership", "invalid_value");
            }
        }
        else
        {
            if (!descriptor.TenantCreatable)
            {
                rejection = StorageRejections.PlatformManagedProvider;
                return StorageOwnership.TenantManaged;
            }
            ownership = StorageOwnership.TenantManaged;
            if (requested is not null && !string.Equals(requested, nameof(StorageOwnership.TenantManaged), StringComparison.Ordinal))
            {
                throw new InvalidStorageConnectionException("ownership", "invalid_value");
            }
        }
        if (!descriptor.SupportedOwnerships.Contains(ownership))
        {
            rejection = scope.IsPlatform ? StorageRejections.TenantManagedProvider : StorageRejections.PlatformManagedProvider;
        }
        return ownership;
    }

    private static string ValidateQuotaScope(string value)
    {
        var label = value.Trim();
        if (!StorageQuotaScopes.IsValid(label)) throw new InvalidStorageConnectionException("quotaScope", "invalid_value");
        return label;
    }

    /// <summary>Only fields the descriptor declares, typed and bounded. Anything else (above all anything named like a credential the descriptor does not declare) is refused, never stored.</summary>
    public static Dictionary<string, string> ValidateConfiguration(StorageProviderDescriptor descriptor, IReadOnlyDictionary<string, string>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var supplied = values ?? new Dictionary<string, string>();
        foreach (var key in supplied.Keys)
        {
            if (descriptor.ConfigFields.All(field => field.Name != key))
            {
                throw new InvalidStorageConnectionException($"configuration.{key}", "unknown_field");
            }
        }
        foreach (var field in descriptor.ConfigFields)
        {
            supplied.TryGetValue(field.Name, out var raw);
            var value = raw?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                if (field.Required) throw new InvalidStorageConnectionException($"configuration.{field.Name}", "required");
                continue;
            }
            if (value.Length > field.MaxLength) throw new InvalidStorageConnectionException($"configuration.{field.Name}", "too_long");
            if (value.Any(char.IsControl)) throw new InvalidStorageConnectionException($"configuration.{field.Name}", "invalid_value");
            var valid = field.Kind switch
            {
                StorageFieldKind.Number => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _),
                StorageFieldKind.Boolean => value is "true" or "false",
                _ => true
            };
            if (!valid) throw new InvalidStorageConnectionException($"configuration.{field.Name}", "invalid_value");
            result[field.Name] = value;
        }
        if (descriptor.ProviderType == StorageProviderTypes.Sftp) SftpConnectionRules.ValidateConfiguration(result);
        NetworkStorageConnectionRules.Validate(result, descriptor.ProviderType);
        if (descriptor.ProviderType == StorageProviderTypes.Local && !StorageProviderTypes.IsValid(result.GetValueOrDefault("volume")))
            throw new InvalidStorageConnectionException("configuration.volume", "invalid_value");
        return result;
    }

    private sealed record ValidatedSecret(SecretValue Value, IReadOnlyList<string> FieldNames);

    private static ValidatedSecret ValidateSecret(StorageProviderDescriptor descriptor, IReadOnlyDictionary<string, string>? values,
        IReadOnlyDictionary<string, string> configuration)
    {
        if (descriptor.SecretFields.Count == 0) throw new InvalidStorageConnectionException("secret", "secrets_not_supported");
        var supplied = values ?? new Dictionary<string, string>();
        foreach (var key in supplied.Keys)
        {
            if (descriptor.SecretFields.All(field => field.Name != key)) throw new InvalidStorageConnectionException($"secret.{key}", "unknown_field");
        }
        var stored = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in descriptor.SecretFields)
        {
            supplied.TryGetValue(field.Name, out var value);
            if (string.IsNullOrEmpty(value))
            {
                if (field.Required) throw new InvalidStorageConnectionException($"secret.{field.Name}", "required");
                continue;
            }
            if (value.Length > 8192) throw new InvalidStorageConnectionException($"secret.{field.Name}", "too_long");
            stored[field.Name] = value;
        }
        if (descriptor.ProviderType == StorageProviderTypes.Sftp) SftpConnectionRules.ValidateSecret(configuration, stored);
        if (stored.Count == 0) throw new InvalidStorageConnectionException("secret", "required");
        return new ValidatedSecret(new SecretValue(JsonSerializer.Serialize(stored)), [.. stored.Keys]);
    }

    private static string NewReference(Guid connectionId) => $"sc-{connectionId:N}-{Guid.NewGuid().ToString("N")[..12]}";

    private static Dictionary<string, object?> Details(StorageConnection connection) => new()
    {
        ["name"] = connection.Name,
        ["providerType"] = connection.ProviderType,
        ["ownership"] = connection.Ownership.ToString(),
        ["quotaScope"] = connection.QuotaScope,
        ["enabled"] = connection.Enabled,
        ["administrativelyDisabled"] = connection.AdministrativelyDisabled,
        ["effectiveEnabled"] = connection.EffectiveEnabled
    };

    private async Task DiscardAsync(string? reference)
    {
        if (reference is null) return;
        try
        {
            await secrets.DeleteAsync(new SecretReference(reference), CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Only an unreferenced, encrypted value is left behind; it must not turn a committed change into a failure.
            logger.LogWarning("A superseded or abandoned storage secret could not be deleted ({ExceptionType}).", exception.GetType().Name);
        }
    }
}

/// <summary>Fixed, safe sentences for the stable test codes. Provider output is never repeated.</summary>
public static class StorageTestMessages
{
    public static string For(string? code) => code switch
    {
        StorageTestCodes.Ok => "Wrote, verified and deleted a probe object.",
        StorageTestCodes.AgentSideStorage => "This storage is reached by the agent, not by the platform; it is checked by the agent when a backup runs.",
        StorageTestCodes.StorageNotConfigured => "The platform has no central storage configured for this tenant.",
        StorageTestCodes.ProviderNotImplemented => "This provider type is not implemented yet, so the connection cannot be tested or used.",
        StorageTestCodes.SecretUnavailable => "The stored credentials could not be read.",
        StorageTestCodes.AccessDenied => "The storage refused access.",
        StorageTestCodes.InvalidConfiguration => "The storage is not configured correctly.",
        StorageTestCodes.InsufficientSpace => "The storage has no space left.",
        StorageTestCodes.IntegrityMismatch => "The probe object did not read back as written.",
        StorageTestCodes.NotSupported => "The storage does not support what the test needs.",
        StorageTestCodes.IoError => "The storage could not be read or written.",
        StorageTestCodes.HostKeyMismatch => "The remote SSH host key does not match the configured fingerprint.",
        StorageTestCodes.AuthenticationFailed => "The storage credentials were rejected by the provider.",
        StorageTestCodes.Timeout => "The connection to the storage provider timed out.",
        StorageTestCodes.NetworkDenied => "Access to this network destination is not permitted by operator policy.",
        StorageTestCodes.NetworkUnavailable => "The storage provider could not be reached over the network.",
        _ => "The connection test failed unexpectedly."
    };
}
