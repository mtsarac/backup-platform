using BackupPlatform.Agent.Core.Discovery;
using BackupPlatform.Agent.Platform.Windows.Discovery;
using BackupPlatform.Contracts.Discovery;
using Microsoft.Win32;

namespace BackupPlatform.Agent.Platform.Windows.Tests;

/// <summary>
/// The registry detectors against a throw-away hive under <c>HKCU</c> that mirrors the real layout, so no SQL Server or PostgreSQL
/// has to be installed. Parsing of the service command line is pure and runs everywhere.
/// </summary>
public sealed class WindowsDiscoveryDetectorTests : IDisposable
{
    private const string SqlServer = @"SOFTWARE\Microsoft\Microsoft SQL Server";
    private static readonly DiscoveryDetectionContext Context = new(IncludeDocker: true);

    private readonly string _hive = $@"Software\BackupPlatformDiscoveryTests\{Guid.NewGuid():N}";
    private readonly TestDirectory _scratch = new();

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(_hive, throwOnMissingSubKey: false);
        }
        _scratch.Dispose();
    }

    // Creates the hive on demand so a machine with nothing installed is an empty root, not a missing one.
    private RegistryKey OpenHive() => Registry.CurrentUser.CreateSubKey(_hive);

    private RegistryKey Create(string path)
    {
        using var root = Registry.CurrentUser.CreateSubKey(_hive);
        return root.CreateSubKey(path);
    }

    private void Set(string path, params (string Name, object Value)[] values)
    {
        using var key = Create(path);
        foreach (var (name, value) in values)
        {
            key.SetValue(name, value);
        }
    }

    private static string? ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    [WindowsFact]
    public async Task Default_and_named_sql_server_instances_are_found_with_ports_edition_and_patch_level()
    {
        Set($@"{SqlServer}\Instance Names\SQL", ("MSSQLSERVER", "MSSQL16.MSSQLSERVER"), ("SQLEXPRESS", "MSSQL16.SQLEXPRESS"));
        Set($@"{SqlServer}\MSSQL16.MSSQLSERVER\MSSQLServer\SuperSocketNetLib\Tcp\IPAll", ("TcpPort", ""), ("TcpDynamicPorts", ""));
        Set($@"{SqlServer}\MSSQL16.MSSQLSERVER\Setup", ("Edition", "Developer Edition"), ("PatchLevel", "16.0.4135.4"), ("Version", "16.0.1000.6"));
        Set($@"{SqlServer}\MSSQL16.SQLEXPRESS\MSSQLServer\SuperSocketNetLib\Tcp\IPAll", ("TcpPort", ""), ("TcpDynamicPorts", "49670,0"));
        Set($@"{SqlServer}\MSSQL16.SQLEXPRESS\Setup", ("Edition", "Express Edition"), ("Version", "16.0.1000.6"));

        var result = await new WindowsSqlServerDetector(OpenHive).DetectAsync(Context, default);

        Assert.Equal(DiscoverySourceStates.Scanned, result.Status);
        var byKey = result.Instances.ToDictionary(instance => instance.Key);
        var defaultInstance = byKey["mssql|service|default"];
        Assert.Equal(1433, defaultInstance.Port);
        Assert.Null(defaultInstance.InstanceName);
        Assert.Equal("Developer Edition", defaultInstance.Edition);
        Assert.Equal("16.0.4135.4", defaultInstance.Version);
        Assert.Equal("Windows", defaultInstance.HostPlatform);
        var named = byKey["mssql|service|SQLEXPRESS"];
        Assert.Equal(49670, named.Port);
        Assert.Equal("SQLEXPRESS", named.InstanceName);
        Assert.Equal("16.0.1000.6", named.Version);
        Assert.Equal(DiscoverySources.Service, named.Source);
        Assert.Equal(2, byKey.Count);
    }

    [WindowsFact]
    public async Task A_configured_tcp_port_wins_over_the_dynamic_one_and_a_named_instance_without_any_port_has_none()
    {
        Set($@"{SqlServer}\Instance Names\SQL", ("A", "MSSQL16.A"), ("B", "MSSQL16.B"));
        Set($@"{SqlServer}\MSSQL16.A\MSSQLServer\SuperSocketNetLib\Tcp\IPAll", ("TcpPort", "1444"), ("TcpDynamicPorts", "49670"));

        var result = await new WindowsSqlServerDetector(OpenHive).DetectAsync(Context, default);

        Assert.Equal(1444, result.Instances.Single(instance => instance.InstanceName == "A").Port);
        Assert.Null(result.Instances.Single(instance => instance.InstanceName == "B").Port);
    }

    [WindowsFact]
    public async Task A_machine_without_sql_server_is_a_scanned_source_with_no_instances_and_a_folder_with_a_separator_is_ignored()
    {
        var none = await new WindowsSqlServerDetector(OpenHive).DetectAsync(Context, default);
        Set($@"{SqlServer}\Instance Names\SQL", ("EVIL", @"..\..\Other"));
        var evil = await new WindowsSqlServerDetector(OpenHive).DetectAsync(Context, default);

        Assert.Equal(DiscoverySourceStates.Scanned, none.Status);
        Assert.Empty(none.Instances);
        Assert.Empty(evil.Instances);
    }

    [WindowsFact]
    public async Task A_registry_root_that_cannot_be_opened_is_reported_unavailable_when_denied()
    {
        var result = await new WindowsSqlServerDetector(() => throw new UnauthorizedAccessException("denied")).DetectAsync(Context, default);

        Assert.Equal(DiscoverySourceStates.Unavailable, result.Status);
        Assert.Equal("registry_access_denied", result.ReasonCode);
    }

    [WindowsFact]
    public async Task Postgresql_installations_and_services_are_merged_and_the_port_comes_from_postgresql_conf_when_readable()
    {
        var data16 = _scratch.Combine("data16");
        var data15 = _scratch.Combine("data15");
        Directory.CreateDirectory(data16);
        Directory.CreateDirectory(data15);
        File.WriteAllText(Path.Combine(data16, "postgresql.conf"), "#port = 5432\nport = 5433 # custom\n");
        Set(@"SOFTWARE\PostgreSQL\Installations\postgresql-x64-16", ("Service ID", "postgresql-x64-16"), ("Version", "16.2"), ("Data Directory", data16));
        Set(@"SYSTEM\CurrentControlSet\Services\postgresql-x64-16", ("ImagePath", $"\"C:\\PG\\16\\bin\\pg_ctl.exe\" runservice -N \"postgresql-x64-16\" -D \"{data16}\" -w"));
        Set(@"SYSTEM\CurrentControlSet\Services\postgresql-x64-15", ("ImagePath", $"\"C:\\PG\\15\\bin\\pg_ctl.exe\" runservice -N \"postgresql-x64-15\" -D \"{data15}\" -w"));
        Set(@"SYSTEM\CurrentControlSet\Services\Spooler", ("ImagePath", @"C:\Windows\System32\spoolsv.exe"));

        var result = await new WindowsPostgreSqlDetector(OpenHive, ReadFile).DetectAsync(Context, default);

        Assert.Equal(DiscoverySourceStates.Scanned, result.Status);
        Assert.Equal(2, result.Instances.Count);
        var sixteen = result.Instances.Single(instance => instance.Key == "postgres|service|postgresql-x64-16");
        Assert.Equal(5433, sixteen.Port);
        Assert.Equal("16.2", sixteen.Version);
        Assert.Equal(DiscoveryEngines.PostgreSql, sixteen.Engine);
        Assert.Equal(5432, result.Instances.Single(instance => instance.Key == "postgres|service|postgresql-x64-15").Port);
    }

    [WindowsFact]
    public async Task A_machine_without_postgresql_is_a_scanned_source_with_no_instances()
    {
        var result = await new WindowsPostgreSqlDetector(OpenHive, ReadFile).DetectAsync(Context, default);

        Assert.Equal(DiscoverySourceStates.Scanned, result.Status);
        Assert.Empty(result.Instances);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\PostgreSQL\\16\\bin\\pg_ctl.exe\" runservice -N \"postgresql-x64-16\" -D \"C:\\Program Files\\PostgreSQL\\16\\data\" -w", @"C:\Program Files\PostgreSQL\16\data")]
    [InlineData(@"C:\pg\bin\pg_ctl.exe runservice -N pg -D D:\data -w", @"D:\data")]
    [InlineData(@"C:\pg\bin\pg_ctl.exe runservice -N pg", null)]
    [InlineData("", null)]
    public void The_data_directory_is_the_dash_d_argument_of_the_service_command_line(string imagePath, string? expected)
    {
        Assert.Equal(expected, WindowsPostgreSqlDetector.ParseDataDirectory(imagePath));
    }
}
