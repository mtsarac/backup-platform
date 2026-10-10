using BackupPlatform.BackupProviders.Abstractions.Database;

namespace BackupPlatform.Agent.Core.Backup;

/// <summary>
/// The stage state machine of one execution. Stages only move forward; a terminal stage (Completed, Failed, Cancelled) closes the
/// timeline and nothing can follow it. Every transition is also reported as progress. The consumer of progress is untrusted for
/// the purpose of execution: whatever it throws is swallowed, so reporting can never change an outcome. Providers report from
/// their own threads, so every member takes the same lock; reports reach the consumer one at a time, in order.
/// </summary>
internal sealed class BackupStageTracker
{
    private readonly object _gate = new();
    private readonly List<BackupStageRecord> _records = [];
    private readonly IProgress<BackupProgress>? _consumer;
    private readonly TimeProvider _time;
    private BackupExecutionStage? _current;

    public BackupStageTracker(IProgress<BackupProgress>? consumer, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _consumer = consumer;
        _time = time;
    }

    public BackupExecutionStage? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public IReadOnlyList<BackupStageRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return _records.ToArray();
            }
        }
    }

    /// <summary>Moves to <paramref name="stage"/>, which must come after the current one. Entering the current stage again is a no-op (a provider reports its stages itself).</summary>
    public void Enter(BackupExecutionStage stage)
    {
        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown backup stage.");
        }

        lock (_gate)
        {
            if (_current == stage)
            {
                return;
            }

            // Uploading again after VerifyingReplica is the next destination of the same artifact; any other backward move is a bug.
            var nextDestination = stage == BackupExecutionStage.Uploading && _current == BackupExecutionStage.VerifyingReplica;
            if (IsTerminal(_current) || (_current is { } current && stage < current && !IsTerminal(stage) && !nextDestination))
            {
                throw new InvalidOperationException($"A backup cannot move from {_current} to {stage}.");
            }

            var now = _time.GetUtcNow();
            if (_records.Count > 0)
            {
                _records[^1] = _records[^1] with { FinishedAt = now };
            }

            _records.Add(new BackupStageRecord(stage, now, IsTerminal(stage) ? now : null));
            _current = stage;
            Publish(new BackupProgress(stage, stage == BackupExecutionStage.Completed ? 100 : null, null, null, now));
        }
    }

    /// <summary>Reports progress inside the current stage. Ignored for any other stage and once the timeline is closed.</summary>
    public void Report(BackupExecutionStage stage, double? percent, long? bytes, long? totalBytes)
    {
        lock (_gate)
        {
            if (IsTerminal(_current) || _current != stage)
            {
                return;
            }

            Publish(new BackupProgress(
                stage,
                percent is { } value && !double.IsNaN(value) ? Math.Clamp(value, 0, 100) : null,
                bytes is >= 0 ? bytes : null,
                totalBytes is >= 0 ? totalBytes : null,
                _time.GetUtcNow()));
        }
    }

    /// <summary>
    /// Forwards a provider's own progress: its stage changes are recorded (a provider owns Preparing, BackingUp and Verifying) and
    /// its numbers are passed on. Terminal stages belong to the pipeline and are ignored, as are stages that would go backwards.
    /// </summary>
    public void Forward(BackupProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (IsTerminal(progress.Stage))
        {
            return;
        }

        lock (_gate)
        {
            if (IsTerminal(_current) || (_current is { } current && progress.Stage < current))
            {
                return;
            }

            Enter(progress.Stage);
            Report(progress.Stage, progress.Percent, progress.Bytes, progress.TotalBytes);
        }
    }

    private static bool IsTerminal(BackupExecutionStage? stage) =>
        stage is BackupExecutionStage.Completed or BackupExecutionStage.Failed or BackupExecutionStage.Cancelled;

    private void Publish(BackupProgress progress)
    {
        if (_consumer is null)
        {
            return;
        }

        try
        {
            _consumer.Report(progress);
        }
        // Progress consumers are untrusted: nothing they throw may reach the execution.
        catch (Exception)
        {
        }
    }
}
