using System.Runtime.CompilerServices;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL;
using BackupPlatform.BackupProviders.MSSQL.Backup;
using BackupPlatform.BackupProviders.MSSQL.Connectivity;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.IntegrationTests.MsSql;

/// <summary>A verified backup file in the real staging directory, as the server and the agent (this process) see it.</summary>
public sealed record StagedRestoreSource(string Database, string FileName, DatabaseServerPath ServerPath, string AgentPath, long SizeBytes);

/// <summary>
/// What the restore integration tests share: real backups taken by the real backup provider (cached for the run, because taking them is the
/// slow part), uniquely named target databases that are dropped again whatever state a test leaves them in (a cancelled restore leaves RESTORING
/// databases, which the fixture's own cleanup cannot bring online), sessions held open against a target, and independent views of the server
/// (the catalog, msdb's restore history, the error log) so the tests verify what the server did instead of what the provider said.
/// </summary>
public sealed class MsSqlRestoreHarness(MsSqlFixture fixture) : IAsyncDisposable
{
    private static readonly ConditionalWeakTable<MsSqlFixture, BackupCache> Caches = new();

    private readonly List<string> _databases = [];
    private readonly List<string> _files = [];
    private int _counter;

    public MsSqlFixture Fixture => fixture;

    public SqlServerRestoreProvider Provider { get; } = new(NullLogger<SqlServerRestoreProvider>.Instance);

    /// <summary>The rows of <c>dbo.Notes</c> in the fixture's source database: what a restored copy must contain.</summary>
    public const int SourceRows = 3000;

    public static string Quote(string identifier) => SqlServerBackupCommandBuilder.QuoteIdentifier(identifier);

    public static string Literal(string value) => SqlServerBackupCommandBuilder.QuoteStringLiteral(value);

    // ---- backups -----------------------------------------------------------------------------------------------------------

    /// <summary>A full backup of the fixture's source database WITH checksums (the platform's usual shape for a verified backup). Do not modify or delete it.</summary>
    public Task<StagedRestoreSource> FullBackupAsync() => CachedAsync("full", fixture.Source, new FullBackupOptions(checksum: BackupOptionMode.Required));

    /// <summary>A full backup without checksums.</summary>
    public Task<StagedRestoreSource> PlainBackupAsync() => CachedAsync("plain", fixture.Source, new FullBackupOptions());

    /// <summary>A compressed full backup.</summary>
    public Task<StagedRestoreSource> CompressedBackupAsync() =>
        CachedAsync("compressed", fixture.Source, new FullBackupOptions(checksum: BackupOptionMode.Required, compression: BackupOptionMode.Required));

    /// <summary>A compressed backup of the fixture's big database (about a gigabyte of random data): a restore that takes long enough to cancel.</summary>
    public Task<StagedRestoreSource> BigBackupAsync() =>
        CachedAsync("big", fixture.Big, new FullBackupOptions(checksum: BackupOptionMode.Required, compression: BackupOptionMode.Required));

    private async Task<StagedRestoreSource> CachedAsync(string tag, string database, FullBackupOptions options)
    {
        var cache = Caches.GetValue(fixture, _ => new BackupCache());
        await cache.Gate.WaitAsync();
        try
        {
            if (cache.Sources.TryGetValue(tag, out var existing))
            {
                return existing;
            }

            var created = await TakeUncachedBackupAsync(database, tag, options);
            cache.Sources[tag] = created;
            return created;
        }
        finally
        {
            cache.Gate.Release();
        }
    }

    /// <summary>Takes a fresh backup with the real backup provider. The file is removed when the harness is disposed (cached backups are shared by the whole run and swept by the fixture).</summary>
    public async Task<StagedRestoreSource> TakeBackupAsync(string database, string tag, FullBackupOptions? options = null)
    {
        var source = await TakeUncachedBackupAsync(database, tag, options);
        _files.Add(source.AgentPath);
        return source;
    }

    private async Task<StagedRestoreSource> TakeUncachedBackupAsync(string database, string tag, FullBackupOptions? options)
    {
        var provider = new SqlServerBackupProvider(NullLogger<SqlServerBackupProvider>.Instance);
        var name = fixture.StagedFileName("rs" + tag);
        var result = await provider.ExecuteFullBackupAsync(
            fixture.Settings, fixture.Admin, new FullBackupRequest(database, fixture.Staging, name, options), progress: null, CancellationToken.None);
        Assert.True(
            result.Status == FullBackupStatus.Succeeded,
            $"The backup the restore tests need failed: {result.Failure?.ReasonCode}: {result.Failure?.Message}");
        return new StagedRestoreSource(database, name, result.StagedFile!.ServerPath, result.StagedFile.AgentPath.Value, result.StagedFile.SizeBytes);
    }

    /// <summary>A copy of a staged backup under a new name (a test damages the copy, never the cached original). Removed when the harness is disposed.</summary>
    public StagedRestoreSource CopyOf(StagedRestoreSource source, string tag)
    {
        var name = fixture.StagedFileName("rs" + tag);
        var target = Path.Combine(fixture.Staging.AgentDirectory.Value, name);
        File.Copy(source.AgentPath, target);
        _files.Add(target);
        return source with { FileName = name, AgentPath = target, ServerPath = fixture.Staging.ServerDirectory.Combine(name) };
    }

    /// <summary>A file in the staging directory with the given bytes.</summary>
    public StagedRestoreSource WriteFile(string tag, byte[] bytes)
    {
        var name = fixture.StagedFileName("rs" + tag);
        var target = Path.Combine(fixture.Staging.AgentDirectory.Value, name);
        File.WriteAllBytes(target, bytes);
        _files.Add(target);
        return new StagedRestoreSource(string.Empty, name, fixture.Staging.ServerDirectory.Combine(name), target, bytes.Length);
    }

    /// <summary>A path inside the staging directory as the server sees it, for a backup the test makes itself with plain T-SQL (the harness removes the file).</summary>
    public (DatabaseServerPath Server, string Agent) NewServerFile(string tag)
    {
        var name = fixture.StagedFileName("rs" + tag);
        var agent = Path.Combine(fixture.Staging.AgentDirectory.Value, name);
        _files.Add(agent);
        return (fixture.Staging.ServerDirectory.Combine(name), agent);
    }

    // ---- databases ---------------------------------------------------------------------------------------------------------

    /// <summary>A unique database name for this test run; the harness drops it on disposal.</summary>
    public string NewName(string tag)
    {
        var name = $"bp_it_rs_{tag}_{fixture.Suffix}_{Interlocked.Increment(ref _counter)}";
        _databases.Add(name);
        return name;
    }

    /// <summary>A fresh <c>bp_verify_*</c> name, as an isolated verification creates; the harness drops it on disposal.</summary>
    public string NewVerificationName()
    {
        var name = RestoreDatabaseNames.NewVerificationName();
        _databases.Add(name);
        return name;
    }

    /// <summary>Creates a database with <c>dbo.Notes</c> holding the given marker rows, so a test can tell it from a restored copy of the source.</summary>
    public async Task<string> CreateTargetAsync(string tag, int rows = 1)
    {
        var name = NewName(tag);
        await fixture.ExecuteAsync($"CREATE DATABASE {Quote(name)}");
        await ExecuteInAsync(name, "CREATE TABLE dbo.Notes (Id int IDENTITY PRIMARY KEY, Body nvarchar(400) NOT NULL); CREATE TABLE dbo.OnlyInTarget (Id int)");
        await ExecuteInAsync(name, $"INSERT dbo.Notes (Body) SELECT TOP ({rows}) N'target marker' FROM sys.all_columns");
        return name;
    }

    public Task ExecuteInAsync(string database, string statement) =>
        fixture.ExecuteAsync($"EXEC {Quote(database)}.sys.sp_executesql {Literal(statement)}");

    public async Task<int> CountNotesAsync(string database) =>
        Convert.ToInt32((await fixture.QueryAsync($"SELECT COUNT(*) FROM {Quote(database)}.dbo.Notes"))[0][0], System.Globalization.CultureInfo.InvariantCulture);

    public async Task<bool> TableExistsAsync(string database, string table) =>
        (await fixture.QueryAsync($"SELECT OBJECT_ID({Literal($"{Quote(database)}.dbo.{table}")})"))[0][0] is not null;

    /// <summary>The state and user access the catalog reports for a database, or null when it does not exist.</summary>
    public async Task<(string State, string Access, int DatabaseId)?> StateAsync(string database)
    {
        var rows = await fixture.QueryAsync(
            $"SELECT state_desc, user_access_desc, database_id FROM sys.databases WHERE name = {Literal(database)}");
        return rows.Count == 0
            ? null
            : ((string)rows[0][0]!, (string)rows[0][1]!, Convert.ToInt32(rows[0][2], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The physical files of a database as <c>sys.master_files</c> lists them (logical name, path, type).</summary>
    public async Task<IReadOnlyList<(string Logical, string Path, string Type)>> FilesAsync(string database) =>
        (await fixture.QueryAsync(
            $"SELECT name, physical_name, type_desc FROM sys.master_files WHERE database_id = DB_ID({Literal(database)}) ORDER BY file_id"))
        .Select(row => ((string)row[0]!, (string)row[1]!, (string)row[2]!)).ToArray();

    public async Task<Guid> FamilyGuidAsync(string database) =>
        (Guid)(await fixture.QueryAsync(
            $"SELECT rs.family_guid FROM sys.database_recovery_status AS rs WHERE rs.database_id = DB_ID({Literal(database)})"))[0][0]!;

    /// <summary>The <c>replace</c> flag msdb recorded for the most recent restore into <paramref name="database"/>; null when there was none.</summary>
    public async Task<bool?> LastRestoreUsedReplaceAsync(string database)
    {
        var rows = await fixture.QueryAsync(
            "SELECT TOP (1) [replace] FROM msdb.dbo.restorehistory "
            + $"WHERE destination_database_name = {Literal(database)} ORDER BY restore_history_id DESC");
        return rows.Count == 0 ? null : Convert.ToBoolean(rows[0][0], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The server's own record of "Setting database option ... for database" for one database (the error log), oldest first.</summary>
    public async Task<IReadOnlyList<string>> OptionChangesAsync(string database, string option)
    {
        var rows = await fixture.QueryAsync(
            $"EXEC sys.sp_readerrorlog 0, 1, {Literal($"Setting database option {option} to ON for database")}, {Literal(database)}");
        return rows.Select(row => Convert.ToString(row[2], System.Globalization.CultureInfo.InvariantCulture)!).ToArray();
    }

    /// <summary>Whether a RESTORE statement is running against <paramref name="database"/> right now (admin view of <c>sys.dm_exec_requests</c>).</summary>
    public async Task<int> CountRunningRestoresAsync(string database)
    {
        var rows = await fixture.QueryAsync(
            "SELECT COUNT(*) FROM sys.dm_exec_requests AS r CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS t "
            + "WHERE r.session_id <> @@SPID AND r.command LIKE N'RESTORE%' "
            + $"AND t.text LIKE N'%' + REPLACE(REPLACE({Literal(database)}, N'[', N'[[]'), N'%', N'[%]') + N'%'");
        return Convert.ToInt32(rows[0][0], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Opens a session whose current database is <paramref name="database"/> and keeps it open, like an application using it.</summary>
    public async Task<HeldSession> HoldSessionAsync(string database)
    {
        var builder = SqlServerConnectionStringFactory.Create(fixture.Settings, fixture.Admin);
        builder.InitialCatalog = database;
        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync();
        }

        return new HeldSession(connection);
    }

    /// <summary>Drops a database whatever state it is in (a RESTORING database cannot be altered, only dropped).</summary>
    public Task DropAsync(string database) =>
        fixture.ExecuteAsync(
            $"IF DB_ID({Literal(database)}) IS NOT NULL BEGIN "
            + $"BEGIN TRY ALTER DATABASE {Quote(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE END TRY BEGIN CATCH END CATCH; "
            + $"DROP DATABASE {Quote(database)}; END");

    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        foreach (var database in _databases)
        {
            try
            {
                await DropAsync(database);
            }
            catch (SqlException exception)
            {
                failures.Add(new InvalidOperationException($"Could not drop the restore test database '{database}'; remove it manually.", exception));
            }
        }

        foreach (var file in _files)
        {
            File.Delete(file);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Restore test cleanup left databases on the server.", failures);
        }
    }

    // ---- the four-step pipeline --------------------------------------------------------------------------------------------

    /// <summary>Runs inspection and plan the way the agent does and builds the execution request; fails the test if either step did not succeed.</summary>
    public async Task<DatabaseRestoreRequest> BuildRequestAsync(
        StagedRestoreSource source,
        string database,
        DatabaseRestoreMode mode,
        bool terminateActiveConnections = false,
        bool verify = false,
        TimeSpan? restoreTimeout = null)
    {
        var inspected = await Provider.InspectBackupAsync(
            fixture.Settings, fixture.Admin, new RestoreInspectionRequest(source.ServerPath, verify), CancellationToken.None);
        Assert.True(inspected.Failure is null, $"Inspection failed: {inspected.Failure?.ReasonCode}: {inspected.Failure?.Message}");
        var planned = await Provider.PlanRestoreAsync(
            fixture.Settings, fixture.Admin, new RestorePlanRequest(database, mode, terminateActiveConnections, inspected.Inspection!), CancellationToken.None);
        Assert.True(
            planned.Plan is not null,
            "Planning failed: " + string.Join("; ", planned.Report.Items.Where(item => item.Status == BackupPreCheckStatus.Failed).Select(item => $"{item.CheckId}={item.ReasonCode}")));
        return new DatabaseRestoreRequest(database, source.ServerPath, mode, terminateActiveConnections, inspected.Inspection!, planned.Plan, restoreTimeout);
    }

    public Task<DatabaseRestoreResult> ExecuteAsync(
        DatabaseRestoreRequest request,
        IProgress<RestoreProgress>? progress = null,
        DatabaseCredential? credential = null,
        CancellationToken cancellationToken = default) =>
        Provider.ExecuteRestoreAsync(fixture.Settings, credential ?? fixture.Admin, request, progress, cancellationToken);

    public static string Describe(DatabaseRestoreResult result) =>
        $"{result.Status} target={result.TargetState}/{result.DatabaseState} {result.Failure?.Kind}/{result.Failure?.Stage}/{result.Failure?.ReasonCode}: {result.Failure?.Message} "
        + $"warnings=[{string.Join(", ", result.Warnings.Select(warning => warning.Code))}]";


    private sealed class BackupCache
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<string, StagedRestoreSource> Sources { get; } = [];
    }
}

/// <summary>A connection kept open against a target database; <see cref="IsAliveAsync"/> says whether the server still serves it.</summary>
public sealed class HeldSession(SqlConnection connection) : IAsyncDisposable
{
    public async Task<bool> IsAliveAsync()
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync();
            return true;
        }
        catch (SqlException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync()
    {
        return connection.DisposeAsync();
    }
}
