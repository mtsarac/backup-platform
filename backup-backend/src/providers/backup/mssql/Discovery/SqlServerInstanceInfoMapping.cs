using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.BackupProviders.MSSQL.Discovery;

/// <summary>The engine-neutral description of an instance and of a backed-up database; shared by discovery and by backup execution so both describe the instance identically.</summary>
internal static class SqlServerInstanceInfoMapping
{
    public static DatabaseInstanceInfo ToInstanceInfo(SqlServerInstanceInfo instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new DatabaseInstanceInfo(
            DatabaseEngineId.MsSql,
            instance.ServerName,
            instance.InstanceName,
            instance.Version?.ToString() ?? instance.RawProductVersion,
            instance.Edition.Text,
            instance.HostPlatform == SqlServerHostPlatform.Unknown ? null : instance.HostPlatform.ToString());
    }

    /// <summary>The backup source with stable, engine-specific detail keys: <c>recovery-model</c>, <c>compatibility-level</c>, <c>transparent-data-encryption</c>, <c>user-access</c>, <c>edition</c>, <c>host-platform</c>.</summary>
    public static DatabaseBackupSource ToBackupSource(SqlServerInstanceInfo instance, SqlServerDatabaseInfo database)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(database);

        var details = new Dictionary<string, string>
        {
            ["recovery-model"] = database.RecoveryModel switch
            {
                SqlServerRecoveryModel.Simple => "simple",
                SqlServerRecoveryModel.Full => "full",
                SqlServerRecoveryModel.BulkLogged => "bulk-logged",
                _ => "unknown"
            },
            ["compatibility-level"] = database.CompatibilityLevel?.ToString() ?? "unknown",
            ["transparent-data-encryption"] = database.IsTransparentDataEncrypted switch
            {
                true => "enabled",
                false => "disabled",
                null => "unknown"
            },
            ["user-access"] = database.UserAccess switch
            {
                SqlServerUserAccess.MultiUser => "multi-user",
                SqlServerUserAccess.SingleUser => "single-user",
                SqlServerUserAccess.RestrictedUser => "restricted-user",
                _ => "unknown"
            },
            ["edition"] = instance.Edition.Text ?? "unknown",
            ["host-platform"] = instance.HostPlatform switch
            {
                SqlServerHostPlatform.Windows => "windows",
                SqlServerHostPlatform.Linux => "linux",
                _ => "unknown"
            }
        };

        return new DatabaseBackupSource(ToInstanceInfo(instance), database.Name, details);
    }
}
