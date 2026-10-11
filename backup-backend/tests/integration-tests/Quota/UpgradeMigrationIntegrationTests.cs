using BackupPlatform.Application.Backups;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Infrastructure.Backups;
using BackupPlatform.Infrastructure.ManagedService;
using BackupPlatform.Infrastructure.Persistence;
using BackupPlatform.ManagedService.Quota;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace BackupPlatform.IntegrationTests.Quota;

internal sealed record UpgradedTenant(TenantId TenantId, Guid AgentId, Guid JobId, Guid ArtifactId);

/// <summary>
/// A database brought to the previous release's schema (plans and subscriptions, no quota), filled with tenants that really backed up before
/// the upgrade, then migrated to the latest schema. The tenants' backup rows are produced by the real run lifecycle in a throw-away database
/// and copied row by row, so the old schema holds exactly what the old code would have written.
/// </summary>
public sealed class UpgradedQuotaDatabase : IAsyncLifetime
{
    public const string PreviousMigration = "20261007143451_ManagedServicePlansAndSubscriptions";
    public static readonly Guid FiveGbPlanId = Guid.Parse("a6a9a7d1-293a-5ec9-a30f-603428c86559");
    private static readonly DateTimeOffset SubscribedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] CopiedTables = ["agents", "backup_jobs", "backup_runs", "backup_run_attempts", "backup_artifacts", "backup_replicas"];

    private readonly string _database = $"backup_platform_upgrade_{Guid.NewGuid():N}";
    private PostgreSqlFixture? _source;
    private string? _administrative;

    public string ConnectionString { get; private set; } = string.Empty;

    internal UpgradedTenant FiveGbRead { get; private set; } = null!;
    internal UpgradedTenant LegacyRead { get; private set; } = null!;
    internal UpgradedTenant FiveGbAdmit { get; private set; } = null!;
    internal UpgradedTenant LegacyAdmit { get; private set; } = null!;
    internal TenantId DanglingPlanTenant { get; private set; } = null!;
    public Guid DanglingPlanId { get; } = Guid.NewGuid();
    public Guid ExplicitDefaultPlanId { get; } = Guid.NewGuid();

    public bool DeniedOutcomeRejectedBeforeUpgrade { get; private set; }
    public long UsageRowsRightAfterMigration { get; private set; }
    public long ReservationRowsRightAfterMigration { get; private set; }

    internal DbContextOptions<PlatformDbContext> Options() => new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(ConnectionString).Options;

    internal PostgreSqlStorageUsageService Usage() => new(Options(), new QuotaOptions(), NullLogger<PostgreSqlStorageUsageService>.Instance);

    internal PostgreSqlBackupRunStore RunStore() => new(
        Options(), new PostgreSqlBackupAdmissionPolicy(Options(), new QuotaOptions(), NullLogger<PostgreSqlBackupAdmissionPolicy>.Instance),
        BackupStoreHarness.Limits(), () => 0);

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_CONNECTION_STRING");
        Assert.False(string.IsNullOrWhiteSpace(configured), "Set BACKUP_PLATFORM_TEST_CONNECTION_STRING.");
        var builder = new NpgsqlConnectionStringBuilder(configured) { Pooling = false };
        _administrative = builder.ConnectionString;
        await using (var connection = new NpgsqlConnection(_administrative))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{_database}\"", connection);
            await command.ExecuteNonQueryAsync();
        }
        builder.Database = _database;
        ConnectionString = builder.ConnectionString;

        _source = new PostgreSqlFixture();
        await _source.InitializeAsync();
        var world = new QuotaWorld(_source);
        FiveGbRead = await SeedSourceAsync(world, 2 * QuotaWorld.Gb, ReplicaSpec.Central, ReplicaSpec.GoogleDriveLike);
        LegacyRead = await SeedSourceAsync(world, 3 * QuotaWorld.Gb, ReplicaSpec.Central, ReplicaSpec.SftpLike);
        FiveGbAdmit = await SeedSourceAsync(world, 2 * QuotaWorld.Gb, ReplicaSpec.Central, ReplicaSpec.GoogleDriveLike);
        LegacyAdmit = await SeedSourceAsync(world, 3 * QuotaWorld.Gb, ReplicaSpec.Central, ReplicaSpec.SftpLike);

        await using (var context = PlatformDbContext.CreateForPlatformOperations(Options()))
        {
            await context.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        }
        Assert.Equal(0, await ScalarAsync("SELECT count(*) FROM information_schema.tables WHERE table_name IN ('quota_policies', 'tenant_storage_usage', 'quota_reservations')"));

        foreach (var tenant in new[] { FiveGbRead, LegacyRead, FiveGbAdmit, LegacyAdmit })
        {
            await CopyTenantAsync(tenant.TenantId.Value);
        }
        var legacyPlan = (Guid)(await ScalarObjectAsync("SELECT \"Id\" FROM plans WHERE \"SystemKey\" = 'legacy_unlimited'"))!;
        await SubscribeAsync(FiveGbRead.TenantId.Value, FiveGbPlanId);
        await SubscribeAsync(FiveGbAdmit.TenantId.Value, FiveGbPlanId);
        await SubscribeAsync(LegacyRead.TenantId.Value, legacyPlan);
        await SubscribeAsync(LegacyAdmit.TenantId.Value, legacyPlan);

        // M1 stored a plan's policy reference as opaque text. One names a policy that never existed, one the policy the upgrade is going to seed.
        await InsertPlanAsync(DanglingPlanId, "Dangling policy plan", 3_000_000_000, "ghost-policy");
        await InsertPlanAsync(ExplicitDefaultPlanId, "Explicit default plan", 7_000_000_000, "default");
        DanglingPlanTenant = TenantId.New();
        await ExecuteAsync("INSERT INTO tenants (\"Id\", \"Name\") VALUES (@id, 'Tenant on a dangling plan')", ("id", DanglingPlanTenant.Value));
        await SubscribeAsync(DanglingPlanTenant.Value, DanglingPlanId);

        try
        {
            await InsertOccurrenceAsync(FiveGbRead, "AdmissionDenied", "quota_exceeded", SubscribedAt);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.CheckViolation)
        {
            DeniedOutcomeRejectedBeforeUpgrade = true;
        }

        await using (var context = PlatformDbContext.CreateForPlatformOperations(Options()))
        {
            await context.Database.MigrateAsync();
        }
        UsageRowsRightAfterMigration = await ScalarAsync("SELECT count(*) FROM tenant_storage_usage");
        ReservationRowsRightAfterMigration = await ScalarAsync("SELECT count(*) FROM quota_reservations");
    }

    public async Task DisposeAsync()
    {
        if (_source is not null)
        {
            await _source.DisposeAsync();
        }
        if (_administrative is null)
        {
            return;
        }
        await using var connection = new NpgsqlConnection(_administrative);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    internal Task InsertOccurrenceAsync(UpgradedTenant tenant, string outcome, string reason, DateTimeOffset scheduledAt) => ExecuteAsync(
        """
        INSERT INTO backup_schedule_occurrences ("Id", "TenantId", "JobId", "ScheduledAt", "DecidedAt", "Outcome", "Reason", "RunId")
        VALUES (@id, @tenant, @job, @at, @at, @outcome, @reason, NULL)
        """, ("id", Guid.NewGuid()), ("tenant", tenant.TenantId.Value), ("job", tenant.JobId), ("at", scheduledAt), ("outcome", outcome), ("reason", reason));

    public async Task<long> ScalarAsync(string sql) => Convert.ToInt64(await ScalarObjectAsync(sql, []));

    public async Task<object?> ScalarObjectAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return await command.ExecuteScalarAsync() is { } scalar and not DBNull ? scalar : null;
    }

    public async Task<int> ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(value is null
                ? new NpgsqlParameter(name, DBNull.Value) { NpgsqlDbType = NpgsqlDbType.Unknown }
                : new NpgsqlParameter { ParameterName = name, Value = value });
        }
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<UpgradedTenant> SeedSourceAsync(QuotaWorld world, long sizeBytes, params ReplicaSpec[] replicas)
    {
        var agent = await world.AgentAsync();
        var job = await world.JobAsync(agent);
        var (_, artifactId) = await world.PublishAsync(world.RunStore(), agent, job, sizeBytes, replicas, QuotaWorld.T0);
        return new UpgradedTenant(agent.TenantId, agent.AgentId.Value, job.Id, artifactId);
    }

    private Task SubscribeAsync(Guid tenantId, Guid planId) => ExecuteAsync(
        """
        INSERT INTO tenant_subscriptions ("Id", "TenantId", "PlanId", "Status", "StartsAt", "EndsAt", "CreatedAt", "UpdatedAt",
                                          "StorageLimitOverrideKind", "StorageLimitOverrideBytes", "AssignedByUserId")
        VALUES (@id, @tenant, @plan, 1, @at, NULL, @at, @at, 0, NULL, NULL)
        """, ("id", Guid.NewGuid()), ("tenant", tenantId), ("plan", planId), ("at", SubscribedAt));

    private Task InsertPlanAsync(Guid id, string name, long limitBytes, string? reference) => ExecuteAsync(
        """
        INSERT INTO plans ("Id", "Name", "NormalizedName", "Enabled", "IsSystem", "PolicyConfigurationVersion", "QuotaPolicyReference", "StorageLimitBytes", "SystemKey", "CreatedAt", "UpdatedAt")
        VALUES (@id, @name, @normalized, TRUE, FALSE, 1, @reference, @limit, NULL, @at, @at)
        """, ("id", id), ("name", name), ("normalized", name.ToLowerInvariant()), ("reference", reference), ("limit", limitBytes), ("at", SubscribedAt));

    private async Task CopyTenantAsync(Guid tenantId)
    {
        await using var from = new NpgsqlConnection(_source!.ConnectionString);
        await from.OpenAsync();
        await using var to = new NpgsqlConnection(ConnectionString);
        await to.OpenAsync();
        await CopyRowsAsync(from, to, "tenants", "Id", tenantId);
        foreach (var table in CopiedTables)
        {
            await CopyRowsAsync(from, to, table, "TenantId", tenantId);
        }
        // Runs of the previous release carry no storage connection in their configuration snapshot; the copy must not smuggle one in from the new schema.
        await using var strip = new NpgsqlCommand("UPDATE backup_runs SET \"Configuration\" = \"Configuration\" - 'StorageConnectionId' WHERE \"TenantId\" = @tenant", to);
        strip.Parameters.AddWithValue("tenant", tenantId);
        await strip.ExecuteNonQueryAsync();
    }

    /// <summary>Copies every column of the tenant's rows as stored (system columns such as xmin are not part of SELECT *).</summary>
    private static async Task CopyRowsAsync(NpgsqlConnection from, NpgsqlConnection to, string table, string tenantColumn, Guid tenantId)
    {
        await using var select = new NpgsqlCommand($"SELECT * FROM {table} WHERE \"{tenantColumn}\" = @tenant", from);
        select.Parameters.AddWithValue("tenant", tenantId);
        await using var reader = await select.ExecuteReaderAsync();
        // The source is at the latest schema, the destination at the previous release: copy what both have. A column added since (a replica's storage
        // connection) is what the upgrade under test has to fill in by itself.
        var destination = new HashSet<string>(StringComparer.Ordinal);
        await using (var describe = new NpgsqlCommand("SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = @table", to))
        {
            describe.Parameters.AddWithValue("table", table);
            await using var names = await describe.ExecuteReaderAsync();
            while (await names.ReadAsync())
            {
                destination.Add(names.GetString(0));
            }
        }
        var sources = Enumerable.Range(0, reader.FieldCount).Where(index => destination.Contains(reader.GetName(index))).ToArray();
        var columns = sources.Select(reader.GetName).ToArray();
        var sql = $"INSERT INTO {table} ({string.Join(", ", columns.Select(column => $"\"{column}\""))}) VALUES ({string.Join(", ", columns.Select((_, index) => $"@p{index}"))})";
        while (await reader.ReadAsync())
        {
            await using var insert = new NpgsqlCommand(sql, to);
            for (var position = 0; position < sources.Length; position++)
            {
                var index = sources[position];
                var name = $"p{position}";
                if (reader.IsDBNull(index))
                {
                    insert.Parameters.Add(new NpgsqlParameter(name, DBNull.Value) { NpgsqlDbType = NpgsqlDbType.Unknown });
                }
                else if (reader.GetDataTypeName(index) == "jsonb")
                {
                    insert.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = reader.GetString(index) });
                }
                else
                {
                    insert.Parameters.Add(new NpgsqlParameter { ParameterName = name, Value = reader.GetValue(index) });
                }
            }
            await insert.ExecuteNonQueryAsync();
        }
    }
}

/// <summary>Upgrading a database that already holds tenants and backups: the quota layer must appear without touching or blocking any of them.</summary>
public sealed class UpgradeMigrationIntegrationTests(UpgradedQuotaDatabase upgraded) : IClassFixture<UpgradedQuotaDatabase>
{
    private const long Gb = QuotaWorld.Gb;
    private static readonly DateTimeOffset Now = QuotaWorld.T0.AddHours(1);

    [Fact]
    public async Task TheDefaultPolicyIsSeededWithTheFirstCommercialBehaviour()
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(upgraded.Options());

        var policy = Assert.Single(await context.QuotaPolicies.AsNoTracking().ToListAsync());

        Assert.Equal(Guid.Parse("14e8fce9-11ed-47a9-aa04-b6ead7a8cd3d"), policy.Id.Value);
        Assert.Equal("default", policy.Key);
        Assert.Equal(1, policy.Version);
        Assert.Equal(QuotaAccountingMode.LogicalArtifactBytes, policy.AccountingMode);
        Assert.Equal("""[{"kind":"all"}]""", policy.IncludedStorageScopes.ToJson());
        Assert.Equal(QuotaEnforcementMode.BlockNewBackups, policy.EnforcementMode);
        Assert.Equal(80, policy.WarningThresholdPercent);
        Assert.Equal(90, policy.CriticalThresholdPercent);
        Assert.True(policy.Enabled);
        Assert.True(policy.IsDefault);
        Assert.Equal(1, await upgraded.ScalarAsync("SELECT \"AccountingMode\" FROM quota_policies WHERE \"IsDefault\""));
        Assert.Equal(1, await upgraded.ScalarAsync("SELECT \"EnforcementMode\" FROM quota_policies WHERE \"IsDefault\""));
    }

    [Fact]
    public void TheMigrationComputesNoUsageAndHoldsNoReservations()
    {
        // Backfilling every tenant would hold long locks on a big database: usage is computed per tenant, on first use.
        Assert.Equal(0, upgraded.UsageRowsRightAfterMigration);
        Assert.Equal(0, upgraded.ReservationRowsRightAfterMigration);
    }

    [Fact]
    public async Task APlansReferenceToAPolicyThatDoesNotExistIsClearedAndAValidOneSurvives()
    {
        Assert.Null(await upgraded.ScalarObjectAsync("SELECT \"QuotaPolicyReference\" FROM plans WHERE \"Id\" = @id", ("id", upgraded.DanglingPlanId)));
        Assert.Equal("default", await upgraded.ScalarObjectAsync("SELECT \"QuotaPolicyReference\" FROM plans WHERE \"Id\" = @id", ("id", upgraded.ExplicitDefaultPlanId)));
        Assert.Equal(0, await upgraded.ScalarAsync("SELECT count(*) FROM plans WHERE \"QuotaPolicyReference\" IS NOT NULL AND \"QuotaPolicyReference\" <> 'default'"));
        Assert.Equal(3_000_000_000, await upgraded.ScalarAsync($"SELECT \"StorageLimitBytes\" FROM plans WHERE \"Id\" = '{upgraded.DanglingPlanId}'"));

        // The reference is a real foreign key from now on.
        var rejected = await Assert.ThrowsAsync<PostgresException>(() => upgraded.ExecuteAsync(
            "UPDATE plans SET \"QuotaPolicyReference\" = 'still-unknown' WHERE \"Id\" = @id", ("id", upgraded.DanglingPlanId)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, rejected.SqlState);
    }

    [Fact]
    public async Task TheOccurrenceOutcomeCheckAcceptsAnAdmissionDenialOnlyAfterTheUpgrade()
    {
        Assert.True(upgraded.DeniedOutcomeRejectedBeforeUpgrade);

        await upgraded.InsertOccurrenceAsync(upgraded.FiveGbRead, "AdmissionDenied", "quota_exceeded", Now);

        Assert.Equal(1, await upgraded.ScalarAsync(
            $"SELECT count(*) FROM backup_schedule_occurrences WHERE \"JobId\" = '{upgraded.FiveGbRead.JobId}' AND \"Outcome\" = 'AdmissionDenied' AND \"Reason\" = 'quota_exceeded'"));
        // The older outcomes remain valid and an unknown one is still refused.
        await upgraded.InsertOccurrenceAsync(upgraded.FiveGbRead, "JobDisabled", "job_disabled", Now.AddMinutes(1));
        var rejected = await Assert.ThrowsAsync<PostgresException>(() => upgraded.InsertOccurrenceAsync(upgraded.FiveGbRead, "Mystery", "x", Now.AddMinutes(2)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, rejected.SqlState);
    }

    [Fact]
    public async Task BackupsMadeBeforeTheUpgradeAreStillThere()
    {
        Assert.Equal(2, await upgraded.ScalarAsync($"SELECT count(*) FROM backup_replicas WHERE \"ArtifactId\" = '{upgraded.FiveGbRead.ArtifactId}' AND \"SizeBytes\" = {2 * Gb} AND \"Status\" = 'Available'"));
        Assert.Equal(3 * Gb, await upgraded.ScalarAsync($"SELECT \"SizeBytes\" FROM backup_artifacts WHERE \"Id\" = '{upgraded.LegacyRead.ArtifactId}'"));
        Assert.Equal(1, await upgraded.ScalarAsync($"SELECT count(*) FROM backup_runs WHERE \"JobId\" = '{upgraded.FiveGbRead.JobId}' AND \"Status\" = 4"));
    }

    [Fact]
    public async Task ATenantOnTheFiveGbPlanReadsItsPreUpgradeBackupAsNormalUsage()
    {
        var status = await upgraded.Usage().GetQuotaStatusAsync(upgraded.FiveGbRead.TenantId, Now, default);

        Assert.NotNull(status);
        Assert.Equal(2 * Gb, status.UsedBytes);
        Assert.Equal(0, status.ReservedBytes);
        Assert.Equal(5 * Gb, status.LimitBytes);
        Assert.Equal(3 * Gb, status.RemainingBytes);
        Assert.Equal(QuotaState.Normal, status.State);
        Assert.Equal("default", status.PolicyKey);
        Assert.True(status.NewBackupsAllowed);
        var stored = await StoredAsync(upgraded.FiveGbRead.TenantId);
        Assert.Equal(2 * Gb, stored.UsedBytes);
        Assert.Equal(QuotaPolicy.DefaultId, stored.PolicyId);
    }

    [Fact]
    public async Task ALegacyUnlimitedTenantReadsItsBytesAsUnlimited()
    {
        var status = await upgraded.Usage().GetQuotaStatusAsync(upgraded.LegacyRead.TenantId, Now, default);

        Assert.NotNull(status);
        Assert.Equal(3 * Gb, status.UsedBytes);
        Assert.True(status.IsUnlimited);
        Assert.Null(status.LimitBytes);
        Assert.Null(status.RemainingBytes);
        Assert.Equal(QuotaState.Unlimited, status.State);
        Assert.True(status.NewBackupsAllowed);
        Assert.Equal(3 * Gb, (await StoredAsync(upgraded.LegacyRead.TenantId)).UsedBytes);
    }

    [Fact]
    public async Task ATenantOnAPlanWithAClearedReferenceUsesTheDefaultPolicy()
    {
        var status = await upgraded.Usage().GetQuotaStatusAsync(upgraded.DanglingPlanTenant, Now, default);

        Assert.NotNull(status);
        Assert.Equal(QuotaPolicy.DefaultId, status.PolicyId);
        Assert.Equal(3 * Gb, status.LimitBytes);
        Assert.Equal(0, status.UsedBytes);
        Assert.Equal(QuotaState.Normal, status.State);
    }

    [Fact]
    public async Task ATenantOnTheFiveGbPlanReservesHistoryAfterItsAgentAdvertisesBoundedUploads()
    {
        var tenant = upgraded.FiveGbAdmit;
        await using (var context = PlatformDbContext.CreateForPlatformOperations(upgraded.Options()))
        {
            context.AgentCapabilities.Add(new BackupPlatform.Agents.Capabilities.AgentCapabilityRecord(new AgentId(tenant.AgentId), tenant.TenantId,
                BackupPlatform.Contracts.Backups.BackupTaskProtocol.QuotaBoundedUploadCapability,
                BackupPlatform.Domain.Capabilities.CapabilitySupport.Supported, null, "upgraded", Now));
            await context.SaveChangesAsync();
        }

        var started = await upgraded.RunStore().StartRunAsync(tenant.TenantId, tenant.JobId, $"after-upgrade-{Guid.NewGuid():N}", Now, default);

        Assert.Equal(BackupStartOutcome.Started, started.Outcome);
        Assert.Null(started.DenialCode);
        var stored = await StoredAsync(tenant.TenantId);
        Assert.Equal(2 * Gb, stored.UsedBytes);
        // The previous artifact (2 GB, found by the same job) is the estimate: 2 GB used + 2 GB reserved of 5 GB.
        Assert.Equal(2 * Gb, stored.ReservedBytes);
        Assert.Equal(1, await upgraded.ScalarAsync($"SELECT count(*) FROM quota_reservations WHERE \"RunId\" = '{started.Run!.Id}' AND \"ReleasedAt\" IS NULL"));
    }

    [Fact]
    public async Task ALegacyUnlimitedTenantKeepsBackingUpAfterTheUpgrade()
    {
        var tenant = upgraded.LegacyAdmit;

        var started = await upgraded.RunStore().StartRunAsync(tenant.TenantId, tenant.JobId, $"after-upgrade-{Guid.NewGuid():N}", Now, default);

        Assert.Equal(BackupStartOutcome.Started, started.Outcome);
        Assert.Equal(0, await upgraded.ScalarAsync($"SELECT count(*) FROM quota_reservations WHERE \"TenantId\" = '{tenant.TenantId.Value}'"));
        var status = await upgraded.Usage().GetQuotaStatusAsync(tenant.TenantId, Now, default);
        Assert.Equal(3 * Gb, status!.UsedBytes);
        Assert.Equal(QuotaState.Unlimited, status.State);
    }

    private async Task<TenantStorageUsage> StoredAsync(TenantId tenantId)
    {
        await using var context = PlatformDbContext.CreateForPlatformOperations(upgraded.Options());
        return await context.TenantStorageUsages.AsNoTracking().SingleAsync(row => row.TenantId == tenantId);
    }
}
