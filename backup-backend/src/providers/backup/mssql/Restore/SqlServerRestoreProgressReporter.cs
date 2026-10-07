using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.BackupProviders.MSSQL.Backup;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.BackupProviders.MSSQL.Restore;

/// <summary>
/// Turns what is observed during a restore into <see cref="RestoreProgress"/> reports and guarantees reporting never matters. The same
/// technique as <see cref="BackupProgressReporter"/>: <c>STATS = 1</c> makes the server send an informational message with number 3211
/// ("N percent processed.") to the very connection that runs the RESTORE, so the percentage needs no second connection and no extra
/// permission. Stage transitions are reported synchronously and only forward through PreparingTarget, Restoring and Finalizing (the other
/// stages belong to the pipeline around the provider); within Restoring the percentage never decreases. Every call into the consumer is
/// wrapped: an exception it throws is logged at debug level and swallowed, and reports are serialised under one lock so their order is the
/// order of observation.
/// </summary>
internal sealed partial class SqlServerRestoreProgressReporter(IProgress<RestoreProgress>? progress, TimeProvider time, ILogger logger)
{
    private readonly object _gate = new();
    private RestoreExecutionStage? _stage;
    private double? _percent;

    public RestoreExecutionStage CurrentStage
    {
        get
        {
            lock (_gate)
            {
                return _stage ?? RestoreExecutionStage.PreparingTarget;
            }
        }
    }

    /// <summary>Enters a stage. Returns false (and reports nothing) when the stage is not a later provider stage than the current one.</summary>
    public bool EnterStage(RestoreExecutionStage stage)
    {
        if (stage is not (RestoreExecutionStage.PreparingTarget or RestoreExecutionStage.Restoring or RestoreExecutionStage.Finalizing))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "A restore provider reports only PreparingTarget, Restoring and Finalizing.");
        }

        lock (_gate)
        {
            if (_stage is { } current && stage <= current)
            {
                return false;
            }

            _stage = stage;
            _percent = null;
            Report(new RestoreProgress(stage, null, null, null, time.GetUtcNow()));
            return true;
        }
    }

    /// <summary>Handles one server message; only <c>STATS</c> progress (number 3211) with a leading integer in 0..100 produces a report.</summary>
    public void OnInfoMessage(SqlInfoMessageEventArgs args)
    {
        foreach (SqlError error in args.Errors)
        {
            if (error.Number == BackupProgressReporter.StatsMessageNumber && BackupProgressReporter.TryParsePercent(error.Message, out var percent))
            {
                ReportPercent(percent);
            }
        }
    }

    public void ReportPercent(double percent)
    {
        lock (_gate)
        {
            if (_stage != RestoreExecutionStage.Restoring || percent is < 0 or > 100 || percent < _percent)
            {
                return;
            }

            _percent = percent;
            Report(new RestoreProgress(RestoreExecutionStage.Restoring, percent, null, null, time.GetUtcNow()));
        }
    }

    private void Report(RestoreProgress value)
    {
        if (progress is null)
        {
            return;
        }

        try
        {
            progress.Report(value);
        }
        catch (Exception exception)
        {
            LogReportFailed(logger, exception, value.Stage);
        }
    }

    [LoggerMessage(EventId = 41, Level = LogLevel.Debug, Message = "Restore progress consumer threw and was ignored: Stage={Stage}")]
    private static partial void LogReportFailed(ILogger logger, Exception exception, RestoreExecutionStage stage);
}
