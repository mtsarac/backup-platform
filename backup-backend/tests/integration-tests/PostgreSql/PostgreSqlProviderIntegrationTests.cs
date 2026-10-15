using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.PostgreSql;
using BackupPlatform.BackupProviders.PostgreSql.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BackupPlatform.IntegrationTests.PostgreSql;

[Trait("Category", "PostgreSqlProvider")]
public sealed class PostgreSqlProviderIntegrationTests(PostgreSqlProviderFixture fixture) : IClassFixture<PostgreSqlProviderFixture>
{
    [Fact]
    public async Task DiscoveryBackupInspectionAndNewRestorePreserveLogicalDatabaseObjectsAndData()
    {
        var source = await fixture.DatabaseAsync(populated: true);
        var discovery = await fixture.Backup.DiscoverAsync(fixture.Settings, fixture.Credential, CancellationToken.None);
        Assert.Equal(DatabaseEngineId.PostgreSql, discovery.Instance.Engine);
        Assert.Contains(discovery.Databases, database => database.Name == source && !database.IsSystem && database.Availability == DatabaseAvailability.Online);
        Assert.Contains(discovery.Databases, database => database.Name == "postgres" && database.IsSystem);
        var file = await fixture.DumpAsync(source);
        var inspected = await fixture.InspectAsync(file);
        Assert.Equal(source, inspected.Header.DatabaseName);
        Assert.NotNull(inspected.Header.SourceServerVersion);
        Assert.NotNull(inspected.VerifyStartedAt);
        Assert.True(inspected.VerifyFinishedAt >= inspected.VerifyStartedAt);
        var destination = fixture.Name();
        var request = await fixture.RequestAsync(destination, file, inspection: inspected);
        var result = await fixture.Restore.ExecuteRestoreAsync(fixture.Settings, fixture.Credential, request, null, CancellationToken.None);
        AssertSuccess(result);
        Assert.Equal(await fixture.SnapshotAsync(source), await fixture.SnapshotAsync(destination));
        Assert.Equal(3, await fixture.ScalarAsync<int>(destination, "INSERT INTO business.customer(name) VALUES ('sequence continued') RETURNING id"));
        Assert.True(await fixture.ScalarAsync<bool>(destination, "SELECT EXISTS(SELECT 1 FROM aclexplode((SELECT relacl FROM pg_class WHERE oid='business.customer'::regclass)) WHERE grantee=0 AND privilege_type='SELECT')"));
        await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync(destination, "INSERT INTO business.orders(customer_id,amount,state) VALUES(999,1,'pending')"));
        await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync(destination, "INSERT INTO business.orders(customer_id,amount,state) VALUES(1,-1,'pending')"));
    }

    [Fact]
    public async Task ReplacementPublishesWholeDatabaseAtomicallyAndRetainsOldDataInsteadOfDroppingIt()
    {
        var source = await fixture.DatabaseAsync(populated: true);
        var destination = await fixture.DatabaseAsync();
        await fixture.ExecuteAsync(destination, "CREATE TABLE old_only(value text); INSERT INTO old_only VALUES ('original data must survive');");
        var previousOid = await fixture.ScalarAsync<uint>(null, $"SELECT oid FROM pg_database WHERE datname='{destination}'");
        var file = await fixture.DumpAsync(source);
        var request = await fixture.RequestAsync(destination, file, DatabaseRestoreMode.ReplaceExistingDatabase);
        var result = await fixture.Restore.ExecuteRestoreAsync(fixture.Settings, fixture.Credential, request, null, CancellationToken.None);
        AssertSuccess(result);
        Assert.Equal(await fixture.SnapshotAsync(source), await fixture.SnapshotAsync(destination));
        Assert.Null(await fixture.ScalarAsync<string>(destination, "SELECT to_regclass('public.old_only')::text"));
        Assert.NotEqual(previousOid, await fixture.ScalarAsync<uint>(null, $"SELECT oid FROM pg_database WHERE datname='{destination}'"));
        var old = Assert.Single(await fixture.RecoveryDatabasesAsync(destination));
        Assert.StartsWith("bp_old_", old);
        Assert.Equal(previousOid, await fixture.ScalarAsync<uint>(null, $"SELECT oid FROM pg_database WHERE datname='{old}'"));
        Assert.False(await fixture.ScalarAsync<bool>(null, $"SELECT datallowconn FROM pg_database WHERE datname='{old}'"));
        await fixture.ExecuteAsync(null, $"ALTER DATABASE {PostgreSqlProviderFixture.Q(old)} ALLOW_CONNECTIONS true");
        Assert.Equal("original data must survive", await fixture.ScalarAsync<string>(old, "SELECT value FROM old_only"));
        Assert.Contains(result.Warnings, warning => warning.Code == PostgreSqlWarningCodes.RestoredDataRetained);
        Assert.Equal(old, result.RetainedDatabaseName);
    }

    [Fact]
    public async Task NewRestoreCannotOverwriteEvenWhenAnEarlierPlanWasMadeBeforeTargetExisted()
    {
        var source = await fixture.DatabaseAsync(populated: true);
        var file = await fixture.DumpAsync(source);
        var destination = fixture.Name();
        var request = await fixture.RequestAsync(destination, file);
        await fixture.ExecuteAsync(null, $"CREATE DATABASE {PostgreSqlProviderFixture.Q(destination)}");
        await fixture.ExecuteAsync(destination, "CREATE TABLE marker(value text); INSERT INTO marker VALUES ('untouched')");
        var result = await fixture.Restore.ExecuteRestoreAsync(fixture.Settings, fixture.Credential, request, null, CancellationToken.None);
        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Equal(RestoreReasonCodes.TargetDatabaseExists, result.Failure!.ReasonCode);
        Assert.Equal(DatabaseRestoreTargetState.NotModified, result.TargetState);
        Assert.Equal("untouched", await fixture.ScalarAsync<string>(destination, "SELECT value FROM marker"));
        Assert.Empty(await fixture.RecoveryDatabasesAsync(destination));
    }

    [Fact]
    public async Task ReplacementRequiresExistingDatabaseAndNeverTouchesMaintenanceDatabases()
    {
        var absent = await fixture.Restore.PreCheckRestoreAsync(fixture.Settings, fixture.Credential,
            new RestorePreCheckRequest(fixture.Name(), DatabaseRestoreMode.ReplaceExistingDatabase, false, null), CancellationToken.None);
        Assert.Contains(absent.Items, item => item.ReasonCode == RestoreReasonCodes.TargetDatabaseMissing && item.Status == BackupPreCheckStatus.Failed);
        foreach (var name in new[] { "postgres", "template0", "template1", "bp_restore_recovery" })
        {
            var protectedResult = await fixture.Restore.PreCheckRestoreAsync(fixture.Settings, fixture.Credential,
                new RestorePreCheckRequest(name, DatabaseRestoreMode.ReplaceExistingDatabase, true, null), CancellationToken.None);
            Assert.Contains(protectedResult.Items, item => item.ReasonCode == RestoreReasonCodes.TargetProtected && item.Status == BackupPreCheckStatus.Failed);
        }
    }

    [Fact]
    public async Task ActiveTargetConnectionsRequirePermissionAndCanBeExplicitlyTerminatedAtPublication()
    {
        var source = await fixture.DatabaseAsync(populated: true);
        var target = await fixture.DatabaseAsync();
        var file = await fixture.DumpAsync(source);
        await using var held = await fixture.OpenAsync(target);
        var precheck = await fixture.Restore.PreCheckRestoreAsync(fixture.Settings, fixture.Credential,
            new RestorePreCheckRequest(target, DatabaseRestoreMode.ReplaceExistingDatabase, false, null), CancellationToken.None);
        Assert.Contains(precheck.Items, item => item.ReasonCode == RestoreReasonCodes.TargetInUse);
        var request = await fixture.RequestAsync(target, file, DatabaseRestoreMode.ReplaceExistingDatabase, terminate: true);
        var restored = await fixture.Restore.ExecuteRestoreAsync(fixture.Settings, fixture.Credential, request, null, CancellationToken.None);
        AssertSuccess(restored);
        Assert.Equal(await fixture.SnapshotAsync(source), await fixture.SnapshotAsync(target));
        Assert.Contains(restored.Warnings, warning => warning.Code == PostgreSqlWarningCodes.ConnectionsTerminated);
    }

    [Fact]
    public async Task FullVerificationRejectsTruncatedDataEvenWhenArchiveTocRemainsReadable()
    {
        var source = await fixture.DatabaseAsync(populated: true);
        await fixture.ExecuteAsync(source, "CREATE TABLE public.payload AS SELECT n, md5(n::text) || md5((n+1)::text) AS payload FROM generate_series(1,10000) n");
        var file = await fixture.DumpAsync(source);
        var damaged = Path.Combine(fixture.DirectoryPath, "truncated.dump");
        var bytes = await File.ReadAllBytesAsync(file.AgentPath.Value);
        await File.WriteAllBytesAsync(damaged, bytes[..(bytes.Length - 128)]);
        var tocOnly = await fixture.Restore.InspectBackupAsync(fixture.Settings, fixture.Credential,
            new RestoreInspectionRequest(new DatabaseServerPath(damaged), false, agentFile: new AgentPath(damaged)), CancellationToken.None);
        Assert.Null(tocOnly.Failure);
        Assert.False(tocOnly.Inspection!.VerifyOnlyPassed);
        var verified = await fixture.Restore.InspectBackupAsync(fixture.Settings, fixture.Credential,
            new RestoreInspectionRequest(new DatabaseServerPath(damaged), true, agentFile: new AgentPath(damaged)), CancellationToken.None);
        Assert.Null(verified.Inspection);
        Assert.NotNull(verified.Failure);
        Assert.Equal(RestoreFailureKind.BackupInvalid, verified.Failure.Kind);
        Assert.Contains(verified.Failure.ReasonCode, new[] { PostgreSqlReasons.ArchiveInvalid, PostgreSqlReasons.VerifyFailed });
    }

    [Fact]
    public async Task CustomMagicAndUnsupportedArchiveVersionAreRejectedByTheActualClient()
    {
        var notArchive = Path.Combine(fixture.DirectoryPath, "plain.sql");
        await File.WriteAllTextAsync(notArchive, "CREATE DATABASE never_execute;");
        var invalid = await fixture.Restore.InspectBackupAsync(fixture.Settings, fixture.Credential,
            new RestoreInspectionRequest(new DatabaseServerPath(notArchive), true, agentFile: new AgentPath(notArchive)), CancellationToken.None);
        Assert.Equal(PostgreSqlReasons.ArchiveNotPostgreSql, invalid.Failure!.ReasonCode);
        var file = await fixture.DumpAsync(await fixture.DatabaseAsync());
        var bytes = await File.ReadAllBytesAsync(file.AgentPath.Value);
        bytes[6] = 255; // A future custom-archive format, rejected by real pg_restore before SQL is read.
        var future = Path.Combine(fixture.DirectoryPath, "future.dump");
        await File.WriteAllBytesAsync(future, bytes);
        var unsupported = await fixture.Restore.InspectBackupAsync(fixture.Settings, fixture.Credential,
            new RestoreInspectionRequest(new DatabaseServerPath(future), true, agentFile: new AgentPath(future)), CancellationToken.None);
        Assert.Equal(PostgreSqlReasons.ArchiveVersionUnsupported, unsupported.Failure!.ReasonCode);
    }

    [Fact]
    public async Task RejectedCredentialsAndMissingToolsAreClassifiedWithoutExposingSecrets()
    {
        var bad = new PasswordDatabaseCredential(fixture.Credential.UserName, new DatabaseSecret("definitely-not-the-test-password"));
        var precheck = await fixture.Restore.PreCheckRestoreAsync(fixture.Settings, bad,
            new RestorePreCheckRequest(fixture.Name(), DatabaseRestoreMode.NewDatabase, false, null), CancellationToken.None);
        Assert.Contains(precheck.Items, item => item.ReasonCode == PostgreSqlReasons.AuthenticationFailed);
        Assert.DoesNotContain(precheck.Items, item => item.Message.Contains("definitely-not-the-test-password", StringComparison.Ordinal));
        var missing = new PostgreSqlRestoreProvider(NullLogger<PostgreSqlRestoreProvider>.Instance,
            new PostgreSqlProviderOptions(Path.Combine(fixture.DirectoryPath, "missing-tools")));
        var unavailable = await missing.PreCheckRestoreAsync(fixture.Settings, fixture.Credential,
            new RestorePreCheckRequest(fixture.Name(), DatabaseRestoreMode.NewDatabase, false, null), CancellationToken.None);
        Assert.Contains(unavailable.Items, item => item.ReasonCode == PostgreSqlReasons.ToolsMissing);
    }

    [Fact]
    public async Task ExistingStagedFilesRemainByteIdenticalEvenWhenBackupFails()
    {
        var database = await fixture.DatabaseAsync(populated: true);
        var path = Path.Combine(fixture.DirectoryPath, "existing.dump");
        var original = "existing user data must survive"u8.ToArray();
        await File.WriteAllBytesAsync(path, original);
        var result = await fixture.Backup.ExecuteFullBackupAsync(fixture.Settings, fixture.Credential,
            new FullBackupRequest(database, StagingArea.SameHost(fixture.DirectoryPath), "existing.dump"), null, CancellationToken.None);
        Assert.Equal(FullBackupStatus.Failed, result.Status);
        Assert.Equal(PostgreSqlReasons.StagedFileExists, result.Failure!.ReasonCode);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task MissingAclRoleFailsInScratchWithoutStrippingAclOrChangingExistingTarget()
    {
        var source = await fixture.DatabaseAsync(populated: true);
        var role = await fixture.RoleAsync();
        await fixture.ExecuteAsync(source, $"GRANT SELECT ON business.orders TO {PostgreSqlProviderFixture.Q(role)}");
        var file = await fixture.DumpAsync(source);
        await fixture.ExecuteAsync(source, $"DROP OWNED BY {PostgreSqlProviderFixture.Q(role)}");
        await fixture.ExecuteAsync(null, $"DROP ROLE {PostgreSqlProviderFixture.Q(role)}");
        var target = await fixture.DatabaseAsync();
        await fixture.ExecuteAsync(target, "CREATE TABLE marker(value text); INSERT INTO marker VALUES ('still here')");
        var request = await fixture.RequestAsync(target, file, DatabaseRestoreMode.ReplaceExistingDatabase);
        var result = await fixture.Restore.ExecuteRestoreAsync(fixture.Settings, fixture.Credential, request, null, CancellationToken.None);
        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Equal(PostgreSqlReasons.RoleMissing, result.Failure!.ReasonCode);
        Assert.Equal(DatabaseRestoreTargetState.NotModified, result.TargetState);
        Assert.Equal("still here", await fixture.ScalarAsync<string>(target, "SELECT value FROM marker"));
        Assert.Empty(await fixture.RecoveryDatabasesAsync(target));
    }

    [Fact]
    public async Task CancellationDuringRealPgRestoreJoinsClientAndRemovesOnlyThisAttemptsScratch()
    {
        var source = await fixture.DatabaseAsync(populated: true);
        var lockKey = Random.Shared.Next(1, int.MaxValue);
        await fixture.ExecuteAsync(source, $"""
            CREATE FUNCTION business.restore_gate(value integer) RETURNS integer LANGUAGE plpgsql IMMUTABLE AS $$
            BEGIN
                PERFORM pg_advisory_xact_lock({lockKey});
                RETURN value;
            END $$;
            CREATE INDEX customer_restore_gate ON business.customer(business.restore_gate(id));
            """);
        var file = await fixture.DumpAsync(source);
        var target = await fixture.DatabaseAsync();
        await fixture.ExecuteAsync(target, "CREATE TABLE marker(value text); INSERT INTO marker VALUES ('cancel preserved')");
        var request = await fixture.RequestAsync(target, file, DatabaseRestoreMode.ReplaceExistingDatabase);
        await using var observer = await fixture.OpenAsync();
        // Advisory locks are database-local; acquire inside scratch before the tool starts, not in the maintenance database.
        await using var barrier = new RestoreGate(fixture, target, lockKey);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var execution = fixture.Restore.ExecuteRestoreAsync(fixture.Settings, fixture.Credential, request, barrier, cancellation.Token);
        using var observe = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string? observedScratch = null;
        try
        {
            while (!execution.IsCompleted)
            {
                observe.Token.ThrowIfCancellationRequested();
                await using var command = new NpgsqlCommand("SELECT datname FROM pg_stat_activity WHERE datname=@database AND application_name='backup-platform-agent' AND wait_event='advisory' LIMIT 1", observer);
                command.Parameters.AddWithValue("database", barrier.Database ?? string.Empty);
                if (await command.ExecuteScalarAsync(observe.Token) is string name) { observedScratch = name; break; }
                await Task.Delay(20, observe.Token);
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            await execution;
        }
        var result = await execution;
        Assert.True(observedScratch is not null,
            $"The real pg_restore session did not reach the controlled index-build lock. Status={result.Status}; Reason={result.Failure?.ReasonCode}; Message={result.Failure?.Message}");
        Assert.Equal(barrier.Database, observedScratch);
        Assert.Equal(DatabaseRestoreStatus.Cancelled, result.Status);
        Assert.Equal(DatabaseRestoreTargetState.NotModified, result.TargetState);
        Assert.Equal("cancel preserved", await fixture.ScalarAsync<string>(target, "SELECT value FROM marker"));
        Assert.Empty(await fixture.RecoveryDatabasesAsync(target));
        Assert.False(await fixture.ScalarAsync<bool>(null,
            $"SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname='{observedScratch}')"));
        Assert.False(await fixture.ScalarAsync<bool>(null,
            $"SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname='{observedScratch}')"));
    }

    [Fact]
    public async Task RealOlderClientsRefuseNewerServerAndArchiveWithoutMutatingExistingData()
    {
        var directory = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_POSTGRES_OLD_TOOLS_DIRECTORY");
        Assert.False(string.IsNullOrWhiteSpace(directory), "Set BACKUP_PLATFORM_TEST_POSTGRES_OLD_TOOLS_DIRECTORY to actual PostgreSQL 16 clients for the PostgreSQL 18 test server.");
        var olderOptions = new PostgreSqlProviderOptions(directory);
        var olderBackup = new PostgreSqlBackupProvider(NullLogger<PostgreSqlBackupProvider>.Instance, olderOptions);
        var source = await fixture.DatabaseAsync(populated: true);
        var result = await olderBackup.ExecuteFullBackupAsync(fixture.Settings, fixture.Credential,
            new FullBackupRequest(source, StagingArea.SameHost(fixture.DirectoryPath), "older.dump"), null, CancellationToken.None);
        Assert.Equal(FullBackupStatus.Failed, result.Status);
        Assert.Equal(PostgreSqlReasons.ToolVersionTooOld, result.Failure!.ReasonCode);
        Assert.False(File.Exists(Path.Combine(fixture.DirectoryPath, "older.dump")));
        var file = await fixture.DumpAsync(source);
        var olderRestore = new PostgreSqlRestoreProvider(NullLogger<PostgreSqlRestoreProvider>.Instance, olderOptions);
        var inspection = await olderRestore.InspectBackupAsync(fixture.Settings, fixture.Credential,
            new RestoreInspectionRequest(file.ServerPath, true, agentFile: file.AgentPath), CancellationToken.None);
        Assert.Equal(PostgreSqlReasons.ArchiveVersionUnsupported, inspection.Failure!.ReasonCode);
        Assert.Equal(2L, await fixture.ScalarAsync<long>(source, "SELECT count(*) FROM business.customer"));
    }

    [Fact]
    public async Task QuotedTargetNamesAndUnrelatedCrashRecoveryDatabasesRemainSafe()
    {
        var source = await fixture.DatabaseAsync(populated: true);
        var file = await fixture.DumpAsync(source);
        var orphan = fixture.Name($"bp_restore_{Guid.NewGuid():N}");
        await fixture.ExecuteAsync(null, $"CREATE DATABASE {PostgreSqlProviderFixture.Q(orphan)} TEMPLATE template0");
        await fixture.ExecuteAsync(orphan, "CREATE TABLE marker(value text); INSERT INTO marker VALUES ('unknown crash data')");
        var target = fixture.Name($"bp pg ' \" \\ {Guid.NewGuid():N}");
        var request = await fixture.RequestAsync(target, file);
        var result = await fixture.Restore.ExecuteRestoreAsync(fixture.Settings, fixture.Credential, request, null, CancellationToken.None);
        AssertSuccess(result);
        Assert.Equal(await fixture.SnapshotAsync(source), await fixture.SnapshotAsync(target));
        Assert.Equal("unknown crash data", await fixture.ScalarAsync<string>(orphan, "SELECT value FROM marker"));
    }

    [Fact]
    public async Task MissingOwnerRoleFailsInScratchBeforeAnyTargetDatabaseIsPublished()
    {
        var source = await fixture.DatabaseAsync(populated: true);
        var role = await fixture.RoleAsync();
        await fixture.ExecuteAsync(source, $"ALTER TABLE business.customer OWNER TO {PostgreSqlProviderFixture.Q(role)}");
        var file = await fixture.DumpAsync(source);
        await fixture.ExecuteAsync(source, $"REASSIGN OWNED BY {PostgreSqlProviderFixture.Q(role)} TO {PostgreSqlProviderFixture.Q(fixture.Credential.UserName)}; DROP OWNED BY {PostgreSqlProviderFixture.Q(role)}");
        await fixture.ExecuteAsync(null, $"DROP ROLE {PostgreSqlProviderFixture.Q(role)}");
        var inspection = await fixture.InspectAsync(file);
        var target = fixture.Name();
        var request = await fixture.RequestAsync(target, file, inspection: inspection);
        var result = await fixture.Restore.ExecuteRestoreAsync(fixture.Settings, fixture.Credential, request, null, CancellationToken.None);
        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Equal(PostgreSqlReasons.RoleMissing, result.Failure!.ReasonCode);
        Assert.Equal(DatabaseRestoreTargetState.NotModified, result.TargetState);
        Assert.Empty(await fixture.RecoveryDatabasesAsync(target));
        Assert.False(await fixture.ScalarAsync<bool>(null, $"SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname='{target}')"));
    }

    [Fact]
    public async Task RestoreRefusesNonSuperuserRatherThanSilentlyDiscardingOwnership()
    {
        var role = await fixture.RoleAsync();
        await fixture.ExecuteAsync(null, $"ALTER ROLE {PostgreSqlProviderFixture.Q(role)} LOGIN CREATEDB PASSWORD 'isolated-provider-test-only'");
        var credential = new PasswordDatabaseCredential(role, new DatabaseSecret("isolated-provider-test-only"));
        var result = await fixture.Restore.PreCheckRestoreAsync(fixture.Settings, credential,
            new RestorePreCheckRequest(fixture.Name(), DatabaseRestoreMode.NewDatabase, false, null), CancellationToken.None);
        Assert.False(result.CanProceed);
        Assert.Contains(result.Items, item => item.ReasonCode == PostgreSqlReasons.RestoreRolePrivilege && item.Cause == BackupFailureCause.PermissionDenied);
    }

    [Fact]
    public async Task ArchivesWithReplicationSubscriptionsAreRefusedInsteadOfCreatingExternalSideEffects()
    {
        var source = await fixture.DatabaseAsync();
        const string subscription = "isolated_test_subscription";
        await fixture.ExecuteAsync(source,
            $"CREATE SUBSCRIPTION {subscription} CONNECTION 'host=127.0.0.1 port=1 dbname=unused user=unused' PUBLICATION unused WITH (connect=false, enabled=false, create_slot=false, slot_name=NONE)");
        try
        {
            var file = await fixture.NativeDumpAsync(source);
            var result = await fixture.Restore.InspectBackupAsync(fixture.Settings, fixture.Credential,
                new RestoreInspectionRequest(file.ServerPath, true, agentFile: file.AgentPath), CancellationToken.None);
            Assert.Null(result.Inspection);
            Assert.Equal(RestoreReasonCodes.BackupTypeUnsupported, result.Failure!.ReasonCode);
        }
        finally
        {
            await fixture.ExecuteAsync(source, $"DROP SUBSCRIPTION {subscription}");
        }
    }

    [Fact]
    public async Task FunctionBodyMetadataLookingSqlIsNeverExecutedAndActualDatabaseMetadataIsPreserved()
    {
        var source = await fixture.DatabaseAsync(populated: true);
        await fixture.ExecuteAsync(null, $"""
            ALTER DATABASE {PostgreSqlProviderFixture.Q(source)} SET application_name TO 'actual database metadata';
            COMMENT ON DATABASE {PostgreSqlProviderFixture.Q(source)} IS 'actual database comment';
            REVOKE TEMPORARY ON DATABASE {PostgreSqlProviderFixture.Q(source)} FROM PUBLIC;
            """);
        await fixture.ExecuteAsync(source, $"""
            CREATE FUNCTION business.metadata_trap() RETURNS void LANGUAGE plpgsql AS $body$
            BEGIN
            ALTER DATABASE {PostgreSqlProviderFixture.Q(source)} SET application_name TO 'function body must never execute';
            CREATE EXTENSION IF NOT EXISTS bp_nonexistent_body_extension;
            ALTER TABLE business.customer OWNER TO bp_nonexistent_body_owner;
            END
            $body$;
            """);
        var file = await fixture.DumpAsync(source);
        var inspection = await fixture.InspectAsync(file);
        Assert.DoesNotContain("bp_nonexistent_body_extension", inspection.EngineDetails!["postgres.extensions"]);
        Assert.DoesNotContain("function body must never execute", inspection.EngineDetails!["postgres.database-sql"]);
        var target = fixture.Name();
        var request = await fixture.RequestAsync(target, file, inspection: inspection);
        var result = await fixture.Restore.ExecuteRestoreAsync(fixture.Settings, fixture.Credential, request, null, CancellationToken.None);
        AssertSuccess(result);
        foreach (var database in new[] { source, target })
        {
            Assert.True(await fixture.ScalarAsync<bool>(database,
                "SELECT 'application_name=actual database metadata'=ANY(setconfig) FROM pg_db_role_setting WHERE setdatabase=(SELECT oid FROM pg_database WHERE datname=current_database()) AND setrole=0"));
            Assert.Equal("actual database comment", await fixture.ScalarAsync<string>(database,
                "SELECT shobj_description(oid,'pg_database') FROM pg_database WHERE datname=current_database()"));
            Assert.False(await fixture.ScalarAsync<bool>(database,
                "SELECT EXISTS(SELECT 1 FROM aclexplode((SELECT datacl FROM pg_database WHERE datname=current_database())) WHERE grantee=0 AND privilege_type='TEMPORARY')"));
        }
        Assert.Contains("function body must never execute", await fixture.ScalarAsync<string>(target,
            "SELECT pg_get_functiondef('business.metadata_trap()'::regprocedure)"));
        Assert.Equal(await fixture.SnapshotAsync(source), await fixture.SnapshotAsync(target));
    }

    private sealed class RestoreGate(PostgreSqlProviderFixture fixture, string target, long key) : IProgress<RestoreProgress>, IAsyncDisposable
    {
        private NpgsqlConnection? _held;
        public string? Database { get; private set; }
        public void Report(RestoreProgress value)
        {
            if (value.Stage != RestoreExecutionStage.Restoring) return;
            _held = fixture.HoldScratchAdvisoryLock(target, key);
            Database = _held.Database;
        }
        public ValueTask DisposeAsync() => _held?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private static void AssertSuccess(DatabaseRestoreResult result)
    {
        Assert.True(result.Status == DatabaseRestoreStatus.Succeeded, result.Failure?.ReasonCode + ": " + result.Failure?.Message);
        Assert.Equal(DatabaseRestoreTargetState.Online, result.TargetState);
        Assert.Empty(result.FileMappings);
    }
}
