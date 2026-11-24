using System.Text.Json;
using System.Net;
using BackupPlatform.Agent.Core.Identity;
using BackupPlatform.Agent.Transport;
using BackupPlatform.Agent.Host;
using BackupPlatform.Contracts.Agents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    [Fact]
    public void OfflineQueueDropsOldestSegmentsAndRecordsOneCumulativeWarning()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using var journal = new AgentLogJournal(directory, maxPendingSegments: 2);
            var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
            for (var index = 0; index < 3; index++)
            {
                journal.Record("backup.started", jobId: ids[index]);
                journal.Pending();
            }
            var entries = journal.Pending().SelectMany(File.ReadAllLines)
                .Select(line => JsonSerializer.Deserialize<AgentLogEntry>(line, JsonSerializerOptions.Web)!).ToArray();
            Assert.Equal(2, entries.Length);
            Assert.Equal(ids[2], Assert.Single(entries, entry => entry.Code == "backup.started").JobId);
            var warning = Assert.Single(entries, entry => entry.Code == "agent.logs_dropped");
            Assert.Equal(2, warning.DroppedCount);
            Assert.Equal("Warning", warning.Level);
            Assert.Contains("2", warning.Message);

            journal.Record("backup.started", jobId: ids[3]);
            entries = journal.Pending().SelectMany(File.ReadAllLines)
                .Select(line => JsonSerializer.Deserialize<AgentLogEntry>(line, JsonSerializerOptions.Web)!).ToArray();
            Assert.Equal(ids[3], Assert.Single(entries, entry => entry.Code == "backup.started").JobId);
            Assert.Equal(4, Assert.Single(entries, entry => entry.Code == "agent.logs_dropped").DroppedCount);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void QueueNeverEvictsAnInFlightSegment()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using var journal = new AgentLogJournal(directory, maxPendingSegments: 2);
            journal.Record("agent.started");
            var inFlight = Assert.Single(journal.Pending());
            Assert.True(journal.TryClaim(inFlight));
            journal.Record("agent.started");
            journal.Pending();
            journal.Record("agent.started");
            Assert.Contains(inFlight, journal.Pending());
            journal.Acknowledge(inFlight);
            journal.Release(inFlight);
        }
        finally { Directory.Delete(directory, true); }
    }


    [Fact]
    public async Task FailedBatchIsRetriedAfterRestartAndAcknowledged()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using var journal = new AgentLogJournal(directory);
            journal.Record("agent.started");
            var identity = new AgentLocalIdentity(Guid.NewGuid(), new Uri("https://example.test"), Guid.NewGuid(), new AgentStoredCredential(Guid.NewGuid(), new string('a', 43), 1));
            var store = new FixedStore(identity);
            var endpoint = new AgentBackendEndpoint(identity.ServerUri);
            var failure = new Reply(HttpStatusCode.ServiceUnavailable);
            using (var sender = new AgentLogSender(journal, store, endpoint, new HttpClient(failure), NullLogger<AgentLogSender>.Instance))
            {
                await sender.StartAsync(CancellationToken.None);
                await failure.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await sender.StopAsync(CancellationToken.None);
            }
            Assert.Single(journal.Pending());
            var success = new Reply(HttpStatusCode.NoContent);
            using (var sender = new AgentLogSender(journal, store, endpoint, new HttpClient(success), NullLogger<AgentLogSender>.Instance))
            {
                await sender.StartAsync(CancellationToken.None);
                Assert.True(SpinWait.SpinUntil(() => Directory.GetFiles(Path.Combine(directory, "logs"), "archive-*.jsonl").Length == 1, TimeSpan.FromSeconds(5)));
                await sender.StopAsync(CancellationToken.None);
            }
            Assert.Empty(journal.Pending());
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class Reply(HttpStatusCode status) : HttpMessageHandler
    {
        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class FixedStore(AgentLocalIdentity identity) : IAgentCredentialStore
    {
        public Task<AgentLocalIdentity?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult<AgentLocalIdentity?>(identity);
        public Task SaveAsync(AgentLocalIdentity value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
