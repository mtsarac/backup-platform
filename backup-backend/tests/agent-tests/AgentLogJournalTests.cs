using System.Text.Json;
using BackupPlatform.Agent.Host;
using BackupPlatform.Contracts.Agents;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.AgentTests;

public sealed class AgentLogJournalTests
{
    [Fact]
    public void JournalPersistsOnlyAdmittedEventsAndNeverFormatsSecrets()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using var journal = new AgentLogJournal(directory);
            var logger = journal.CreateLogger("BackupPlatform.Agent.Core.Backup.DatabaseBackupPipeline");
            var job = Guid.NewGuid();
            var run = Guid.NewGuid();
            using (logger.BeginScope(new Dictionary<string, object> { ["JobId"] = job, ["RunId"] = run }))
            {
                logger.Log(LogLevel.Warning, new EventId(100), "password=hunter2", new Exception("token=secret"), static (state, error) => state + error);
                logger.Log(LogLevel.Warning, new EventId(999), "private", null, static (state, _) => state);
            }
            var pending = Assert.Single(journal.Pending());
            var text = File.ReadAllText(pending);
            Assert.DoesNotContain("hunter2", text);
            Assert.DoesNotContain("secret", text);
            Assert.DoesNotContain("private", text);
            var entry = JsonSerializer.Deserialize<AgentLogEntry>(text, JsonSerializerOptions.Web)!;
            Assert.Equal("backup.stage_failed", entry.Code);
            Assert.Equal(job, entry.JobId);
            Assert.Equal(run, entry.RunId);
            journal.Acknowledge(pending);
            Assert.Empty(journal.Pending());
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void UnacknowledgedLogsSurviveRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using (var journal = new AgentLogJournal(directory)) journal.Record("agent.started");
            using var restarted = new AgentLogJournal(directory);
            var pending = Assert.Single(restarted.Pending());
            Assert.Contains("agent.started", File.ReadAllText(pending));
        }
        finally { Directory.Delete(directory, true); }
    }
}
