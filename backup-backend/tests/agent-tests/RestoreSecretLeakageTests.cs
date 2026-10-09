using System.Security.Cryptography;
using System.Text.Json;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Restore;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Agents;
using BackupPlatform.Contracts.Restores;
using BackupPlatform.StorageProviders.Abstractions;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.AgentTests;

/// <summary>
/// Runs the real restore worker, the real pipeline and a real staging directory through success and failure and asserts that neither what the
/// agent logs nor what it sends back contains the database password, the agent credential, a connection string or a staging path.
/// </summary>
public sealed class RestoreSecretLeakageTests : IAsyncLifetime
{
    private const string Password = "local-only-password";
    private static readonly string[] Forbidden = [Password, TestIdentity.Secret, "BackupAgent ", "Authorization", "Password=hunter2", "prod-sql", "Server="];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bp_test_restore_leak_" + Guid.NewGuid().ToString("N")[..10]);
    private readonly DeterministicTimeProvider _time = new();
    private readonly CaptureLoggerProvider _logs = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly CannedRestoreProvider _provider = new();
    private readonly RestoreClient _client;
    private readonly RestoreTaskWorker _worker;
    private readonly AgentTaskEnvelope _envelope;
    private readonly TaskCredentials _credentials = new();
    private Task? _running;

    public RestoreSecretLeakageTests()
    {
        Directory.CreateDirectory(_root);
        var content = RandomNumberGenerator.GetBytes(5000);
        var harness = new RestoreTaskHarness();
        _envelope = harness.With(harness.Envelope(integrated: false), task => task with
        {
            Source = task.Source with { SizeBytes = content.Length, Checksum = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant() }
        });
        var storage = new MemoryReadableStorage(new StorageObjectKey(_envelope.Restore!.Source.StorageKey), content);
        var factory = _logs.CreateFactory(LogLevel.Trace);
        var pipeline = new DatabaseRestorePipeline([_provider], [storage], factory.CreateLogger<DatabaseRestorePipeline>(), _time);
        _client = new RestoreClient(_time);
        _worker = new RestoreTaskWorker(_client, new FakeCredentialStore(TestIdentity.Create()), pipeline,
            new RestoreTaskRequestFactory(_credentials, [StagingArea.SameHost(_root)]),
            new AgentSoftwareInfo("1.2.3", "linux", null, null), _time, factory.CreateLogger<RestoreTaskWorker>());
        _running = _worker.RunAsync(_stop.Token);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        try
        {
            await _stop.CancelAsync();
            if (_running is not null)
            {
                await _running.WaitAsync(TimeSpan.FromSeconds(10));
            }

            _stop.Dispose();
        }
        finally
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void AssertNoSecrets(string text, string where)
    {
        foreach (var token in Forbidden)
        {
            Assert.False(text.Contains(token, StringComparison.OrdinalIgnoreCase), $"{where} contains '{token}'");
        }
    }

    private string Wire(RestoreTaskCompletionRequest completion) => JsonSerializer.Serialize(completion, JsonSerializerOptions.Web);

    [Fact]
    public async Task A_successful_restore_sends_and_logs_no_secret_and_no_staging_path()
    {
        Assert.True(_worker.TryPublish(_envelope));
        await Wait.UntilAsync(() => _client.Completions.Count == 1, "completion");

        var completion = Assert.Single(_client.Completions);
        Assert.Equal("Succeeded", completion.Status);
        Assert.Equal(1, _credentials.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
        AssertNoSecrets(Wire(completion), "the completion");
        Assert.DoesNotContain(_root, Wire(completion), StringComparison.Ordinal);
        Assert.NotEmpty(_logs.Entries);
        AssertNoSecrets(_logs.AllText, "the log");
        Assert.DoesNotContain(_root, _logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_that_throws_text_with_a_connection_string_leaves_it_out_of_the_completion()
    {
        _provider.Execute = (_, _) => throw new InvalidOperationException("Server=prod-sql;Password=hunter2;file=" + _root + "/x.bak");
        Assert.True(_worker.TryPublish(_envelope));
        await Wait.UntilAsync(() => _client.Completions.Count == 1, "completion");

        var completion = Assert.Single(_client.Completions);
        Assert.Equal("Failed", completion.Status);
        Assert.Equal("InternalError", completion.FailureCode);
        Assert.Equal("Unknown", completion.TargetState);
        AssertNoSecrets(Wire(completion), "the completion");
        Assert.DoesNotContain(_root, Wire(completion), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(_root));
        // The agent's own log is where diagnostics belong; the credential it holds must still never be in it.
        Assert.DoesNotContain(Password, _logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(TestIdentity.Secret, _logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_message_from_a_provider_is_never_part_of_the_completion()
    {
        _provider.Execute = (request, _) => Task.FromResult(new DatabaseRestoreResult(
            DatabaseRestoreStatus.Failed,
            new RestoreFailure(RestoreFailureKind.SqlRestoreFailed, RestoreExecutionStage.Restoring, "restore-failed", "Server=prod-sql;Password=hunter2 failed."),
            DatabaseRestoreTargetState.PartiallyRestored, "RESTORING", DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(-1), null, request.Plan.Mappings, []));
        Assert.True(_worker.TryPublish(_envelope));
        await Wait.UntilAsync(() => _client.Completions.Count == 1, "completion");

        var completion = Assert.Single(_client.Completions);
        Assert.Equal("SqlRestoreFailed", completion.FailureCode);
        Assert.Equal("PartiallyRestored", completion.TargetState);
        AssertNoSecrets(Wire(completion), "the completion");
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task A_credential_that_cannot_be_resolved_leaks_neither_its_name_nor_the_resolver_text()
    {
        _credentials.Failure = new InvalidOperationException("Server=prod-sql;Password=hunter2");
        Assert.True(_worker.TryPublish(_envelope));
        await Wait.UntilAsync(() => _client.Completions.Count == 1, "completion");

        var completion = Assert.Single(_client.Completions);
        Assert.Equal("InvalidConfiguration", completion.FailureCode);
        Assert.Equal("NotModified", completion.TargetState);
        AssertNoSecrets(Wire(completion), "the completion");
        Assert.DoesNotContain("hunter2", _logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("sales-db", Wire(completion), StringComparison.Ordinal);
    }
}
