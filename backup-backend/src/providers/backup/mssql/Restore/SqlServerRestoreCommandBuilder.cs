using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Backup;

namespace BackupPlatform.BackupProviders.MSSQL.Restore;

/// <summary>
/// The only place that turns a restore into T-SQL text. RESTORE and ALTER DATABASE take no parameters for database names or file
/// names, so every identifier goes through <see cref="SqlServerBackupCommandBuilder.QuoteIdentifier"/> and every path or logical name
/// through <see cref="SqlServerBackupCommandBuilder.QuoteStringLiteral"/>: both refuse control characters and double the quote
/// characters, so nothing a caller supplies can end a literal or reshape a statement. Pure functions, no I/O.
/// </summary>
internal static class SqlServerRestoreCommandBuilder
{
    /// <summary>
    /// The error number raised (<c>THROW</c>) by the guard in front of a RESTORE that must not touch an existing database. 50000 and above is
    /// the user-defined range, so no engine error can carry it.
    /// </summary>
    public const int TargetExistsErrorNumber = 50001;

    private const string TargetExistsErrorText = "target-database-exists";

    public static string BuildRestoreHeaderOnly(DatabaseServerPath serverFile)
    {
        ArgumentNullException.ThrowIfNull(serverFile);
        return $"RESTORE HEADERONLY FROM DISK = {SqlServerBackupCommandBuilder.QuoteStringLiteral(serverFile.Value)};";
    }

    public static string BuildRestoreFileListOnly(DatabaseServerPath serverFile)
    {
        ArgumentNullException.ThrowIfNull(serverFile);
        return $"RESTORE FILELISTONLY FROM DISK = {SqlServerBackupCommandBuilder.QuoteStringLiteral(serverFile.Value)};";
    }

    /// <summary>
    /// <c>RESTORE DATABASE</c> with one <c>MOVE</c> per logical file (a file without a MOVE would be restored to the SOURCE server's path,
    /// so none is allowed), <c>RECOVERY</c> always (the platform never leaves a database restoring on purpose), <c>CHECKSUM</c> only when
    /// <paramref name="withChecksum"/> (asking for it on a backup without checksums is error 3187) and <c>REPLACE</c> only when
    /// <paramref name="replace"/>.
    /// <para>
    /// Without <paramref name="replace"/> the statement is guarded: SQL Server restores a backup over an existing database of the same family
    /// without any option (a SIMPLE-recovery database needs no <c>REPLACE</c> at all), so "no REPLACE" alone does not mean "the target does not
    /// exist". The guard makes the existence check part of the same batch, so a database that appeared after the caller looked fails the batch
    /// with <see cref="TargetExistsErrorNumber"/> instead of being overwritten.
    /// </para>
    /// </summary>
    public static string BuildRestoreDatabase(
        string databaseName,
        DatabaseServerPath serverFile,
        IReadOnlyList<RestoreFileMapping> moves,
        bool replace,
        bool withChecksum,
        bool stats)
    {
        ArgumentNullException.ThrowIfNull(serverFile);
        ArgumentNullException.ThrowIfNull(moves);
        if (moves.Count == 0)
        {
            throw new ArgumentException("A restore moves every file; without a MOVE a file would go back to the source server's path.", nameof(moves));
        }

        var options = new List<string>(moves.Count + 4);
        foreach (var move in moves)
        {
            options.Add($"MOVE {SqlServerBackupCommandBuilder.QuoteStringLiteral(move.LogicalName)} TO {SqlServerBackupCommandBuilder.QuoteStringLiteral(move.TargetPath.Value)}");
        }

        options.Add("RECOVERY");
        if (withChecksum)
        {
            options.Add("CHECKSUM");
        }

        if (replace)
        {
            options.Add("REPLACE");
        }

        if (stats)
        {
            options.Add("STATS = 1");
        }

        var identifier = SqlServerBackupCommandBuilder.QuoteIdentifier(databaseName);
        var guard = replace
            ? string.Empty
            : $"IF DB_ID({SqlServerBackupCommandBuilder.QuoteStringLiteral(databaseName)}) IS NOT NULL THROW {TargetExistsErrorNumber}, N'{TargetExistsErrorText}', 1; ";
        return $"{guard}RESTORE DATABASE {identifier} FROM DISK = {SqlServerBackupCommandBuilder.QuoteStringLiteral(serverFile.Value)} WITH {string.Join(", ", options)};";
    }

    /// <summary>Disconnects every other session (their open transactions roll back) and leaves one connection slot. Only for an existing target and only when the caller allowed it.</summary>
    public static string BuildSetSingleUser(string databaseName) =>
        $"ALTER DATABASE {SqlServerBackupCommandBuilder.QuoteIdentifier(databaseName)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;";

    public static string BuildSetMultiUser(string databaseName) =>
        $"ALTER DATABASE {SqlServerBackupCommandBuilder.QuoteIdentifier(databaseName)} SET MULTI_USER;";
}
