using System.Data;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Backup;
using BackupPlatform.BackupProviders.MSSQL.Connectivity;
using BackupPlatform.BackupProviders.MSSQL.Discovery;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.BackupProviders.MSSQL.PreChecks;

/// <summary>
/// Gathers the facts of the staging pre-checks over an open session; the decisions are <see cref="SqlServerStagingPreCheck"/>.
/// <para>
/// Server-side probe: <c>EXEC master.sys.xp_create_subdir</c> asks the SQL Server process itself to create a uniquely named
/// directory below the staging directory, so success proves that the service account can write there. Right after, the agent
/// looks for that entry on its own side (which proves the two directories are the same storage) and removes it. It never runs a
/// backup. <c>xp_fileexist</c> is deliberately NOT used: for a login without sysadmin it answers "does not exist" even for existing
/// paths, so it is no evidence of absence.
/// </para>
/// <para>
/// Observed against SQL Server 2025 on Linux: a login with only <c>db_backupoperator</c> is refused (Msg 229) and the check is
/// reported as unverifiable; sysadmin succeeds; when the operating system refuses the service account the procedure fails with
/// Msg 22048 ("xp_create_subdir() returned error N", for example error 5 "Access is denied" or error 2 "path not found").
/// </para>
/// </summary>
internal static partial class SqlServerStagingProbe
{
    /// <summary>Msg 22048: <c>xp_create_subdir()</c> returned an operating-system error for the service account.</summary>
    internal const int ExtendedProcedureOsError = 22048;

    private const int AgentVisibilityAttempts = 6;
    private static readonly TimeSpan AgentVisibilityDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    // 8192 bytes per page. Needs VIEW DATABASE STATE; allocated extents are the closest cheap figure to what a backup writes.
    private const string UsedBytesSql = "SELECT SUM(CONVERT(bigint, allocated_extent_page_count)) * 8192 FROM {0}.sys.dm_db_file_space_usage;";

    // Upper bound: allocated size of the data files (log excluded, type 0 = ROWS).
    private const string AllocatedBytesSql = "SELECT SUM(CONVERT(bigint, size)) * 8192 FROM sys.master_files WHERE database_id = @id AND type = 0;";

    public static async Task<SqlServerStagingFacts> GatherAsync(
        SqlServerSession session,
        FullBackupPreCheckRequest request,
        SqlServerDatabaseInfo database,
        bool pathFormatUsable,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var staging = request.Staging ?? throw new ArgumentException("The request has no staging area.", nameof(request));
        var agent = new AgentStagingDirectory(staging.AgentDirectory);
        var access = agent.Inspect();
        var compression = request.RequestedCapabilities.Contains(Capabilities.SqlServerCapabilityIds.BackupCompression);

        if (!pathFormatUsable || !access.DirectoryExists || !access.CanList || !access.CanWrite)
        {
            return new SqlServerStagingFacts(access, null, null, compression);
        }

        var probe = await ProbeAsync(session, staging, agent, logger, cancellationToken).ConfigureAwait(false);
        var estimate = database.State == SqlServerDatabaseState.Online
            ? await EstimateAsync(session, database, cancellationToken).ConfigureAwait(false)
            : null;
        return new SqlServerStagingFacts(access, probe, estimate, compression);
    }

    internal static async Task<SqlServerStagingProbeOutcome> ProbeAsync(
        SqlServerSession session,
        StagingArea staging,
        AgentStagingDirectory agent,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // xp_create_subdir creates every missing parent, so probing a path that does not exist would build it on the server (and a
        // later backup would write into the directory just built). Whether it exists is settled first.
        if (await ServerDirectoryIsMissingAsync(session, staging.ServerDirectory, logger, cancellationToken).ConfigureAwait(false))
        {
            return SqlServerStagingProbeOutcome.ServerDirectoryMissing;
        }

        var name = $"bp_probe_{Guid.NewGuid():N}";
        var command = SqlServerBackupCommandBuilder.BuildCreateSubdirectory(staging.ServerDirectory.Combine(name));

        try
        {
            await session.ExecuteAsync(command, ProbeTimeout, onInfoMessage: null, cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var numbers = exception.Errors.Cast<SqlError>().Select(error => error.Number).ToArray();
            LogProbeFailed(logger, exception, string.Join(',', numbers));
            if (numbers.Any(number => number is 229 or 230 or 262 or 297 or 300))
            {
                return SqlServerStagingProbeOutcome.PermissionDenied;
            }

            return numbers.Contains(ExtendedProcedureOsError)
                ? SqlServerStagingProbeOutcome.ServerOsFailure
                : SqlServerStagingProbeOutcome.Failed;
        }

        var visible = false;
        for (var attempt = 0; attempt < AgentVisibilityAttempts && !visible; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(AgentVisibilityDelay, cancellationToken).ConfigureAwait(false);
            }

            visible = agent.EntryExists(name);
        }

        if (!visible)
        {
            return SqlServerStagingProbeOutcome.CreatedNotVisible;
        }

        return agent.TryDeleteEmptyDirectory(name)
            ? SqlServerStagingProbeOutcome.CreatedAndVisible
            : SqlServerStagingProbeOutcome.CreatedAndVisibleLeftover;
    }

    /// <summary>
    /// True only when the server itself says the directory does not exist. <c>xp_fileexist</c> answers truthfully for sysadmin and
    /// reports "does not exist" for everything else, so for any other login (and whenever the question cannot be asked) the answer
    /// is "not known to be missing" and the probe proceeds: such a login is refused by <c>xp_create_subdir</c> anyway. The procedure
    /// takes a 255-character name, so longer paths are not asked about.
    /// </summary>
    private static async Task<bool> ServerDirectoryIsMissingAsync(
        SqlServerSession session,
        DatabaseServerPath directory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (directory.Value.Length > 255)
        {
            return false;
        }

        try
        {
            var sysadmin = await session.QueryAsync(
                "staging directory probe (role)",
                "SELECT CONVERT(int, ISNULL(IS_SRVROLEMEMBER(N'sysadmin'), 0));",
                bind: null,
                static reader => reader.GetInt32(0),
                cancellationToken).ConfigureAwait(false);
            if (sysadmin.Count != 1 || sysadmin[0] != 1)
            {
                return false;
            }

            // Columns: File Exists, File is a Directory, Parent Directory Exists. They are tinyint, not int.
            var answer = await session.QueryAsync(
                "staging directory probe (exists)",
                "EXEC master.dbo.xp_fileexist @path;",
                parameters => parameters.Add(new SqlParameter("@path", SqlDbType.NVarChar, 255) { Value = directory.Value }),
                static reader => Convert.ToInt32(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
            return answer.Count == 1 && answer[0] == 0;
        }
        catch (DatabaseProviderException exception)
        {
            LogExistenceCheckFailed(logger, exception);
            return false;
        }
    }

    private static async Task<SqlServerBackupSizeEstimate?> EstimateAsync(
        SqlServerSession session,
        SqlServerDatabaseInfo database,
        CancellationToken cancellationToken)
    {
        try
        {
            var used = await session.QueryAsync(
                "estimate-used-bytes",
                string.Format(System.Globalization.CultureInfo.InvariantCulture, UsedBytesSql, SqlServerBackupCommandBuilder.QuoteIdentifier(database.Name)),
                bind: null,
                reader => reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0),
                cancellationToken).ConfigureAwait(false);
            if (used is [{ } bytes])
            {
                return new SqlServerBackupSizeEstimate(bytes, IsUpperBound: false);
            }
        }
        catch (DatabaseProviderException)
        {
            // Typically VIEW DATABASE STATE is missing; the allocated file size is the fallback.
        }

        try
        {
            var allocated = await session.QueryAsync(
                "estimate-allocated-bytes",
                AllocatedBytesSql,
                parameters => parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.Int) { Value = database.DatabaseId }),
                reader => reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0),
                cancellationToken).ConfigureAwait(false);
            return allocated is [{ } bytes] ? new SqlServerBackupSizeEstimate(bytes, IsUpperBound: true) : null;
        }
        catch (DatabaseProviderException)
        {
            return null;
        }
    }

    [LoggerMessage(EventId = 30, Level = LogLevel.Information, Message = "Staging probe failed: SqlErrorNumbers={SqlErrorNumbers}")]
    private static partial void LogProbeFailed(ILogger logger, Exception exception, string sqlErrorNumbers);

    [LoggerMessage(EventId = 31, Level = LogLevel.Information, Message = "Staging directory existence check could not be asked; the probe proceeds without it")]
    private static partial void LogExistenceCheckFailed(ILogger logger, Exception exception);
}
