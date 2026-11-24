using System.Text.Json;
using BackupPlatform.Contracts.Agents;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.Agent.Host;

/// <summary>Records only explicitly admitted events; arbitrary messages, arguments and exceptions never reach disk.</summary>
internal sealed class AgentLogJournal : ILoggerProvider, ISupportExternalScope
{
    private readonly string _directory;
    private readonly object _gate = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    private int _count;

    public AgentLogJournal(string directory)
    {
        _directory = Path.Combine(directory, "logs");
        Directory.CreateDirectory(_directory);
        if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("The agent log directory must not be a link.");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        lock (_gate) Seal();
    }

    public ILogger CreateLogger(string categoryName) => new JournalLogger(this, categoryName);
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
    public void Dispose() { lock (_gate) Seal(); }

    internal string[] Pending()
    {
        lock (_gate)
        {
            Seal();
            return Directory.GetFiles(_directory, "pending-*.jsonl").Order(StringComparer.Ordinal).ToArray();
        }
    }

    internal void Acknowledge(string path)
    {
        lock (_gate)
        {
            File.Move(path, Path.Combine(_directory, Path.GetFileName(path).Replace("pending-", "archive-")));
            foreach (var old in Directory.GetFiles(_directory, "archive-*.jsonl").OrderDescending(StringComparer.Ordinal).Skip(10)) File.Delete(old);
        }
    }

    internal void Record(string code, Guid? jobId = null, Guid? runId = null)
    {
        if (AgentLogEvents.Message(code) is null) return;
        var line = JsonSerializer.Serialize(new AgentLogEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, code, jobId, runId), JsonSerializerOptions.Web);
        lock (_gate)
        {
            File.AppendAllText(Path.Combine(_directory, "active.jsonl"), line + "\n");
            if (++_count >= 100) Seal();
        }
    }

    private void Seal()
    {
        var active = Path.Combine(_directory, "active.jsonl");
        if (File.Exists(active)) File.Move(active, Path.Combine(_directory, $"pending-{DateTime.UtcNow:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}.jsonl"));
        _count = 0;
    }

    private sealed class JournalLogger(AgentLogJournal owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var code = (category, eventId.Id) switch
            {
                ("BackupPlatform.Agent.Core.Backup.DatabaseBackupPipeline", 100) => "backup.stage_failed",
                ("BackupPlatform.Agent.Core.Backup.DatabaseBackupPipeline", 101) => "backup.cleanup_failed",
                ("BackupPlatform.Agent.Core.Backup.DatabaseBackupPipeline", 102) => "backup.finished",
                ("BackupPlatform.Agent.Core.Restore.DatabaseRestorePipeline", 200) => "restore.stage_failed",
                ("BackupPlatform.Agent.Core.Restore.DatabaseRestorePipeline", 201) => "restore.cleanup_failed",
                ("BackupPlatform.Agent.Core.Restore.DatabaseRestorePipeline", 202) => "restore.finished",
                ("BackupPlatform.Agent.Core.Tasks.BackupTaskWorker", 900) => "backup.started",
                ("BackupPlatform.Agent.Core.Tasks.BackupTaskWorker", 901) => "backup.failed",
                ("BackupPlatform.Agent.Host.AgentLogSender", 900) => "agent.started",
                _ => null
            };
            if (code is null) return;
            Guid? job = null, run = null;
            owner._scopes.ForEachScope((scope, _) =>
            {
                if (scope is not IEnumerable<KeyValuePair<string, object?>> values) return;
                foreach (var pair in values)
                {
                    if (pair.Key == "JobId" && pair.Value is Guid jobId) job = jobId;
                    if (pair.Key == "RunId" && pair.Value is Guid runId) run = runId;
                }
            }, (object?)null);
            try { owner.Record(code, job, run); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Agent log write failed ({error.GetType().Name}).");
            }
        }
    }
}
