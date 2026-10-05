using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL;
using BackupPlatform.BackupProviders.MSSQL.Capabilities;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using Microsoft.Data.SqlClient;

namespace BackupPlatform.IntegrationTests.MsSql;

/// <summary>
/// Checks the capability rules against the live server without ever writing a backup file. The harness (never the provider)
/// sends BACKUP statements whose destination directory does not exist. The engine validates statement options and the target
/// database first and only then opens the device, so the first error identifies how far the engine got:
/// <list type="bullet">
/// <item>Supported: the engine accepted the feature and failed only at the first later step (opening the device, or the "no base
/// backup yet" precondition of differential and log backups), identified by <c>acceptedError</c>.</item>
/// <item>Unsupported: the engine rejected the statement earlier, so <c>acceptedError</c> is absent.</item>
/// <item>Unknown: no claim is made, nothing is asserted.</item>
/// </list>
/// The error numbers were measured on SQL Server 2025 Developer (Linux); BACKUP to /dev/null no longer works there, so it cannot be the oracle.
/// </summary>
[Collection(MsSqlCollection.Name)]
public sealed class MsSqlCapabilityIntegrationTests(MsSqlFixture fixture)
{
    // 3201: cannot open backup device. 3035/4214: no current (full) database backup, which differential/log check right after the options.
    // 15151: server certificate not found, reached only after the ENCRYPTION option itself was accepted.
    private const int CannotOpenDevice = 3201;
    private const int NoBaseBackupForDifferential = 3035;
    private const int NoBaseBackupForLog = 4214;
    private const int CertificateNotFound = 15151;

    private readonly SqlServerBackupProvider _provider = MsSqlCollection.CreateProvider();

    [Fact]
    public async Task EveryCapabilityIsEvaluatedForTheLiveServer()
    {
        var discovery = await _provider.DiscoverAsync(fixture.Settings, fixture.Admin, CancellationToken.None);

        Assert.Equal(9, discovery.Capabilities.Count);
        Assert.Equal(DatabaseCapabilitySupport.Supported, discovery.Capabilities.SupportOf(SqlServerCapabilityIds.FullBackup));
    }

    [Fact]
    public async Task InstanceLevelClaimsHoldOnTheLiveServer()
    {
        var (instance, directory) = await InstanceAsync();
        var capabilities = SqlServerCapabilityEvaluator.EvaluateInstance(instance);
        var target = $"TO DISK = N'{directory}'";
        var database = $"[{fixture.OnlineFull}]";
        var certificate = $"bp_it_no_such_cert_{Guid.NewGuid():N}";

        var claims = new (DatabaseCapabilityId Id, string Statement, int AcceptedError)[]
        {
            (SqlServerCapabilityIds.FullBackup, $"BACKUP DATABASE {database} {target}", CannotOpenDevice),
            (SqlServerCapabilityIds.BackupChecksum, $"BACKUP DATABASE {database} {target} WITH CHECKSUM", CannotOpenDevice),
            (SqlServerCapabilityIds.CopyOnly, $"BACKUP DATABASE {database} {target} WITH COPY_ONLY", CannotOpenDevice),
            (SqlServerCapabilityIds.BackupCompression, $"BACKUP DATABASE {database} {target} WITH COMPRESSION", CannotOpenDevice),
            (SqlServerCapabilityIds.DifferentialBackup, $"BACKUP DATABASE {database} {target} WITH DIFFERENTIAL", NoBaseBackupForDifferential),
            (SqlServerCapabilityIds.LogBackup, $"BACKUP LOG {database} {target}", NoBaseBackupForLog),
            (SqlServerCapabilityIds.BackupEncryption,
                $"BACKUP DATABASE {database} {target} WITH ENCRYPTION (ALGORITHM = AES_256, SERVER CERTIFICATE = [{certificate}])", CertificateNotFound)
        };

        foreach (var (id, statement, acceptedError) in claims)
        {
            await AssertClaimAsync(capabilities, id, statement, acceptedError);
        }

        // The rules must make a claim for these on a Developer/Enterprise server; an all-Unknown result would pass the loop above vacuously.
        if (instance.Edition.Engine == SqlServerEngineEdition.Enterprise)
        {
            Assert.All(claims, claim => Assert.Equal(DatabaseCapabilitySupport.Supported, capabilities.SupportOf(claim.Id)));
        }
    }

    [Fact]
    public async Task DatabaseLevelRestrictionsHoldOnTheLiveServer()
    {
        var (instance, directory) = await InstanceAsync();
        var instanceCapabilities = SqlServerCapabilityEvaluator.EvaluateInstance(instance);
        var databases = (await _provider.DiscoverSqlServerAsync(fixture.Settings, fixture.Admin, CancellationToken.None)).Databases;
        var target = $"TO DISK = N'{directory}'";

        DatabaseCapabilitySet For(Func<SqlServerDatabaseInfo, bool> match) =>
            SqlServerCapabilityEvaluator.EvaluateDatabase(instanceCapabilities, databases.Single(match));

        var tempdb = For(database => database.SystemDatabase == SqlServerSystemDatabase.Tempdb);
        await AssertClaimAsync(tempdb, SqlServerCapabilityIds.FullBackup, $"BACKUP DATABASE [tempdb] {target}", CannotOpenDevice);
        Assert.Equal(DatabaseCapabilitySupport.Unsupported, tempdb.SupportOf(SqlServerCapabilityIds.FullBackup));

        var master = For(database => database.SystemDatabase == SqlServerSystemDatabase.Master);
        await AssertClaimAsync(master, SqlServerCapabilityIds.FullBackup, $"BACKUP DATABASE [master] {target}", CannotOpenDevice);
        await AssertClaimAsync(master, SqlServerCapabilityIds.DifferentialBackup, $"BACKUP DATABASE [master] {target} WITH DIFFERENTIAL", NoBaseBackupForDifferential);
        await AssertClaimAsync(master, SqlServerCapabilityIds.LogBackup, $"BACKUP LOG [master] {target}", NoBaseBackupForLog);
        Assert.Equal(DatabaseCapabilitySupport.Unsupported, master.SupportOf(SqlServerCapabilityIds.DifferentialBackup));
        Assert.Equal(DatabaseCapabilitySupport.Unsupported, master.SupportOf(SqlServerCapabilityIds.LogBackup));

        var simple = For(database => database.Name == fixture.OnlineSimple);
        await AssertClaimAsync(simple, SqlServerCapabilityIds.LogBackup, $"BACKUP LOG [{fixture.OnlineSimple}] {target}", NoBaseBackupForLog);
        Assert.Equal(DatabaseCapabilitySupport.Unsupported, simple.SupportOf(SqlServerCapabilityIds.LogBackup));

        var bulkLogged = For(database => database.Name == fixture.OnlineBulkLogged);
        await AssertClaimAsync(bulkLogged, SqlServerCapabilityIds.LogBackup, $"BACKUP LOG [{fixture.OnlineBulkLogged}] {target}", NoBaseBackupForLog);
        Assert.Equal(DatabaseCapabilitySupport.Supported, bulkLogged.SupportOf(SqlServerCapabilityIds.LogBackup));
    }

    private async Task<(SqlServerInstanceInfo Instance, string NonexistentBackupFile)> InstanceAsync()
    {
        var instance = (await _provider.DiscoverSqlServerAsync(fixture.Settings, fixture.Admin, CancellationToken.None)).Instance;
        var directory = $"bp-it-nonexistent-{Guid.NewGuid():N}";
        return (instance, instance.HostPlatform == SqlServerHostPlatform.Windows
            ? $@"C:\{directory}\probe.bak"
            : $"/{directory}/probe.bak");
    }

    private async Task AssertClaimAsync(DatabaseCapabilitySet capabilities, DatabaseCapabilityId id, string statement, int acceptedError)
    {
        var support = capabilities.SupportOf(id);
        if (support == DatabaseCapabilitySupport.Unknown)
        {
            return;
        }

        var failure = await Assert.ThrowsAsync<SqlException>(() => fixture.ExecuteAsync(statement));
        var numbers = failure.Errors.Cast<SqlError>().Select(error => error.Number).ToArray();

        if (support == DatabaseCapabilitySupport.Supported)
        {
            Assert.True(numbers.Contains(acceptedError), $"{id} is claimed Supported but the engine rejected it earlier: {string.Join(',', numbers)}.");
        }
        else
        {
            Assert.False(numbers.Contains(acceptedError), $"{id} is claimed Unsupported but the engine accepted the statement: {string.Join(',', numbers)}.");
        }
    }
}
