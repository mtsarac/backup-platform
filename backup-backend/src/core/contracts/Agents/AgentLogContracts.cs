namespace BackupPlatform.Contracts.Agents;

/// <summary>A safe, structured agent event. The event code determines its message on the server.</summary>
public sealed record AgentLogEntry(Guid Id, DateTimeOffset Timestamp, string Code, Guid? JobId, Guid? RunId)
{
    public long DroppedCount { get; init; }
    public string Message => Code == "agent.logs_dropped" ? $"Agent dropped {DroppedCount} buffered log records" : AgentLogEvents.Message(Code) ?? "";
    public string Level => Code == "agent.logs_dropped" || Code.Contains("failed", StringComparison.Ordinal) ? "Warning" : "Information";
    public string Component => Code.Split('.')[0];
    public string? Error => Code.Contains("failed", StringComparison.Ordinal) ? "Failure details withheld" : null;
}

/// <summary>Agent log events admitted at the ingest boundary.</summary>
public static class AgentLogEvents
{
    public static string? Message(string? code) => code switch
    {
        "agent.started" => "Agent started",
        "agent.logs_dropped" => "Agent dropped buffered log records",
        "backup.started" => "Backup attempt started",
        "backup.failed" => "Backup attempt failed",
        "backup.stage_failed" => "Backup stage failed",
        "backup.cleanup_failed" => "Backup cleanup failed",
        "backup.finished" => "Backup attempt finished",
        "restore.stage_failed" => "Restore stage failed",
        "restore.cleanup_failed" => "Restore cleanup failed",
        "restore.finished" => "Restore finished",
        _ => null
    };
}
