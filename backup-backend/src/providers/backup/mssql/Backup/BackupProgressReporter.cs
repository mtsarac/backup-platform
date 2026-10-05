using System.Globalization;
using BackupPlatform.BackupProviders.Abstractions.Database;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.BackupProviders.MSSQL.Backup;

/// <summary>
/// Turns what is observed during an execution into <see cref="BackupProgress"/> reports, and guarantees reporting never matters.
/// <para>
/// Stage transitions are reported synchronously, in order, and only forward through Preparing, BackingUp and Verifying; the
/// terminal stages belong to the pipeline around the provider. Within BackingUp the percentage never decreases.
/// </para>
/// <para>
/// Where the percentage comes from: the <c>STATS = 1</c> option makes the server send an informational message with number
/// 3211 ("N percent processed.") to the very connection that runs the BACKUP, so it works for every login. The alternative,
/// polling <c>sys.dm_exec_requests.percent_complete</c> from a second connection, is more structural but needs
/// <c>VIEW SERVER STATE</c>, which a least-privilege <c>db_backupoperator</c> login does not have (such a login only sees its own
/// session), so it cannot be the primary mechanism. Only message number 3211 is accepted and only its leading integer is read;
/// anything else is ignored, because progress is advisory. A timer in the executor adds the size of the staged file as seen by
/// the agent, which is the only byte count that is observable without extra privileges.
/// </para>
/// <para>
/// Every call into the consumer is wrapped: an exception it throws is logged at debug level and swallowed. Reports are
/// serialised under one lock so their order is the order of observation; a slow consumer can therefore delay later reports,
/// but never changes the outcome of the backup.
/// </para>
/// </summary>
internal sealed partial class BackupProgressReporter(IProgress<BackupProgress>? progress, TimeProvider time, ILogger logger)
{
    /// <summary>The stable message number SQL Server uses for <c>STATS</c> progress messages.</summary>
    public const int StatsMessageNumber = 3211;

    private readonly object _gate = new();
    private BackupExecutionStage? _stage;
    private double? _percent;

    public BackupExecutionStage CurrentStage
    {
        get
        {
            lock (_gate)
            {
                return _stage ?? BackupExecutionStage.Preparing;
            }
        }
    }

    /// <summary>Enters a stage. Returns false (and reports nothing) when the stage is not a later provider stage than the current one.</summary>
    public bool EnterStage(BackupExecutionStage stage)
    {
        if (stage is not (BackupExecutionStage.Preparing or BackupExecutionStage.BackingUp or BackupExecutionStage.Verifying))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "A database provider reports only Preparing, BackingUp and Verifying.");
        }

        lock (_gate)
        {
            if (_stage is { } current && stage <= current)
            {
                return false;
            }

            _stage = stage;
            _percent = null;
            Report(new BackupProgress(stage, null, null, null, time.GetUtcNow()));
            return true;
        }
    }

    /// <summary>Handles one server message; only <c>STATS</c> progress (number 3211) with a leading integer in 0..100 produces a report.</summary>
    public void OnInfoMessage(SqlInfoMessageEventArgs args)
    {
        foreach (SqlError error in args.Errors)
        {
            if (error.Number == StatsMessageNumber && TryParsePercent(error.Message, out var percent))
            {
                ReportPercent(percent);
            }
        }
    }

    internal static bool TryParsePercent(string? message, out int percent)
    {
        percent = 0;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var text = message.AsSpan().TrimStart();
        var end = 0;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        return end > 0
            && int.TryParse(text[..end], NumberStyles.None, CultureInfo.InvariantCulture, out percent)
            && percent is >= 0 and <= 100;
    }

    public void ReportPercent(double percent)
    {
        lock (_gate)
        {
            if (_stage != BackupExecutionStage.BackingUp || percent is < 0 or > 100 || percent < _percent)
            {
                return;
            }

            _percent = percent;
            Report(new BackupProgress(BackupExecutionStage.BackingUp, percent, null, null, time.GetUtcNow()));
        }
    }

    /// <summary>Reports the staged file's current size (and the latest known percentage, so the pair stays consistent).</summary>
    public void ReportBytes(long bytes)
    {
        lock (_gate)
        {
            if (_stage != BackupExecutionStage.BackingUp || bytes < 0)
            {
                return;
            }

            Report(new BackupProgress(BackupExecutionStage.BackingUp, _percent, bytes, null, time.GetUtcNow()));
        }
    }

    private void Report(BackupProgress value)
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

    [LoggerMessage(EventId = 21, Level = LogLevel.Debug, Message = "Progress consumer threw and was ignored: Stage={Stage}")]
    private static partial void LogReportFailed(ILogger logger, Exception exception, BackupExecutionStage stage);
}
