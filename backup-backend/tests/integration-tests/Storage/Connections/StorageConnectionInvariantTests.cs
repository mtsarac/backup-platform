using System.Text.Json;
using BackupPlatform.Domain.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.IntegrationTests.Quota;
using BackupPlatform.SecretProviders.Abstractions;
using BackupPlatform.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static BackupPlatform.IntegrationTests.Quota.UsageTestKit;

namespace BackupPlatform.IntegrationTests.Storage.Connections;

/// <summary>What the database itself refuses, whatever the application code does, and what the secret tables are allowed to hold.</summary>
public sealed class StorageConnectionInvariantTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private const string Plaintext = "correct-horse-battery-staple-4711";
    private readonly ConnectionWorld _world = new(fixture);

    // ---- composite foreign key ---------------------------------------------------------------------------------------------------

    private async Task<(StoreAgent Agent, Guid ReplicaId)> TenantWithACentralReplicaAsync()
    {
        var agent = await _world.Quota.AgentAsync();
        var job = await _world.Quota.JobAsync(agent);
        var (_, artifactId) = await _world.Quota.PublishCopiesAsync(agent, job, 1000, ReplicaSpec.Central);
        return (agent, (await _world.ReplicasAsync(artifactId)).Single().Id);
    }

    private Task ReassignAsync(Guid replicaId, Guid connectionId) => ConnectionWorld.ExecuteAsync(fixture.ConnectionString,
        "UPDATE backup_replicas SET \"StorageConnectionId\" = @c WHERE \"Id\" = @r", ("c", connectionId), ("r", replicaId));

    [Fact]
    public async Task AReplicaCannotBePointedAtAnotherTenantsConnectionOfTheSameProvider()
    {
        var (agent, replicaId) = await TenantWithACentralReplicaAsync();
        var stranger = await _world.TenantAsync();
        var (_, foreignCentral) = await _world.DefaultsAsync(stranger);

        var rejected = await Assert.ThrowsAsync<PostgresException>(() => ReassignAsync(replicaId, foreignCentral.Id));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, rejected.SqlState);
        var (_, own) = await _world.DefaultsAsync(agent.TenantId);
        Assert.Equal(own.Id, await StoredConnectionOfAsync(replicaId));
    }

    [Fact]
    public async Task AReplicaCannotBePointedAtAConnectionOfADifferentProviderTypeEvenInItsOwnTenant()
    {
        var (agent, replicaId) = await TenantWithACentralReplicaAsync();
        var (local, central) = await _world.DefaultsAsync(agent.TenantId);

        var rejected = await Assert.ThrowsAsync<PostgresException>(() => ReassignAsync(replicaId, local.Id));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, rejected.SqlState);
        Assert.Equal(central.Id, await StoredConnectionOfAsync(replicaId));
    }

    [Fact]
    public async Task AReplicaCanMoveToAnotherConnectionOfItsOwnTenantAndProvider()
    {
        var (agent, replicaId) = await TenantWithACentralReplicaAsync();
        var second = await _world.AddAsync(agent.TenantId, "Second central", "central", StorageOwnership.PlatformManaged);

        await ReassignAsync(replicaId, second.Id);

        Assert.Equal(second.Id, await StoredConnectionOfAsync(replicaId));
    }

    [Fact]
    public async Task TheContextRefusesToSaveAReplicaThatWasNeverAssignedToAConnection()
    {
        var tenantId = await _world.TenantAsync();
        var replica = new BackupReplica(Guid.NewGuid(), tenantId, Guid.NewGuid(), "central", "managed", "a/b/backup.bak", 10, new string('b', 64), "sha256", "Verified", "ReadBack",
            QuotaWorld.T0, QuotaWorld.T0);
        await using var context = fixture.CreatePlatformContext();
        context.BackupReplicas.Add(replica);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());

        Assert.Contains("storage connection", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Guid.Empty, replica.StorageConnectionId);
    }

    [Fact]
    public void AReplicaConstructedWithAnEmptyConnectionIdIsRejectedImmediately()
    {
        Assert.Throws<ArgumentException>(() => new BackupReplica(Guid.NewGuid(), TenantId.New(), Guid.NewGuid(), "central", "managed", "a/b/backup.bak", 10, new string('b', 64), "sha256",
            "Verified", "ReadBack", QuotaWorld.T0, QuotaWorld.T0, storageConnectionId: Guid.Empty));
    }

    // ---- check constraints and uniqueness ---------------------------------------------------------------------------------------

    private async Task InsertConnectionAsync(
        TenantId tenantId, string name, string provider, string ownership = "TenantManaged", string scope = "tenant", bool isDefault = false, string? normalized = null)
    {
        await ConnectionWorld.ExecuteAsync(fixture.ConnectionString, """
            INSERT INTO storage_connections ("Id", "TenantId", "Name", "NormalizedName", "ProviderType", "Ownership", "QuotaScope", "Enabled", "IsDefault",
                                             "ConfigurationJson", "SecretFieldNamesJson", "CreatedAt", "UpdatedAt")
            VALUES (gen_random_uuid(), @tenant, @name, @normalized, @provider, @ownership, @scope, true, @isDefault, '{}'::jsonb, '[]'::jsonb, now(), now())
            """, ("tenant", tenantId.Value), ("name", name), ("normalized", normalized ?? name.ToLowerInvariant()), ("provider", provider), ("ownership", ownership), ("scope", scope), ("isDefault", isDefault));
    }

    [Theory]
    [InlineData("Central", "tenant", "TenantManaged", "ck_storage_connections_provider")]
    [InlineData("my_provider", "tenant", "TenantManaged", "ck_storage_connections_provider")]
    [InlineData("-sftp", "tenant", "TenantManaged", "ck_storage_connections_provider")]
    [InlineData("sftp-", "tenant", "TenantManaged", "ck_storage_connections_provider")]
    [InlineData("a b", "tenant", "TenantManaged", "ck_storage_connections_provider")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456", "tenant", "TenantManaged", "ck_storage_connections_provider")]
    [InlineData("sftp", "Managed", "TenantManaged", "ck_storage_connections_scope")]
    [InlineData("sftp", "my_scope", "TenantManaged", "ck_storage_connections_scope")]
    [InlineData("sftp", "-scope", "TenantManaged", "ck_storage_connections_scope")]
    [InlineData("sftp", "", "TenantManaged", "ck_storage_connections_scope")]
    [InlineData("sftp", "scope_with_underscores", "TenantManaged", "ck_storage_connections_scope")]
    [InlineData("sftp", "tenant", "Nobody", "ck_storage_connections_ownership")]
    public async Task MalformedProviderScopeAndOwnershipLabelsAreRejectedByTheDatabase(string provider, string scope, string ownership, string constraint)
    {
        var tenantId = await _world.TenantAsync();

        var rejected = await Assert.ThrowsAsync<PostgresException>(() => InsertConnectionAsync(tenantId, "Probe", provider, ownership, scope));

        Assert.Equal((PostgresErrorCodes.CheckViolation, constraint), (rejected.SqlState, rejected.ConstraintName));
        Assert.Empty(await _world.RowsAsync(tenantId));
    }

    [Fact]
    public async Task WellFormedLabelsAtTheirLimitsAreAccepted()
    {
        var tenantId = await _world.TenantAsync();

        await InsertConnectionAsync(tenantId, "Longest", "abcdefghijklmnopqrstuvwxyz012345", "PlatformManaged", new string('a', 32) + "-" + new string('b', 31));

        Assert.Single(await _world.RowsAsync(tenantId));
    }

    [Fact]
    public async Task ANormalizedNameThatIsNotTheLowercaseOfTheNameIsRejectedSoCaseInsensitiveUniquenessCannotBeBypassed()
    {
        var tenantId = await _world.TenantAsync();

        var rejected = await Assert.ThrowsAsync<PostgresException>(() => InsertConnectionAsync(tenantId, "Archive", "sftp", normalized: "something-else"));

        Assert.Equal("ck_storage_connections_name", rejected.ConstraintName);
    }

    [Fact]
    public async Task ATenantCannotHoldTwoSystemDefaultsOfOneProviderTypeOrTwoConnectionsWithTheSameNormalizedName()
    {
        var tenantId = await _world.TenantAsync();
        await InsertConnectionAsync(tenantId, "First", "sftp", isDefault: true);

        var secondDefault = await Assert.ThrowsAsync<PostgresException>(() => InsertConnectionAsync(tenantId, "Second", "sftp", isDefault: true));
        var sameName = await Assert.ThrowsAsync<PostgresException>(() => InsertConnectionAsync(tenantId, "FIRST", "googledrive", normalized: "first"));
        await InsertConnectionAsync(tenantId, "Second", "sftp", isDefault: false);

        Assert.Equal(("ux_storage_connections_default", PostgresErrorCodes.UniqueViolation), (secondDefault.ConstraintName, secondDefault.SqlState));
        Assert.Equal(("ux_storage_connections_name", PostgresErrorCodes.UniqueViolation), (sameName.ConstraintName, sameName.SqlState));
        Assert.Equal(2, (await _world.RowsAsync(tenantId)).Count);
    }

    [Fact]
    public async Task TheDomainRefusesMalformedLabelsBeforeTheyReachTheDatabase()
    {
        var tenantId = TenantId.New();

        Assert.Throws<ArgumentException>(() => new StorageConnection(Guid.NewGuid(), tenantId, "x", "Bad_Type", StorageOwnership.TenantManaged, null, null, false, QuotaWorld.T0));
        Assert.Throws<ArgumentException>(() => new StorageConnection(Guid.NewGuid(), tenantId, "x", "sftp", StorageOwnership.TenantManaged, "Bad Scope", null, false, QuotaWorld.T0));
        Assert.Throws<ArgumentException>(() => new StorageConnection(Guid.NewGuid(), tenantId, "   ", "sftp", StorageOwnership.TenantManaged, null, null, false, QuotaWorld.T0));
        Assert.Throws<ArgumentException>(() => new StorageConnection(Guid.NewGuid(), tenantId, new string('n', 101), "sftp", StorageOwnership.TenantManaged, null, null, false, QuotaWorld.T0));
    }

    // ---- the secret ciphertext table ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASecretIsStoredAsCiphertextOnlyAndResolvesBackToTheValue()
    {
        var store = _world.Secrets();
        var reference = new SecretReference($"sc-test-{Guid.NewGuid():N}");

        await store.SetAsync(reference, new SecretValue(Plaintext), default);

        var ciphertext = await ConnectionWorld.TextAsync(fixture.ConnectionString, "SELECT \"Ciphertext\" FROM platform_secrets WHERE \"Name\" = @n", ("n", reference.Name));
        var wholeRow = await ConnectionWorld.TextAsync(fixture.ConnectionString, "SELECT row_to_json(s)::text FROM platform_secrets s WHERE \"Name\" = @n", ("n", reference.Name));
        Assert.NotEmpty(ciphertext);
        Assert.DoesNotContain(Plaintext, wholeRow, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Plaintext)), wholeRow, StringComparison.Ordinal);
        Assert.True(await store.ExistsAsync(reference, default));
        Assert.Equal(Plaintext, (await store.ResolveAsync(reference, default)).Reveal());
    }

    [Fact]
    public async Task ReplacingADeletingAndResolvingAnUnknownSecretBehaveAsDocumented()
    {
        var store = _world.Secrets();
        var reference = new SecretReference($"sc-test-{Guid.NewGuid():N}");
        await store.SetAsync(reference, new SecretValue("first-value"), default);

        await store.SetAsync(reference, new SecretValue("second-value"), default);
        var replaced = (await store.ResolveAsync(reference, default)).Reveal();
        var deleted = await store.DeleteAsync(reference, default);
        var deletedAgain = await store.DeleteAsync(reference, default);

        Assert.Equal("second-value", replaced);
        Assert.True(deleted);
        Assert.False(deletedAgain);
        Assert.False(await store.ExistsAsync(reference, default));
        await Assert.ThrowsAsync<SecretUnavailableException>(async () => await store.ResolveAsync(reference, default));
        Assert.Equal(0, await _world.ScalarAsync("SELECT count(*) FROM platform_secrets WHERE \"Name\" = @n", ("n", reference.Name)));
    }

    [Fact]
    public async Task CiphertextCopiedOntoAnotherReferenceOrEncryptedUnderAnotherKeyRingDoesNotDecrypt()
    {
        var store = _world.Secrets();
        var source = new SecretReference($"sc-test-{Guid.NewGuid():N}");
        var copy = new SecretReference($"sc-test-{Guid.NewGuid():N}");
        await store.SetAsync(source, new SecretValue(Plaintext), default);
        var ciphertext = (await _world.Ciphertexts().GetAsync(source.Name, default))!;
        await _world.Ciphertexts().SetAsync(copy.Name, ciphertext, default);

        var otherKeys = _world.Secrets(new EphemeralDataProtectionProvider());

        await Assert.ThrowsAsync<SecretUnavailableException>(async () => await store.ResolveAsync(copy, default));
        await Assert.ThrowsAsync<SecretUnavailableException>(async () => await otherKeys.ResolveAsync(source, default));
        Assert.Equal(Plaintext, (await store.ResolveAsync(source, default)).Reveal());
    }

    [Fact]
    public async Task TheSecretTableRefusesAnEmptyOrOverlongName()
    {
        var empty = await Assert.ThrowsAsync<PostgresException>(() => ConnectionWorld.ExecuteAsync(fixture.ConnectionString,
            "INSERT INTO platform_secrets (\"Name\", \"Ciphertext\", \"CreatedAt\", \"UpdatedAt\") VALUES ('', 'x', now(), now())"));
        var overlong = await Assert.ThrowsAnyAsync<PostgresException>(() => ConnectionWorld.ExecuteAsync(fixture.ConnectionString,
            "INSERT INTO platform_secrets (\"Name\", \"Ciphertext\", \"CreatedAt\", \"UpdatedAt\") VALUES (@n, 'x', now(), now())", ("n", new string('n', 129))));

        Assert.Equal("ck_platform_secrets_name", empty.ConstraintName);
        Assert.NotNull(overlong.SqlState);
    }

    // ---- secrets through the service: rows, views and audit never hold the value -------------------------------------------------

    private static CreateStorageConnectionCommand Vault(string name, string? password = Plaintext, string host = "vault.example") =>
        new(VaultProvider.Type, name, null, null, new Dictionary<string, string> { [VaultProvider.Host] = host },
            password is null ? null : new Dictionary<string, string> { [VaultProvider.Password] = password });

    private async Task<string> EverythingStoredAboutAsync(TenantId tenantId)
    {
        var rows = await ConnectionWorld.TextAsync(fixture.ConnectionString,
            "SELECT coalesce(string_agg(row_to_json(c)::text, E'\\n'), '') FROM storage_connections c WHERE \"TenantId\" = @t", ("t", tenantId.Value));
        var audit = await ConnectionWorld.TextAsync(fixture.ConnectionString,
            "SELECT coalesce(string_agg(row_to_json(a)::text, E'\\n'), '') FROM audit_events a WHERE \"TenantId\" = @t", ("t", tenantId.Value));
        return rows + audit;
    }

    [Fact]
    public async Task ACredentialGivenAtCreationLivesOnlyAsCiphertextAndNeverInTheRowTheViewOrTheAuditTrail()
    {
        var tenantId = await _world.TenantAsync();
        var service = _world.Service();
        var actor = (await _world.PlatformActorAsync()).UserId;
        var scope = StorageScope.Tenant(tenantId, canManage: true);

        var created = await service.CreateAsync(scope, tenantId, Vault("Corporate vault"), actor, default);

        Assert.Equal(StorageConnectionOutcome.Changed, created.Outcome);
        var view = created.Value!;
        Assert.True(view.HasSecret);
        Assert.Equal([VaultProvider.Password], view.SecretFieldNames);
        Assert.NotNull(view.SecretUpdatedAt);
        Assert.DoesNotContain(Plaintext, JsonSerializer.Serialize(view), StringComparison.Ordinal);
        Assert.DoesNotContain(Plaintext, await EverythingStoredAboutAsync(tenantId), StringComparison.Ordinal);
        var row = await _world.RowAsync(view.Id);
        var resolved = JsonDocument.Parse((await _world.Secrets().ResolveAsync(new SecretReference(row.SecretReference!), default)).Reveal()).RootElement;
        Assert.Equal(Plaintext, resolved.GetProperty(VaultProvider.Password).GetString());
        var audit = Assert.Single(await _world.AuditOfTenantAsync(tenantId), entry => entry.Action == StorageConnectionAuditActions.Created);
        Assert.Equal([VaultProvider.Password], JsonDocument.Parse(audit.DetailsJson).RootElement.GetProperty("secretFields").EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    [Fact]
    public async Task RotatingASecretReplacesTheReferenceAndDeletesTheOldCiphertextAndClearingRemovesTheLast()
    {
        var tenantId = await _world.TenantAsync();
        var service = _world.Service();
        var actor = (await _world.PlatformActorAsync()).UserId;
        var scope = StorageScope.Tenant(tenantId, canManage: true);
        var id = (await service.CreateAsync(scope, tenantId, Vault("Rotating"), actor, default)).Value!.Id;
        var first = (await _world.RowAsync(id)).SecretReference!;

        var rotated = await service.SetSecretAsync(scope, id, new Dictionary<string, string> { [VaultProvider.Password] = "rotated-" + Plaintext }, actor, default);

        Assert.Equal(StorageConnectionOutcome.Changed, rotated.Outcome);
        var second = (await _world.RowAsync(id)).SecretReference!;
        Assert.NotEqual(first, second);
        Assert.False(await _world.Secrets().ExistsAsync(new SecretReference(first), default));
        Assert.True(await _world.Secrets().ExistsAsync(new SecretReference(second), default));
        Assert.DoesNotContain("rotated-" + Plaintext, await EverythingStoredAboutAsync(tenantId), StringComparison.Ordinal);
        var rotation = Assert.Single(await _world.AuditOfTenantAsync(tenantId), entry => entry.Action == StorageConnectionAuditActions.SecretRotated);
        Assert.True(JsonDocument.Parse(rotation.DetailsJson).RootElement.GetProperty("replacedExisting").GetBoolean());

        var cleared = await service.ClearSecretAsync(scope, id, actor, default);

        Assert.Equal(StorageConnectionOutcome.Changed, cleared.Outcome);
        Assert.False(cleared.Value!.HasSecret);
        Assert.Empty(cleared.Value.SecretFieldNames);
        Assert.False(await _world.Secrets().ExistsAsync(new SecretReference(second), default));
        Assert.Null((await _world.RowAsync(id)).SecretReference);
        Assert.Equal(StorageConnectionOutcome.Unchanged, (await service.ClearSecretAsync(scope, id, actor, default)).Outcome);
    }

    [Fact]
    public async Task ACreationThatFailsAfterTheSecretWasWrittenLeavesNoOrphanCiphertext()
    {
        var tenantId = await _world.TenantAsync();
        var service = _world.Service();
        var actor = (await _world.PlatformActorAsync()).UserId;
        var scope = StorageScope.Tenant(tenantId, canManage: true);
        await service.CreateAsync(scope, tenantId, Vault("Taken"), actor, default);
        var before = await _world.ScalarAsync("SELECT count(*) FROM platform_secrets");

        var clash = await service.CreateAsync(scope, tenantId, Vault("TAKEN", "another-" + Plaintext), actor, default);

        Assert.Equal(StorageConnectionOutcome.NameTaken, clash.Outcome);
        Assert.Equal(before, await _world.ScalarAsync("SELECT count(*) FROM platform_secrets"));
    }

    [Fact]
    public async Task DeletingAConnectionDeletesItsCiphertext()
    {
        var tenantId = await _world.TenantAsync();
        var service = _world.Service();
        var actor = (await _world.PlatformActorAsync()).UserId;
        var scope = StorageScope.Tenant(tenantId, canManage: true);
        var id = (await service.CreateAsync(scope, tenantId, Vault("Short lived"), actor, default)).Value!.Id;
        var reference = (await _world.RowAsync(id)).SecretReference!;

        var deleted = await service.DeleteAsync(scope, id, actor, default);

        Assert.Equal(StorageConnectionOutcome.Changed, deleted.Outcome);
        Assert.False(await _world.Secrets().ExistsAsync(new SecretReference(reference), default));
    }

    private async Task<Guid> StoredConnectionOfAsync(Guid replicaId)
    {
        await using var context = fixture.CreatePlatformContext();
        return await context.BackupReplicas.AsNoTracking().Where(row => row.Id == replicaId).Select(row => row.StorageConnectionId).SingleAsync();
    }
}
