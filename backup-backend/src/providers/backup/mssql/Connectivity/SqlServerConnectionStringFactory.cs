using BackupPlatform.BackupProviders.Abstractions.Database;
using Microsoft.Data.SqlClient;

namespace BackupPlatform.BackupProviders.MSSQL.Connectivity;

/// <summary>
/// The single place where endpoint, options and credential become a SqlClient connection configuration. The result is a
/// <see cref="SqlConnectionStringBuilder"/> that stays inside this provider: it is never logged, returned or stored, and
/// values are assigned as properties (never concatenated), so nothing a caller supplies can inject connection-string keywords.
/// </summary>
internal static class SqlServerConnectionStringFactory
{
    public const string ApplicationName = "BackupPlatform.Agent";

    public static SqlConnectionStringBuilder Create(DatabaseConnectionSettings settings, DatabaseCredential credential)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credential);

        var builder = new SqlConnectionStringBuilder
        {
            // The "tcp:" prefix pins the normal SQL network protocol: no shared memory or named pipes, the same path for a
            // native service and for anything that publishes the port (a container, a forwarded port).
            DataSource = DataSource(settings.Endpoint),
            InitialCatalog = "master",
            ApplicationName = ApplicationName,
            ConnectTimeout = (int)settings.Options.ConnectTimeout.TotalSeconds,
            Encrypt = settings.Options.RequireEncryption ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = settings.Options.TrustServerCertificate,
            // Diagnostic sessions are short and rare: no pooling (nothing outlives the call, no pool keyed by a string holding
            // a password), no hidden reconnect retries (failures must be prompt and attributable), no ambient transaction.
            Pooling = false,
            ConnectRetryCount = 0,
            Enlist = false,
            MultipleActiveResultSets = false
        };

        switch (credential)
        {
            case IntegratedDatabaseCredential:
                builder.IntegratedSecurity = true;
                break;
            case PasswordDatabaseCredential password:
                builder.UserID = password.UserName;
                builder.Password = password.Password.Reveal();
                break;
            default:
                throw new ArgumentException("Unsupported credential type.", nameof(credential));
        }

        return builder;
    }

    private static string DataSource(DatabaseEndpoint endpoint) =>
        endpoint switch
        {
            { Port: { } port } => $"tcp:{endpoint.Host},{port}",
            { InstanceName: { } instance } => $"tcp:{endpoint.Host}\\{instance}",
            _ => $"tcp:{endpoint.Host}"
        };
}
