using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.PreChecks;
using BackupPlatform.BackupProviders.MSSQL.Restore;

namespace BackupPlatform.IntegrationTests.MsSql;

/// <summary>Isolated restore verification on a real SQL Server: restore into a disposable <c>bp_verify_*</c> database, prove it usable, drop it.</summary>
[Collection(MsSqlCollection.Name)]
public sealed class MsSqlRestoreVerificationIntegrationTests(MsSqlFixture fixture) : IAsyncLifetime
{
    private readonly MsSqlRestoreHarness _harness = new(fixture);

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _harness.DisposeAsync();

    private Task<RestoreVerificationReport> VerifyAsync(string name) =>
        _harness.Provider.VerifyDatabaseAsync(fixture.Settings, fixture.Admin, new RestoreVerificationRequest(name), CancellationToken.None);

    private Task<DatabaseDropResult> DropAsync(string name) =>
        _harness.Provider.DropDatabaseAsync(fixture.Settings, fixture.Admin, new DatabaseDropRequest(name, DatabaseDropKind.VerificationDatabase), CancellationToken.None);

    private async Task<string> RestoredVerificationDatabaseAsync()
    {
        var name = _harness.NewVerificationName();
        var request = await _harness.BuildRequestAsync(await _harness.FullBackupAsync(), name, DatabaseRestoreMode.IsolatedVerification);
        var result = await _harness.ExecuteAsync(request);
        Assert.True(result.Status == DatabaseRestoreStatus.Succeeded, MsSqlRestoreHarness.Describe(result));
        return name;
    }

    [Fact]
    public async Task ARealBackupRestoresIntoAVerificationDatabaseVerifiesAndIsDropped()
    {
        var source = await _harness.FullBackupAsync();
        var name = _harness.NewVerificationName();
        var request = await _harness.BuildRequestAsync(source, name, DatabaseRestoreMode.IsolatedVerification);

        var result = await _harness.ExecuteAsync(request);

        Assert.Equal(DatabaseRestoreStatus.Succeeded, result.Status);
        Assert.Equal(DatabaseRestoreTargetState.Online, result.TargetState);
        Assert.False(await _harness.LastRestoreUsedReplaceAsync(name));
        Assert.Equal(MsSqlRestoreHarness.SourceRows, await _harness.CountNotesAsync(name));

        var report = await VerifyAsync(name);

        Assert.True(report.Passed, string.Join("; ", report.Checks.Select(check => $"{check.CheckId}={check.ReasonCode}")));
        Assert.Equal([RestoreVerificationCheck.DatabaseOnline, RestoreVerificationCheck.Integrity, RestoreVerificationCheck.DataReadable], report.Checks.Select(check => check.CheckId));
        Assert.True(report.UserTableCount >= 1);
        Assert.Equal(report.UserTableCount, report.ReadableTableCount);

        await using var held = await _harness.HoldSessionAsync(name);
        Assert.Equal(new DatabaseDropResult(DatabaseDropOutcome.Dropped), await DropAsync(name));
        Assert.False(await held.IsAliveAsync());
        Assert.Null(await _harness.StateAsync(name));
        Assert.Equal(new DatabaseDropResult(DatabaseDropOutcome.NotFound), await DropAsync(name));
        Assert.Equal(MsSqlRestoreHarness.SourceRows, await _harness.CountNotesAsync(fixture.Source));
    }

    [Fact]
    public async Task ADatabaseLeftInRestoringStateIsStillDropped()
    {
        var source = await _harness.FullBackupAsync();
        var name = _harness.NewVerificationName();
        var plan = (await _harness.BuildRequestAsync(source, name, DatabaseRestoreMode.IsolatedVerification)).Plan;
        // The provider never leaves a database restoring on purpose, so the state a cancelled restore leaves is made with the same statement and NORECOVERY.
        var statement = SqlServerRestoreCommandBuilder.BuildRestoreDatabase(name, source.ServerPath, plan.Mappings, replace: false, withChecksum: true, stats: false)
            .Replace(", RECOVERY", ", NORECOVERY", StringComparison.Ordinal);
        await fixture.ExecuteAsync(statement);
        Assert.Equal("RESTORING", (await _harness.StateAsync(name))!.Value.State);

        var report = await VerifyAsync(name);
        Assert.Equal(DatabaseDropOutcome.Dropped, (await DropAsync(name)).Outcome);

        var offline = Assert.Single(report.Checks);
        Assert.Equal((RestoreVerificationCheck.DatabaseOnline, false, SqlServerVerificationReasons.DatabaseNotOnline), (offline.CheckId, offline.Passed, offline.ReasonCode));
        Assert.Null(await _harness.StateAsync(name));
    }

    [Fact]
    public async Task APageDamagedAfterTheRestoreFailsTheIntegrityCheckAndStopsTheDependentCheck()
    {
        var name = await RestoredVerificationDatabaseAsync();
        var quoted = MsSqlRestoreHarness.Quote(name);
        var page = (await fixture.QueryAsync(
            $"SELECT TOP (1) allocated_page_file_id, allocated_page_page_id FROM sys.dm_db_database_page_allocations(DB_ID({MsSqlRestoreHarness.Literal(name)}), OBJECT_ID({MsSqlRestoreHarness.Literal($"{quoted}.dbo.Notes")}), NULL, NULL, 'DETAILED') "
            + "WHERE page_type_desc = N'DATA_PAGE' ORDER BY allocated_page_page_id"))[0];
        // Writes straight to the disk image of one data page (bypassing the buffer pool and the page checksum), then takes the database offline and back so nothing cached hides it.
        await fixture.ExecuteAsync($"ALTER DATABASE {quoted} SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
        await fixture.ExecuteAsync($"DBCC WRITEPAGE({MsSqlRestoreHarness.Literal(name)}, {page[0]}, {page[1]}, 200, 4, 0xDEADBEEF, 1)");
        await fixture.ExecuteAsync($"ALTER DATABASE {quoted} SET OFFLINE WITH ROLLBACK IMMEDIATE");
        await fixture.ExecuteAsync($"ALTER DATABASE {quoted} SET ONLINE");
        await fixture.ExecuteAsync($"ALTER DATABASE {quoted} SET MULTI_USER");

        var report = await VerifyAsync(name);

        Assert.False(report.Passed);
        Assert.Equal([RestoreVerificationCheck.DatabaseOnline, RestoreVerificationCheck.Integrity], report.Checks.Select(check => check.CheckId));
        Assert.Equal((true, false), (report.Checks[0].Passed, report.Checks[1].Passed));
        Assert.Equal(SqlServerVerificationReasons.IntegrityCheckFailed, report.Checks[1].ReasonCode);
        Assert.Null(report.UserTableCount);
        Assert.Equal(DatabaseDropOutcome.Dropped, (await DropAsync(name)).Outcome);
    }

    [Fact]
    public async Task VerifyingAMissingDatabaseIsAFailedFirstCheck()
    {
        var report = await VerifyAsync(RestoreDatabaseNames.NewVerificationName());

        var check = Assert.Single(report.Checks);
        Assert.Equal((RestoreVerificationCheck.DatabaseOnline, false, SqlServerVerificationReasons.DatabaseNotOnline), (check.CheckId, check.Passed, check.ReasonCode));
    }

    [Fact]
    public async Task ReservedNamesAreRefusedByEveryModeAndNothingIsCreated()
    {
        var source = await _harness.FullBackupAsync();
        var verification = RestoreDatabaseNames.NewVerificationName();
        var valid = await _harness.BuildRequestAsync(source, _harness.NewVerificationName(), DatabaseRestoreMode.IsolatedVerification);

        var newOnReserved = await _harness.Provider.PreCheckRestoreAsync(fixture.Settings, fixture.Admin,
            new RestorePreCheckRequest(verification, DatabaseRestoreMode.NewDatabase, false, source.SizeBytes), CancellationToken.None);
        var verificationOnOrdinary = await _harness.Provider.PreCheckRestoreAsync(fixture.Settings, fixture.Admin,
            new RestorePreCheckRequest(_harness.NewName("ordinary"), DatabaseRestoreMode.IsolatedVerification, false, source.SizeBytes), CancellationToken.None);
        var hijack = new DatabaseRestoreRequest(verification, source.ServerPath, DatabaseRestoreMode.NewDatabase, false, valid.Inspection, valid.Plan);
        var executed = await _harness.ExecuteAsync(hijack);

        foreach (var report in new[] { newOnReserved, verificationOnOrdinary })
            Assert.Contains(report.Items, item => item.CheckId == SqlServerRestoreCheckIds.TargetProtected && item.Status == BackupPreCheckStatus.Failed && item.ReasonCode == RestoreReasonCodes.TargetProtected);
        Assert.Equal(DatabaseRestoreStatus.Failed, executed.Status);
        Assert.Equal(RestoreReasonCodes.TargetProtected, executed.Failure!.ReasonCode);
        Assert.Equal(DatabaseRestoreTargetState.NotModified, executed.TargetState);
        Assert.Null(await _harness.StateAsync(verification));
    }

    [Fact]
    public async Task AVerificationRestoreNeverRestoresOverAnExistingDatabase()
    {
        var name = _harness.NewVerificationName();
        var request = await _harness.BuildRequestAsync(await _harness.FullBackupAsync(), name, DatabaseRestoreMode.IsolatedVerification);
        await fixture.ExecuteAsync($"CREATE DATABASE {MsSqlRestoreHarness.Quote(name)}");
        await _harness.ExecuteInAsync(name, "CREATE TABLE dbo.Marker (Id int)");

        var result = await _harness.ExecuteAsync(request);

        Assert.Equal(DatabaseRestoreStatus.Failed, result.Status);
        Assert.Equal(RestoreReasonCodes.TargetDatabaseExists, result.Failure!.ReasonCode);
        Assert.Equal(DatabaseRestoreTargetState.NotModified, result.TargetState);
        Assert.True(await _harness.TableExistsAsync(name, "Marker"));
        Assert.Null(await _harness.LastRestoreUsedReplaceAsync(name));
    }

    [Fact]
    public async Task OnlyTheVerificationKindCanBeDropped()
    {
        var result = await _harness.Provider.DropDatabaseAsync(fixture.Settings, fixture.Admin,
            new DatabaseDropRequest("bp_old_0123456789abcdef0123456789abcdef", DatabaseDropKind.RollbackCopy), CancellationToken.None);

        Assert.Equal(new DatabaseDropResult(DatabaseDropOutcome.Failed, SqlServerVerificationReasons.UnsupportedKind), result);
    }
}
