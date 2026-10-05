using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Backup;
using BackupPlatform.BackupProviders.MSSQL.Connectivity;
using Microsoft.Data.SqlClient;

namespace BackupPlatform.IntegrationTests.MsSql;

/// <summary>
/// A real SQL Server. Configuration comes from environment variables and is mandatory: with no server configured the fixture
/// throws, so these tests fail loudly instead of passing or skipping silently (the same rule as the PostgreSQL fixture).
/// The administrative login needs sysadmin; the fixture creates uniquely named databases and logins and drops them afterwards.
/// The staging directory is mandatory too: it is the one directory the server writes backups to and the agent (this process) reads,
/// declared once as the server's path and once as the agent's path (<see cref="StagingArea.Mapped"/>), so the tests exercise the real
/// path mapping instead of a same-host shortcut.
/// </summary>
public sealed class MsSqlFixture : IAsyncLifetime
{
    private const string ConfigurationHint =
        "Set BACKUP_PLATFORM_TEST_MSSQL_HOST, BACKUP_PLATFORM_TEST_MSSQL_USER and BACKUP_PLATFORM_TEST_MSSQL_PASSWORD (a sysadmin SQL login) "
        + "before running MSSQL integration tests. Optional: BACKUP_PLATFORM_TEST_MSSQL_PORT or BACKUP_PLATFORM_TEST_MSSQL_INSTANCE (not both), "
        + "BACKUP_PLATFORM_TEST_MSSQL_TRUST_SERVER_CERTIFICATE=true for self-signed servers. Required for the backup tests: "
        + "BACKUP_PLATFORM_TEST_MSSQL_STAGING_SERVER_DIR (a directory the SQL Server service account can write, as the server sees it) and "
        + "BACKUP_PLATFORM_TEST_MSSQL_STAGING_AGENT_DIR (the same directory as this test process sees it; it must be able to read and delete the files SQL Server creates there). "
        + "Optional: BACKUP_PLATFORM_TEST_MSSQL_UNWRITABLE_SERVER_DIR (an existing directory the service account cannot write; defaults to /root or C:\\Windows\\System32) and, on Windows "
        + "with a test account that is a sysadmin, BACKUP_PLATFORM_TEST_MSSQL_WINDOWS_AUTH=true to run the Windows Authentication tests.";

    private const int BigDatabaseBatches = 24;
    private const int BigDatabaseRowsPerBatch = 5000;

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..10];
    private readonly List<string> _databases = [];
    private readonly List<string> _logins = [];
    private DatabaseConnectionSettings? _settings;
    private PasswordDatabaseCredential? _admin;
    private StagingArea? _staging;

    public DatabaseConnectionSettings Settings => _settings ?? throw NotInitialized();
    public PasswordDatabaseCredential Admin => _admin ?? throw NotInitialized();

    /// <summary>The real staging directory seen from the server and from this process.</summary>
    public StagingArea Staging => _staging ?? throw NotInitialized();

    public string Suffix => _suffix;

    /// <summary>The operating system the database server runs on (<c>linux</c> or <c>windows</c>), as the server itself reports it.</summary>
    public string HostPlatform { get; private set; } = string.Empty;

    /// <summary>
    /// An EXISTING directory the SQL Server service account cannot create anything in, written in the server's own path style (it must
    /// exist: a missing directory is reported as missing, not as unwritable). Defaults: the home of root on Linux, the Windows system
    /// folder on Windows (which an unprivileged service account cannot modify; do not run these tests against a SQL Server that runs as
    /// LocalSystem or an administrator). BACKUP_PLATFORM_TEST_MSSQL_UNWRITABLE_SERVER_DIR overrides it, for example with a directory whose
    /// ACL grants the service account nothing. It does not have to exist on the agent side.
    /// </summary>
    public string UnwritableServerDirectory =>
        Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_UNWRITABLE_SERVER_DIR") is { Length: > 0 } configured
            ? configured
            : HostPlatform == "windows" ? @"C:\Windows\System32" : "/root";

    /// <summary>ONLINE, FULL recovery, compatibility level 130.</summary>
    public string OnlineFull => $"bp_it_full_{_suffix}";
    public string OnlineSimple => $"bp_it_simple_{_suffix}";
    public string OnlineBulkLogged => $"bp_it_bulk_{_suffix}";
    public string Offline => $"bp_it_offline_{_suffix}";
    public string Emergency => $"bp_it_emergency_{_suffix}";
    public string SingleUser => $"bp_it_single_{_suffix}";

    /// <summary>FULL recovery with a small table of incompressible-enough rows; the database the backup tests copy.</summary>
    public string Source => $"bp_it_src_{_suffix}";

    /// <summary>SIMPLE recovery, a few hundred MB of random data: a backup that takes long enough to cancel or time out.</summary>
    public string Big => $"bp_it_big_{_suffix}";

    /// <summary>The least-privilege case: db_backupoperator and nothing else. It can BACKUP, but SQL Server refuses it RESTORE VERIFYONLY.</summary>
    public SqlLogin Operator { get; private set; } = null!;

    /// <summary>db_backupoperator plus the server-level CREATE ANY DATABASE that RESTORE VERIFYONLY needs: the minimum login for a platform backup.</summary>
    public SqlLogin OperatorWithVerify { get; private set; } = null!;
    public SqlLogin GrantedOnly { get; private set; } = null!;
    public SqlLogin ReaderOnly { get; private set; } = null!;
    public SqlLogin Denied { get; private set; } = null!;
    public SqlLogin NoUser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var host = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_HOST");
        var user = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_USER");
        var password = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_PASSWORD");
        var serverDirectory = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_STAGING_SERVER_DIR");
        var agentDirectory = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_STAGING_AGENT_DIR");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password)
            || string.IsNullOrWhiteSpace(serverDirectory) || string.IsNullOrWhiteSpace(agentDirectory))
        {
            throw new InvalidOperationException(ConfigurationHint);
        }

        var portText = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_PORT");
        var instance = Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_INSTANCE");
        var trust = string.Equals(Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_TRUST_SERVER_CERTIFICATE"), "true", StringComparison.OrdinalIgnoreCase);

        _settings = new DatabaseConnectionSettings(
            new DatabaseEndpoint(
                host,
                string.IsNullOrWhiteSpace(portText) ? null : int.Parse(portText, System.Globalization.CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(instance) ? null : instance),
            new DatabaseConnectionOptions(trustServerCertificate: trust));
        _admin = new PasswordDatabaseCredential(user, new DatabaseSecret(password));
        _staging = StagingArea.Mapped(new DatabaseServerPath(serverDirectory), new AgentPath(agentDirectory));

        try
        {
            HostPlatform = Convert.ToString((await QueryAsync("SELECT LOWER(host_platform) FROM sys.dm_os_host_info"))[0][0], System.Globalization.CultureInfo.InvariantCulture)!;
            await CreateDatabasesAsync();
            await CreateLoginsAsync();
            await CreateBackupSourcesAsync();
        }
        catch (Exception initializationFailure)
        {
            try
            {
                await DisposeAsync();
            }
            catch (AggregateException cleanupFailure)
            {
                throw new AggregateException("MSSQL fixture initialization failed and its cleanup did not complete.", initializationFailure, cleanupFailure);
            }

            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (_settings is null)
        {
            return;
        }

        var failures = new List<Exception>();
        foreach (var database in _databases)
        {
            var literal = SqlServerBackupCommandBuilder.QuoteStringLiteral(database);
            var identifier = SqlServerBackupCommandBuilder.QuoteIdentifier(database);
            // An OFFLINE or EMERGENCY database keeps its files when dropped, so it is brought back online first. Without this the
            // fixture leaked .mdf/.ldf files on the server (observed on SQL Server 2025). SINGLE_USER removes other sessions' locks.
            await TryExecuteAsync(
                $"IF DB_ID({literal}) IS NOT NULL BEGIN "
                + $"ALTER DATABASE {identifier} SET ONLINE; "
                + $"ALTER DATABASE {identifier} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; "
                + $"DROP DATABASE {identifier}; END",
                failures);
        }

        foreach (var login in _logins)
        {
            await TryExecuteAsync($"IF SUSER_ID(N'{login}') IS NOT NULL DROP LOGIN [{login}]", failures);
        }

        SweepStagingDirectory(failures);

        if (failures.Count > 0)
        {
            throw new AggregateException("MSSQL fixture cleanup left objects on the server; remove them manually (names start with bp_it_).", failures);
        }
    }

    public PasswordDatabaseCredential Credential(SqlLogin login) => new(login.Name, new DatabaseSecret(login.Password));

    /// <summary>A staged file name unique to this fixture run; <see cref="DisposeAsync"/> removes leftovers by the suffix.</summary>
    public string StagedFileName(string tag) => $"bp_it_{tag}_{_suffix}_{Guid.NewGuid().ToString("N")[..8]}.bak";

    /// <summary>Creates a database (name is quoted properly, so odd names work) that the fixture drops at the end; the operator login may back it up.</summary>
    public async Task CreateScratchDatabaseAsync(string name)
    {
        _databases.Add(name);
        var identifier = SqlServerBackupCommandBuilder.QuoteIdentifier(name);
        await ExecuteAsync($"CREATE DATABASE {identifier}");
        await GrantBackupOperatorsAsync(name);
    }

    /// <summary>Runs a statement as the administrative login, outside the provider, to arrange or independently verify server state.</summary>
    public async Task ExecuteAsync(string sql)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<object?[]>> QueryAsync(string sql)
    {
        await using var connection = await OpenAdminAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
            {
                row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>The msdb backup-set row of the most recent backup of <paramref name="database"/> taken to the named staged file (as seen by the server).</summary>
    public async Task<BackupSetRow?> FindBackupSetAsync(string database, string stagedFileName)
    {
        var rows = await QueryAsync(
            "SELECT TOP (1) s.is_copy_only, s.has_backup_checksums, s.backup_size, s.compressed_backup_size, s.name "
            + "FROM msdb.dbo.backupset AS s JOIN msdb.dbo.backupmediafamily AS m ON m.media_set_id = s.media_set_id "
            + $"WHERE s.database_name = {SqlServerBackupCommandBuilder.QuoteStringLiteral(database)} "
            + $"AND m.physical_device_name LIKE N'%' + {SqlServerBackupCommandBuilder.QuoteStringLiteral(stagedFileName)} ORDER BY s.backup_set_id DESC");
        return rows.Count == 0
            ? null
            : new BackupSetRow(
                (bool)rows[0][0]!, (bool)rows[0][1]!, Convert.ToInt64(rows[0][2], System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToInt64(rows[0][3], System.Globalization.CultureInfo.InvariantCulture), (string)rows[0][4]!);
    }

    /// <summary>Whether a BACKUP DATABASE request is currently running anywhere on the server (admin view of <c>sys.dm_exec_requests</c>).</summary>
    public async Task<int> CountRunningBackupRequestsAsync(string database)
    {
        var rows = await QueryAsync(
            "SELECT COUNT(*) FROM sys.dm_exec_requests AS r CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) AS t "
            + "WHERE r.session_id <> @@SPID AND r.command LIKE N'BACKUP%' "
            + $"AND t.text LIKE N'%' + REPLACE(REPLACE({SqlServerBackupCommandBuilder.QuoteStringLiteral(database)}, N'[', N'[[]'), N'%', N'[%]') + N'%'");
        return Convert.ToInt32(rows[0][0], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Lists the entries currently in the agent's staging directory whose names start with <paramref name="prefix"/>.</summary>
    public string[] StagingEntries(string prefix) =>
        Directory.GetFileSystemEntries(Staging.AgentDirectory.Value, prefix + "*").Select(Path.GetFileName).Cast<string>().ToArray();

    private async Task CreateDatabasesAsync()
    {
        await CreateDatabaseAsync(OnlineFull, "FULL");
        await CreateDatabaseAsync(OnlineSimple, "SIMPLE");
        await CreateDatabaseAsync(OnlineBulkLogged, "BULK_LOGGED");
        await CreateDatabaseAsync(Offline, "FULL");
        await CreateDatabaseAsync(Emergency, "FULL");
        await CreateDatabaseAsync(SingleUser, "FULL");

        await ExecuteAsync($"ALTER DATABASE [{Offline}] SET OFFLINE WITH ROLLBACK IMMEDIATE");
        await ExecuteAsync($"ALTER DATABASE [{Emergency}] SET EMERGENCY");
        await ExecuteAsync($"ALTER DATABASE [{SingleUser}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
    }

    private async Task CreateDatabaseAsync(string name, string recoveryModel)
    {
        _databases.Add(name);
        await ExecuteAsync($"CREATE DATABASE [{name}]");
        await ExecuteAsync($"ALTER DATABASE [{name}] SET RECOVERY {recoveryModel}");
        await ExecuteAsync($"ALTER DATABASE [{name}] SET COMPATIBILITY_LEVEL = 130");
    }

    private async Task CreateLoginsAsync()
    {
        Operator = await CreateLoginAsync("operator", "ALTER ROLE db_backupoperator ADD MEMBER [{0}]");
        OperatorWithVerify = await CreateLoginAsync("opverify", "ALTER ROLE db_backupoperator ADD MEMBER [{0}]");
        await ExecuteAsync($"GRANT CREATE ANY DATABASE TO [{OperatorWithVerify.Name}]");
        GrantedOnly = await CreateLoginAsync("granted", "GRANT BACKUP DATABASE TO [{0}]");
        ReaderOnly = await CreateLoginAsync("reader", "ALTER ROLE db_datareader ADD MEMBER [{0}]");
        Denied = await CreateLoginAsync("denied", "ALTER ROLE db_backupoperator ADD MEMBER [{0}]; DENY BACKUP DATABASE TO [{0}]");
        NoUser = await CreateLoginAsync("nouser", null);
        // They can verify, so that a refused backup is refused for the missing (or denied) BACKUP DATABASE permission and not earlier.
        await ExecuteAsync($"GRANT CREATE ANY DATABASE TO [{ReaderOnly.Name}]");
        await ExecuteAsync($"GRANT CREATE ANY DATABASE TO [{Denied.Name}]");
    }

    // The two databases the backup tests copy. The operator is a db_backupoperator in both: the least-privilege case.
    private async Task CreateBackupSourcesAsync()
    {
        await CreateDatabaseAsync(Source, "FULL");
        await ExecuteInAsync(Source, "CREATE TABLE dbo.Notes (Id int IDENTITY PRIMARY KEY, Body nvarchar(400) NOT NULL)");
        await ExecuteInAsync(Source, "INSERT dbo.Notes (Body) SELECT TOP (3000) REPLICATE(N'backup platform ', 20) + CONVERT(nvarchar(20), ABS(CHECKSUM(NEWID()))) FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b");
        await GrantBackupOperatorsAsync(Source);

        await CreateDatabaseAsync(Big, "SIMPLE");
        await ExecuteAsync($"ALTER DATABASE [{Big}] MODIFY FILE (NAME = N'{Big}', SIZE = {BigDatabaseBatches * 45}MB)");
        await ExecuteInAsync(Big, "CREATE TABLE dbo.Blob (Id int IDENTITY PRIMARY KEY, Payload varbinary(8000) NOT NULL)");
        for (var batch = 0; batch < BigDatabaseBatches; batch++)
        {
            await ExecuteInAsync(Big, $"INSERT dbo.Blob (Payload) SELECT TOP ({BigDatabaseRowsPerBatch}) CRYPT_GEN_RANDOM(8000) FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b");
        }

        await GrantBackupOperatorsAsync(Big);
    }

    private Task ExecuteInAsync(string database, string statement) =>
        ExecuteAsync($"EXEC {SqlServerBackupCommandBuilder.QuoteIdentifier(database)}.sys.sp_executesql {SqlServerBackupCommandBuilder.QuoteStringLiteral(statement)}");

    private async Task GrantBackupOperatorsAsync(string database)
    {
        foreach (var login in new[] { Operator, OperatorWithVerify })
        {
            await ExecuteInAsync(database, $"CREATE USER [{login.Name}] FOR LOGIN [{login.Name}]");
            await ExecuteInAsync(database, $"ALTER ROLE db_backupoperator ADD MEMBER [{login.Name}]");
        }
    }

    // The generated password mixes character classes and contains no quote, so it is safe inside the literal below.
    private async Task<SqlLogin> CreateLoginAsync(string role, string? databaseStatement)
    {
        var name = $"bp_it_{role}_{_suffix}";
        var password = $"Aa1!{Guid.NewGuid():N}";
        _logins.Add(name);
        await ExecuteAsync($"CREATE LOGIN [{name}] WITH PASSWORD = N'{password}', CHECK_POLICY = OFF");
        if (databaseStatement is not null)
        {
            var statement = string.Format(System.Globalization.CultureInfo.InvariantCulture, databaseStatement, name);
            await ExecuteAsync($"EXEC [{OnlineFull}].sys.sp_executesql N'CREATE USER [{name}] FOR LOGIN [{name}]'");
            await ExecuteAsync($"EXEC [{OnlineFull}].sys.sp_executesql N'{statement}'");
        }

        return new SqlLogin(name, password);
    }

    private async Task<SqlConnection> OpenAdminAsync()
    {
        var connection = new SqlConnection(SqlServerConnectionStringFactory.Create(Settings, Admin).ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task TryExecuteAsync(string sql, List<Exception> failures)
    {
        try
        {
            await ExecuteAsync(sql);
        }
        catch (SqlException exception)
        {
            // Cleanup continues so one stuck object does not leak the rest, but the failure is reported when the fixture is disposed.
            failures.Add(new InvalidOperationException($"MSSQL fixture cleanup statement failed: {sql}", exception));
        }
    }

    // Files the tests created carry the fixture suffix; anything else in the shared staging directory is not ours.
    private void SweepStagingDirectory(List<Exception> failures)
    {
        if (_staging is null || !Directory.Exists(_staging.AgentDirectory.Value))
        {
            return;
        }

        foreach (var path in Directory.GetFileSystemEntries(_staging.AgentDirectory.Value, $"bp_it_*{_suffix}*"))
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(new InvalidOperationException("MSSQL fixture could not remove a leftover staging entry.", exception));
            }
        }
    }

    private static InvalidOperationException NotInitialized() => new("The MSSQL fixture has not been initialized.");
}

public sealed record SqlLogin(string Name, string Password)
{
    public override string ToString() => Name;
}

public sealed record BackupSetRow(bool IsCopyOnly, bool HasChecksums, long BackupSize, long CompressedBackupSize, string Name);
