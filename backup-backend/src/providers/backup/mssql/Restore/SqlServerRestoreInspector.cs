using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Backup;
using BackupPlatform.BackupProviders.MSSQL.Connectivity;
using BackupPlatform.BackupProviders.MSSQL.PreChecks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.BackupProviders.MSSQL.Restore;

/// <summary>
/// Reads a staged backup with the engine itself: <c>RESTORE HEADERONLY</c>, <c>RESTORE FILELISTONLY</c> and, when asked,
/// <c>RESTORE VERIFYONLY</c>. Nothing is changed on the server. A file the server cannot open or that is not a restorable backup is a
/// <see cref="RestoreInspectionResult.Failure"/> with a stable reason, never an exception; only caller cancellation is thrown. Exactly one backup
/// set of type Database (a full backup) and no backup encryption are accepted, because that is all the restore can do.
/// </summary>
internal sealed partial class SqlServerRestoreInspector(ILogger logger, TimeProvider time)
{
    public async Task<RestoreInspectionResult> InspectAsync(
        DatabaseConnectionSettings connection,
        DatabaseCredential credential,
        RestoreInspectionRequest request,
        CancellationToken cancellationToken)
    {
        // The statements are built first: a path with control characters is an argument error, not a server failure.
        var headerSql = SqlServerRestoreCommandBuilder.BuildRestoreHeaderOnly(request.ServerFile);
        var fileListSql = SqlServerRestoreCommandBuilder.BuildRestoreFileListOnly(request.ServerFile);

        SqlServerSession session;
        try
        {
            session = await SqlServerSession.OpenAsync(connection, credential, logger, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseProviderException exception)
        {
            return Failure(SqlServerRestoreFailureClassifier.FromConnection(exception));
        }

        await using (session.ConfigureAwait(false))
        {
            var statement = SqlServerRestoreStatement.HeaderOnly;
            try
            {
                var headerRows = await session.QueryAsync(
                    "restore-headeronly", headerSql, bind: null, SqlServerBackupMetadataMapper.ReadRow, cancellationToken).ConfigureAwait(false);
                if (headerRows.Count == 0)
                {
                    return Failure(RestoreFailureKind.BackupInvalid, SqlServerRestoreReasons.BackupMediaInvalid,
                        "The file does not contain a backup set.", BackupFailureCause.Unclassified);
                }

                if (headerRows.Count > 1)
                {
                    return Failure(RestoreFailureKind.BackupInvalid, RestoreReasonCodes.BackupTypeUnsupported,
                        "The file contains more than one backup set; only a file with a single full database backup can be restored.", BackupFailureCause.Unsupported);
                }

                var header = SqlServerBackupMetadataMapper.MapHeader(headerRows[0]);
                if (header is null)
                {
                    return Failure(RestoreFailureKind.BackupInvalid, SqlServerRestoreReasons.BackupMediaInvalid,
                        "The backup header is incomplete; the file is not a valid SQL Server backup.", BackupFailureCause.Unclassified);
                }

                var typeCheck = SqlServerRestoreCompatibility.CheckBackupType(header);
                if (typeCheck.Status == BackupPreCheckStatus.Failed)
                {
                    return Failure(RestoreFailureKind.BackupInvalid, typeCheck.ReasonCode, typeCheck.Message, typeCheck.Cause);
                }

                statement = SqlServerRestoreStatement.FileListOnly;
                var fileRows = await session.QueryAsync(
                    "restore-filelistonly", fileListSql, bind: null, SqlServerBackupMetadataMapper.ReadRow, cancellationToken).ConfigureAwait(false);
                var mapped = fileRows.Select(SqlServerBackupMetadataMapper.MapFile).ToArray();
                var files = mapped.OfType<RestoreBackupFile>().ToArray();
                if (files.Length == 0 || files.Length != mapped.Length)
                {
                    return Failure(RestoreFailureKind.BackupInvalid, SqlServerRestoreReasons.BackupMediaInvalid,
                        "The backup does not list its database files; the file is not a valid SQL Server backup.", BackupFailureCause.Unclassified);
                }

                var warnings = new List<BackupWarning>();
                if (!header.HasChecksums)
                {
                    warnings.Add(new BackupWarning(SqlServerRestoreWarningCodes.BackupWithoutChecksums,
                        "The backup has no page checksums, so SQL Server cannot detect damaged pages in it while verifying or restoring it."));
                }

                DateTimeOffset? verifyStartedAt = null;
                DateTimeOffset? verifyFinishedAt = null;
                if (request.RunVerifyOnly)
                {
                    statement = SqlServerRestoreStatement.VerifyOnly;
                    verifyStartedAt = time.GetUtcNow();
                    var failure = await VerifyAsync(session, request, header.HasChecksums, cancellationToken).ConfigureAwait(false);
                    if (failure is not null)
                    {
                        return Failure(failure);
                    }

                    verifyFinishedAt = time.GetUtcNow();
                }

                return new RestoreInspectionResult(
                    new RestoreBackupInspection(header, files, request.RunVerifyOnly, verifyStartedAt, verifyFinishedAt), null, warnings);
            }
            catch (DatabaseProviderException exception)
            {
                return Failure(SqlServerRestoreFailureClassifier.Classify(statement, exception));
            }
        }
    }

    /// <summary>Runs <c>RESTORE VERIFYONLY</c> (with <c>CHECKSUM</c> only for a backup that has checksums); null when it passed.</summary>
    private async Task<SqlServerRestoreFailureInfo?> VerifyAsync(SqlServerSession session, RestoreInspectionRequest request, bool checksums, CancellationToken cancellationToken)
    {
        try
        {
            await session.ExecuteAsync(
                SqlServerBackupCommandBuilder.BuildVerifyOnly(request.ServerFile, checksums), request.VerifyTimeout, onInfoMessage: null, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (SqlException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var numbers = SqlServerRestoreFailureClassifier.ErrorNumbers(exception);
            LogStatementFailed(logger, exception, "RESTORE VERIFYONLY", string.Join(',', numbers));
            return SqlServerRestoreFailureClassifier.Classify(SqlServerRestoreStatement.VerifyOnly, numbers);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static RestoreInspectionResult Failure(SqlServerRestoreFailureInfo info) =>
        Failure(info.Kind, info.ReasonCode, info.Message, info.Cause);

    private static RestoreInspectionResult Failure(RestoreFailureKind kind, string reasonCode, string message, BackupFailureCause cause) =>
        new(null, new RestoreFailure(kind, RestoreExecutionStage.InspectingBackup, reasonCode, message, cause));

    [LoggerMessage(EventId = 45, Level = LogLevel.Warning, Message = "SQL Server statement failed: Statement={Statement} SqlErrorNumbers={SqlErrorNumbers}")]
    private static partial void LogStatementFailed(ILogger logger, Exception exception, string statement, string sqlErrorNumbers);
}
