using BackupPlatform.BackupProviders.MSSQL;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupPlatform.IntegrationTests.MsSql;

[CollectionDefinition(Name)]
public sealed class MsSqlCollection : ICollectionFixture<MsSqlFixture>
{
    public const string Name = "mssql";

    public static SqlServerBackupProvider CreateProvider() => new(NullLogger<SqlServerBackupProvider>.Instance);
}
