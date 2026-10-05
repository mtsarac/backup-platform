using BackupPlatform.BackupProviders.MSSQL.Discovery;

namespace BackupPlatform.UnitTests.MsSql;

internal static class MsSqlTestData
{
    public static SqlServerInstanceInfo Instance(
        string? version = "16.0.4165.4",
        string? edition = "Enterprise Edition (64-bit)",
        int? engineEdition = 3,
        SqlServerHostPlatform platform = SqlServerHostPlatform.Linux)
    {
        SqlServerVersion? parsed = SqlServerVersion.TryParse(version, out var value) ? value : null;
        return new SqlServerInstanceInfo(
            ServerName: "SRV",
            MachineName: "SRV",
            InstanceName: null,
            Version: parsed,
            RawProductVersion: version,
            ProductLevel: "RTM",
            ProductUpdateLevel: null,
            Edition: SqlServerEdition.FromServerProperties(edition, engineEdition),
            HostPlatform: platform,
            DefaultBackupDirectory: null);
    }

    public static SqlServerDatabaseInfo Database(
        string name = "Sales",
        int id = 5,
        SqlServerDatabaseState state = SqlServerDatabaseState.Online,
        SqlServerUserAccess userAccess = SqlServerUserAccess.MultiUser,
        SqlServerRecoveryModel recoveryModel = SqlServerRecoveryModel.Full,
        bool snapshot = false,
        bool? tde = false) =>
        new(
            DatabaseId: id,
            Name: name,
            State: state,
            StateCode: null,
            UserAccess: userAccess,
            RecoveryModel: recoveryModel,
            CompatibilityLevel: new SqlServerCompatibilityLevel(160),
            SystemDatabase: SqlServerMetadataMapper.MapSystemDatabase(id),
            IsReadOnly: false,
            IsTransparentDataEncrypted: tde,
            IsSnapshot: snapshot);
}
