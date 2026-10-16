using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.PostgreSql;
using Npgsql;

namespace BackupPlatform.IntegrationTests.PostgreSql;

/// <summary>Isolated restore verification against a real server: a CREATEDB-only login, the real clients, and every database and role uniquely named and dropped.</summary>
[Trait("Category", "PostgreSqlProvider")]
public sealed class PostgreSqlRestoreVerificationIntegrationTests(PostgreSqlProviderFixture fixture) : IClassFixture<PostgreSqlProviderFixture>
{
    private static string Q(string name) => PostgreSqlProviderFixture.Q(name);

    private async Task<DatabaseRestoreRequest> VerificationRequestAsync(string name, StagedBackupFile file, PasswordDatabaseCredential login, RestoreBackupInspection? inspection = null)
    {
        inspection ??= await fixture.InspectAsync(file);
        var plan = await fixture.Restore.PlanRestoreAsync(fixture.Settings, login, new RestorePlanRequest(name, DatabaseRestoreMode.IsolatedVerification, false, inspection), CancellationToken.None);
        Assert.True(plan.Report.CanProceed, string.Join("; ", plan.Report.Items.Where(item => item.Status == BackupPreCheckStatus.Failed).Select(item => item.ReasonCode + ": " + item.Message)));
        Assert.Contains(plan.Report.Items, item => item.CheckId == "postgres.verification-scope" && item.Status == BackupPreCheckStatus.Warning);
        return new DatabaseRestoreRequest(name, file.ServerPath, DatabaseRestoreMode.IsolatedVerification, false, inspection, plan.Plan!, agentFile: file.AgentPath);
    }

    private Task<DatabaseRestoreResult> RestoreAsync(DatabaseRestoreRequest request, PasswordDatabaseCredential login, IProgress<RestoreProgress>? progress = null) =>
        fixture.Restore.ExecuteRestoreAsync(fixture.Settings, login, request, progress, CancellationToken.None);

    private Task<DatabaseDropResult> DropAsync(string name, PasswordDatabaseCredential login, DatabaseDropKind kind = DatabaseDropKind.VerificationDatabase) =>
        fixture.Restore.DropDatabaseAsync(fixture.Settings, login, new DatabaseDropRequest(name, kind), CancellationToken.None);

    private async Task<bool> ExistsAsync(string name) => await fixture.ScalarAsync<bool>(null, $"SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = '{name}')");

    [Fact]
    public async Task CreateDbOnlyLoginRestoresVerifiesAndDropsADisposableDatabase()
    {
        var source = await fixture.DatabaseAsync(populated: true);
        var file = await fixture.DumpAsync(source);
        var login = await fixture.LoginRoleAsync(createDatabase: true);
        Assert.False(await fixture.ScalarAsync<bool>(null, $"SELECT rolsuper OR rolreplication OR rolcreaterole OR rolbypassrls FROM pg_roles WHERE rolname = '{login.UserName}'"));
        var name = fixture.Name(RestoreDatabaseNames.NewVerificationName());
        var request = await VerificationRequestAsync(name, file, login);
        var stages = new List<RestoreExecutionStage>();

        var result = await RestoreAsync(request, login, new InlineProgress(report => stages.Add(report.Stage)));

        Assert.True(result.Status == DatabaseRestoreStatus.Succeeded, result.Failure?.ReasonCode + ": " + result.Failure?.Message);
        Assert.Equal(DatabaseRestoreTargetState.Online, result.TargetState);
        Assert.Null(result.RetainedDatabaseName);
        Assert.Equal([RestoreExecutionStage.PreparingTarget, RestoreExecutionStage.Restoring, RestoreExecutionStage.Completed], stages);
        // Created directly under its final name: no scratch or rollback database was ever made for this target.
        Assert.Equal([name], await fixture.RecoveryDatabasesAsync(name));
        Assert.Contains("\"phase\":\"verification\"", await fixture.ScalarAsync<string>(null, $"SELECT shobj_description(oid,'pg_database') FROM pg_database WHERE datname='{name}'"));
        // Owners and ACLs are intentionally not preserved: every object belongs to the restoring login, and the PUBLIC grant of the source is gone.
        Assert.Equal(login.UserName, await fixture.ScalarAsync<string>(name, "SELECT string_agg(DISTINCT pg_get_userbyid(relowner), ',') FROM pg_class WHERE relnamespace = 'business'::regnamespace"));
        Assert.Null(await fixture.ScalarAsync<string>(name, "SELECT relacl::text FROM pg_class WHERE oid = 'business.customer'::regclass"));
        Assert.Equal(await fixture.ScalarAsync<string>(source, "SELECT string_agg(row_to_json(t)::text, ',' ORDER BY id) FROM business.orders t"),
            await fixture.ScalarAsync<string>(name, "SELECT string_agg(row_to_json(t)::text, ',' ORDER BY id) FROM business.orders t"));

        var report = await fixture.Restore.VerifyDatabaseAsync(fixture.Settings, login, new RestoreVerificationRequest(name), CancellationToken.None);

        Assert.True(report.Passed, string.Join("; ", report.Checks.Select(check => check.CheckId + "=" + check.ReasonCode)));
        Assert.Equal([RestoreVerificationCheck.DatabaseOnline, RestoreVerificationCheck.Integrity, RestoreVerificationCheck.DataReadable], report.Checks.Select(check => check.CheckId));
        Assert.Equal((3, 3), (report.UserTableCount, report.ReadableTableCount));
        Assert.Contains("amcheck", report.Checks[1].Message, StringComparison.Ordinal);

        // A session of another role cannot be ended by the login (FORCE follows pg_terminate_backend's rule): the drop fails with a stable reason and removes nothing.
        await using (var foreign = await fixture.OpenAsync(name))
            Assert.Equal(new DatabaseDropResult(DatabaseDropOutcome.Failed, PostgreSqlReasons.PermissionDenied), await DropAsync(name, login));
        Assert.True(await ExistsAsync(name));
        // The login's own stray session is ended by the drop.
        await using var stray = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
        {
            Host = fixture.Settings.Endpoint.Host, Port = fixture.Settings.Endpoint.Port!.Value, Database = name,
            Username = login.UserName, Password = login.Password.Reveal(), Pooling = false
        }.ConnectionString);
        await stray.OpenAsync();
        Assert.Equal(new DatabaseDropResult(DatabaseDropOutcome.Dropped), await DropAsync(name, login));
        await Assert.ThrowsAnyAsync<Exception>(async () => { await using var command = new NpgsqlCommand("SELECT 1", stray); await command.ExecuteScalarAsync(); });
        Assert.False(await ExistsAsync(name));
        Assert.Equal(new DatabaseDropResult(DatabaseDropOutcome.NotFound), await DropAsync(name, login));
        Assert.True(await ExistsAsync(source));
    }

    [Fact]
    public async Task ObjectsOwnedByAnotherRoleAndAclsRestoreWithoutThatRoleBecauseOwnersAndAclsAreNotKept()
    {
        var source = await fixture.DatabaseAsync(populated: true);
        var owner = await fixture.RoleAsync();
        await fixture.ExecuteAsync(source, $"ALTER TABLE business.customer OWNER TO {Q(owner)}; ALTER TABLE business.orders OWNER TO {Q(owner)}; GRANT SELECT ON business.orders TO {Q(owner)}");
        var file = await fixture.DumpAsync(source);
        await fixture.ExecuteAsync(source, $"REASSIGN OWNED BY {Q(owner)} TO {Q(fixture.Credential.UserName)}; DROP OWNED BY {Q(owner)}");
        await fixture.ExecuteAsync(null, $"DROP ROLE {Q(owner)}");
        var login = await fixture.LoginRoleAsync(createDatabase: true);
        var name = fixture.Name(RestoreDatabaseNames.NewVerificationName());
        var request = await VerificationRequestAsync(name, file, login);

        var result = await RestoreAsync(request, login);

        Assert.True(result.Status == DatabaseRestoreStatus.Succeeded, result.Failure?.ReasonCode + ": " + result.Failure?.Message);
        Assert.Equal(login.UserName, await fixture.ScalarAsync<string>(name, "SELECT pg_get_userbyid(relowner) FROM pg_class WHERE oid = 'business.orders'::regclass"));
        Assert.True((await fixture.Restore.VerifyDatabaseAsync(fixture.Settings, login, new RestoreVerificationRequest(name), CancellationToken.None)).Passed);
        Assert.Equal(DatabaseDropOutcome.Dropped, (await DropAsync(name, login)).Outcome);
    }

    [Fact]
    public async Task AnExtensionOnlyASuperuserMayCreateFailsWithAFixedReasonAndLeavesADroppableDatabase()
    {
        var source = await fixture.DatabaseAsync();
        await fixture.ExecuteAsync(source, "CREATE EXTENSION amcheck");
        var file = await fixture.DumpAsync(source);
        var login = await fixture.LoginRoleAsync(createDatabase: true);
        var name = fixture.Name(RestoreDatabaseNames.NewVerificationName());
        var request = await VerificationRequestAsync(name, file, login);

        var result = await RestoreAsync(request, login);

        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Equal(PostgreSqlReasons.ExtensionRequiresSuperuser, result.Failure!.ReasonCode);
        Assert.Equal(RestoreExecutionStage.Restoring, result.Failure.Stage);
        Assert.DoesNotContain("amcheck", result.Failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Command was", result.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(DatabaseRestoreTargetState.PartiallyRestored, result.TargetState);
        Assert.True(await ExistsAsync(name));
        Assert.Equal(new DatabaseDropResult(DatabaseDropOutcome.Dropped), await DropAsync(name, login));
        Assert.False(await ExistsAsync(name));
    }

    [Fact]
    public async Task ATruncatedArchiveFailsBeforeAnyDatabaseIsCreated()
    {
        var source = await fixture.DatabaseAsync(populated: true);
        await fixture.ExecuteAsync(source, "CREATE TABLE public.payload AS SELECT n, md5(n::text) || md5((n+1)::text) AS payload FROM generate_series(1,10000) n");
        var file = await fixture.DumpAsync(source);
        var inspection = await fixture.InspectAsync(file);
        var login = await fixture.LoginRoleAsync(createDatabase: true);
        var name = fixture.Name(RestoreDatabaseNames.NewVerificationName());
        var request = await VerificationRequestAsync(name, file, login, inspection);
        var damaged = Path.Combine(fixture.DirectoryPath, "verification-truncated.dump");
        var bytes = await File.ReadAllBytesAsync(file.AgentPath.Value);
        await File.WriteAllBytesAsync(damaged, bytes[..(bytes.Length - 128)]);
        request = new DatabaseRestoreRequest(name, new DatabaseServerPath(damaged), DatabaseRestoreMode.IsolatedVerification, false, inspection, request.Plan, agentFile: new AgentPath(damaged));

        var result = await RestoreAsync(request, login);

        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Contains(result.Failure!.ReasonCode, new[] { PostgreSqlReasons.ArchiveInvalid, PostgreSqlReasons.VerifyFailed });
        Assert.Equal(DatabaseRestoreTargetState.NotModified, result.TargetState);
        Assert.False(await ExistsAsync(name));
    }

    [Fact]
    public async Task VerificationNamesAndPrivilegesAreEnforcedByEveryRestoreMode()
    {
        var source = await fixture.DatabaseAsync(populated: true);
        var file = await fixture.DumpAsync(source);
        var inspection = await fixture.InspectAsync(file);
        var creator = await fixture.LoginRoleAsync(createDatabase: true);
        var plain = await fixture.LoginRoleAsync(createDatabase: false);
        var verification = RestoreDatabaseNames.NewVerificationName();

        async Task<BackupPreCheckItem> FirstFailureAsync(DatabaseCredential credential, string name, DatabaseRestoreMode mode)
        {
            var report = await fixture.Restore.PreCheckRestoreAsync(fixture.Settings, credential, new RestorePreCheckRequest(name, mode, false, null), CancellationToken.None);
            var plan = await fixture.Restore.PlanRestoreAsync(fixture.Settings, credential, new RestorePlanRequest(name, mode, false, inspection), CancellationToken.None);
            Assert.Null(plan.Plan);
            return Assert.Single(report.Items, item => item.Status == BackupPreCheckStatus.Failed);
        }

        foreach (var mode in new[] { DatabaseRestoreMode.NewDatabase, DatabaseRestoreMode.ReplaceExistingDatabase })
            Assert.Equal(RestoreReasonCodes.TargetProtected, (await FirstFailureAsync(fixture.Credential, verification, mode)).ReasonCode);
        Assert.Equal(RestoreReasonCodes.TargetProtected, (await FirstFailureAsync(fixture.Credential, "bp_old_" + Guid.NewGuid().ToString("N"), DatabaseRestoreMode.NewDatabase)).ReasonCode);
        Assert.Equal(RestoreReasonCodes.TargetProtected, (await FirstFailureAsync(fixture.Credential, fixture.Name(), DatabaseRestoreMode.IsolatedVerification)).ReasonCode);
        Assert.Equal(PostgreSqlReasons.RestoreRolePrivilege, (await FirstFailureAsync(plain, verification, DatabaseRestoreMode.IsolatedVerification)).ReasonCode);
        Assert.Equal(PostgreSqlReasons.RestoreRolePrivilege, (await FirstFailureAsync(creator, fixture.Name(), DatabaseRestoreMode.NewDatabase)).ReasonCode);

        // A normal restore that somehow got past planning still refuses a reserved name, and creates nothing.
        var request = await VerificationRequestAsync(fixture.Name(RestoreDatabaseNames.NewVerificationName()), file, creator, inspection);
        var reserved = fixture.Name(RestoreDatabaseNames.NewVerificationName());
        var hijack = new DatabaseRestoreRequest(reserved, file.ServerPath, DatabaseRestoreMode.NewDatabase, false, inspection, request.Plan, agentFile: file.AgentPath);
        var result = await fixture.Restore.ExecuteRestoreAsync(fixture.Settings, fixture.Credential, hijack, null, CancellationToken.None);
        Assert.Equal(RestoreReasonCodes.TargetProtected, result.Failure!.ReasonCode);
        Assert.Equal(DatabaseRestoreTargetState.NotModified, result.TargetState);
        Assert.False(await ExistsAsync(reserved));

        // An existing verification name is never restored over.
        var taken = fixture.Name(RestoreDatabaseNames.NewVerificationName());
        await fixture.ExecuteAsync(null, $"CREATE DATABASE {Q(taken)}");
        var existing = await fixture.Restore.PreCheckRestoreAsync(fixture.Settings, creator, new RestorePreCheckRequest(taken, DatabaseRestoreMode.IsolatedVerification, false, null), CancellationToken.None);
        Assert.Contains(existing.Items, item => item.ReasonCode == RestoreReasonCodes.TargetDatabaseExists && item.Status == BackupPreCheckStatus.Failed);
    }

    [Fact]
    public async Task AnInvalidIndexFailsTheIntegrityCheckAndLaterChecksAreNotListed()
    {
        var name = fixture.Name(RestoreDatabaseNames.NewVerificationName());
        await fixture.ExecuteAsync(null, $"CREATE DATABASE {Q(name)}");
        await fixture.ExecuteAsync(name, "CREATE TABLE t(v int); INSERT INTO t VALUES (1), (1)");
        await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsync(name, "CREATE UNIQUE INDEX CONCURRENTLY t_v ON t(v)"));

        var report = await fixture.Restore.VerifyDatabaseAsync(fixture.Settings, fixture.Credential, new RestoreVerificationRequest(name), CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Equal([RestoreVerificationCheck.DatabaseOnline, RestoreVerificationCheck.Integrity], report.Checks.Select(check => check.CheckId));
        Assert.Equal((true, false), (report.Checks[0].Passed, report.Checks[1].Passed));
        Assert.Equal(PostgreSqlVerificationReasons.IntegrityCheckFailed, report.Checks[1].ReasonCode);
        Assert.Null(report.UserTableCount);
    }

    [Fact]
    public async Task VerifyingAMissingDatabaseIsAFailedFirstCheckNotAnException()
    {
        var report = await fixture.Restore.VerifyDatabaseAsync(fixture.Settings, fixture.Credential,
            new RestoreVerificationRequest(RestoreDatabaseNames.NewVerificationName()), CancellationToken.None);

        var check = Assert.Single(report.Checks);
        Assert.Equal((RestoreVerificationCheck.DatabaseOnline, false, PostgreSqlReasons.DatabaseNotFound), (check.CheckId, check.Passed, check.ReasonCode));
    }

    [Fact]
    public async Task ARollbackCopyIsOnlyDroppedWithTheMarkerTheReplacingRestoreStamped()
    {
        var name = fixture.Name("bp_old_" + Guid.NewGuid().ToString("N"));
        await fixture.ExecuteAsync(null, $"CREATE DATABASE {Q(name)}");
        await fixture.ExecuteAsync(name, "CREATE TABLE previous_data(v int)");

        Assert.Equal(new DatabaseDropResult(DatabaseDropOutcome.Failed, PostgreSqlVerificationReasons.RollbackMarkerMissing), await DropAsync(name, fixture.Credential, DatabaseDropKind.RollbackCopy));
        await fixture.ExecuteAsync(null, $"COMMENT ON DATABASE {Q(name)} IS '{{\"provider\":\"someone-else\",\"phase\":\"previous\"}}'");
        Assert.Equal(PostgreSqlVerificationReasons.RollbackMarkerMissing, (await DropAsync(name, fixture.Credential, DatabaseDropKind.RollbackCopy)).ReasonCode);
        await fixture.ExecuteAsync(null, $"COMMENT ON DATABASE {Q(name)} IS '{{\"provider\":\"backup-platform\",\"phase\":\"scratch\"}}'");
        Assert.Equal(PostgreSqlVerificationReasons.RollbackMarkerMissing, (await DropAsync(name, fixture.Credential, DatabaseDropKind.RollbackCopy)).ReasonCode);
        Assert.True(await ExistsAsync(name));

        await fixture.ExecuteAsync(null, $"COMMENT ON DATABASE {Q(name)} IS '{{\"provider\":\"backup-platform\",\"phase\":\"previous\",\"target\":\"x\"}}'");
        Assert.Equal(new DatabaseDropResult(DatabaseDropOutcome.Dropped), await DropAsync(name, fixture.Credential, DatabaseDropKind.RollbackCopy));
        Assert.False(await ExistsAsync(name));
        Assert.Equal(new DatabaseDropResult(DatabaseDropOutcome.NotFound), await DropAsync(name, fixture.Credential, DatabaseDropKind.RollbackCopy));
        Assert.Throws<ArgumentException>(() => new DatabaseDropRequest(name, DatabaseDropKind.VerificationDatabase));
    }

    [Fact]
    public async Task AnUnprivilegedLoginCannotDropADatabaseItDoesNotOwnAndGetsAStableReason()
    {
        var name = fixture.Name(RestoreDatabaseNames.NewVerificationName());
        await fixture.ExecuteAsync(null, $"CREATE DATABASE {Q(name)}");
        var stranger = await fixture.LoginRoleAsync(createDatabase: true);

        var result = await DropAsync(name, stranger);

        Assert.Equal(new DatabaseDropResult(DatabaseDropOutcome.Failed, PostgreSqlReasons.PermissionDenied), result);
        Assert.True(await ExistsAsync(name));
    }

    private sealed class InlineProgress(Action<RestoreProgress> report) : IProgress<RestoreProgress>
    {
        public void Report(RestoreProgress value) => report(value);
    }
}
