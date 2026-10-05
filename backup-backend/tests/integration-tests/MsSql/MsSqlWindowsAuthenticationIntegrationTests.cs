using System.Runtime.Versioning;
using System.Security.Principal;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL;
using BackupPlatform.BackupProviders.MSSQL.Connectivity;
using Microsoft.Data.SqlClient;

namespace BackupPlatform.IntegrationTests.MsSql;

/// <summary>
/// Windows Authentication against a real SQL Server: the agent process connects as itself and the server must recognise that Windows
/// principal. Needs a Windows test process whose account is a sysadmin on the test instance (the same standing the SQL admin login of
/// the other tests has) and BACKUP_PLATFORM_TEST_MSSQL_WINDOWS_AUTH=true; everywhere else the tests are skipped with that reason.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(MsSqlCollection.Name)]
public sealed class MsSqlWindowsAuthenticationIntegrationTests(MsSqlFixture fixture) : IDisposable
{
    private readonly List<string> _files = [];
    private readonly SqlServerBackupProvider _provider = MsSqlCollection.CreateProvider();

    public void Dispose()
    {
        foreach (var file in _files)
        {
            new AgentStagingDirectory(fixture.Staging.AgentDirectory).TryDeleteFile(file);
        }
    }

    [WindowsAuthenticationFact]
    public async Task TheServerSeesTheWindowsPrincipalOfTheAgentProcessAndAuthenticatesItWithoutAPassword()
    {
        var principal = WindowsIdentity.GetCurrent().Name;
        var builder = SqlServerConnectionStringFactory.Create(fixture.Settings, IntegratedDatabaseCredential.Instance);
        Assert.True(string.IsNullOrEmpty(builder.UserID));
        Assert.True(string.IsNullOrEmpty(builder.Password));

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT SUSER_SNAME(), ORIGINAL_LOGIN(), (SELECT auth_scheme FROM sys.dm_exec_connections WHERE session_id = @@SPID), IS_SRVROLEMEMBER('sysadmin')";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(principal, reader.GetString(0), ignoreCase: true);
        Assert.Equal(principal, reader.GetString(1), ignoreCase: true);
        Assert.Contains(reader.GetString(2), new[] { "NTLM", "KERBEROS" });
        Assert.True(
            reader.GetInt32(3) == 1,
            "These tests need the Windows account that runs them to be a sysadmin on the test instance.");
    }

    [WindowsAuthenticationFact]
    public async Task DiscoveryWithWindowsAuthenticationSeesWhatTheSqlLoginSees()
    {
        var integrated = await _provider.DiscoverAsync(fixture.Settings, IntegratedDatabaseCredential.Instance, CancellationToken.None);
        var sql = await _provider.DiscoverAsync(fixture.Settings, fixture.Admin, CancellationToken.None);

        Assert.Equal(sql.Instance, integrated.Instance);
        Assert.Equal(Project(sql.Capabilities), Project(integrated.Capabilities));
        Assert.Equal(
            sql.Databases.OrderBy(d => d.Name, StringComparer.Ordinal).Select(d => (d.Name, d.IsSystem, d.Availability, Capabilities: Project(d.Capabilities))),
            integrated.Databases.OrderBy(d => d.Name, StringComparer.Ordinal).Select(d => (d.Name, d.IsSystem, d.Availability, Capabilities: Project(d.Capabilities))));
        Assert.Contains(integrated.Databases, d => d.Name == fixture.Source && d.Availability == DatabaseAvailability.Online);
    }

    [WindowsAuthenticationFact]
    public async Task APreCheckAndAFullBackupWorkForTheWindowsPrincipal()
    {
        var preCheck = await _provider.PreCheckFullBackupAsync(
            fixture.Settings,
            IntegratedDatabaseCredential.Instance,
            new FullBackupPreCheckRequest(fixture.Source, staging: fixture.Staging),
            CancellationToken.None);
        Assert.True(
            preCheck.CanProceed,
            string.Join("; ", preCheck.Items.Where(i => i.Status == BackupPreCheckStatus.Failed).Select(i => $"{i.CheckId}/{i.ReasonCode}")));

        var name = fixture.StagedFileName("winauth");
        _files.Add(name);
        var result = await _provider.ExecuteFullBackupAsync(
            fixture.Settings,
            IntegratedDatabaseCredential.Instance,
            new FullBackupRequest(fixture.Source, fixture.Staging, name),
            progress: null,
            CancellationToken.None);

        Assert.True(result.Status == FullBackupStatus.Succeeded, $"{result.Failure?.Kind}/{result.Failure?.ReasonCode}: {result.Failure?.Message}");
        Assert.Equal(BackupVerificationStatus.Passed, result.Verification.Status);
        Assert.True(new FileInfo(result.StagedFile!.AgentPath.Value).Length > 0);
    }

    private static IEnumerable<(string Id, DatabaseCapabilitySupport Support)> Project(DatabaseCapabilitySet set) =>
        set.OrderBy(c => c.Id.Value, StringComparer.Ordinal).Select(c => (c.Id.Value, c.Support));
}

/// <summary>Runs only on Windows and only when the environment declares that the account running the tests is a sysadmin on the test instance.</summary>
public sealed class WindowsAuthenticationFactAttribute : FactAttribute
{
    public WindowsAuthenticationFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows Authentication needs a Windows test process.";
        }
        else if (!string.Equals(Environment.GetEnvironmentVariable("BACKUP_PLATFORM_TEST_MSSQL_WINDOWS_AUTH"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Skip = "Set BACKUP_PLATFORM_TEST_MSSQL_WINDOWS_AUTH=true when the Windows account running the tests is a sysadmin on the test instance.";
        }
    }
}
