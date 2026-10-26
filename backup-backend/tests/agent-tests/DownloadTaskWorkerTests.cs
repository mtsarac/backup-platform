using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.Contracts.Downloads;

namespace BackupPlatform.AgentTests;

public sealed class DownloadTaskWorkerTests
{
    [Fact]
    public async Task Claimed_local_object_is_verified_once_and_unavailable_peer_never_uses_a_payload_upload()
    {
        await using var harness = new DownloadTaskHarness();
        byte[] bytes = Enumerable.Range(0, 131073).Select(value => (byte)value).ToArray();
        var envelope = harness.Envelope(bytes);
        await harness.PutAsync(envelope, bytes);
        Assert.True(harness.Worker.TryPublish(envelope));
        Assert.False(harness.Worker.TryPublish(envelope));
        harness.Start();
        await Wait.UntilAsync(() => !harness.Client.Completions.IsEmpty, "explicit direct-path refusal");
        Assert.Single(harness.Client.Signals);
        Assert.Equal(envelope.Download!.LeaseToken, Assert.Single(harness.Client.Starts).LeaseToken);
        Assert.Equal(DownloadAgentFailureCodes.DirectPathUnavailable, Assert.Single(harness.Client.Completions).FailureCode);
        Assert.False(harness.Worker.TryPublish(envelope));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrong_checksum_or_length_never_sends_backup_bytes(bool wrongLength)
    {
        await using var harness = new DownloadTaskHarness();
        byte[] bytes = [1, 2, 3, 4];
        var envelope = harness.Envelope(bytes);
        envelope = envelope with { Download = envelope.Download! with
        {
            Checksum = wrongLength ? envelope.Download.Checksum : new string('a', 64),
            SizeBytes = wrongLength ? bytes.Length + 1 : bytes.Length
        } };
        await harness.PutAsync(envelope, bytes);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await Wait.UntilAsync(() => !harness.Client.Completions.IsEmpty, "integrity refusal");
        Assert.Equal(DownloadAgentFailureCodes.IntegrityMismatch, Assert.Single(harness.Client.Completions).FailureCode);
        Assert.Empty(harness.Client.Signals);
    }

    [Theory]
    [InlineData("../private.key", "local", "local")]
    [InlineData("/etc/passwd", "local", "local")]
    [InlineData("C:\\Windows\\private.key", "local", "local")]
    [InlineData("tenant/artifact.bak", "central", "central")]
    [InlineData("tenant/artifact.bak", "local", "/arbitrary/root")]
    [InlineData("tenant/artifact.bak", "local", "../escape")]
    public async Task A_task_cannot_choose_filesystem_paths_or_unconfigured_storage_targets(string key, string provider, string target)
    {
        await using var harness = new DownloadTaskHarness();
        var envelope = harness.Envelope([1, 2, 3], key);
        envelope = envelope with { Download = envelope.Download! with { StorageProvider = provider, StorageTarget = target } };
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await Wait.UntilAsync(() => !harness.Client.Completions.IsEmpty, "source validation refusal");
        Assert.Equal(DownloadAgentFailureCodes.InvalidConfiguration, Assert.Single(harness.Client.Completions).FailureCode);
        Assert.Empty(harness.Client.Signals);
    }

    [Fact]
    public async Task A_volume_name_the_agent_does_not_declare_is_inaccessible()
    {
        await using var harness = new DownloadTaskHarness();
        var envelope = harness.Envelope([1, 2, 3], "tenant/artifact.bak");
        envelope = envelope with { Download = envelope.Download! with { StorageTarget = "external-ssd" } };
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await Wait.UntilAsync(() => !harness.Client.Completions.IsEmpty, "unknown volume refusal");
        Assert.Equal(DownloadAgentFailureCodes.SourceInaccessible, Assert.Single(harness.Client.Completions).FailureCode);
        Assert.Empty(harness.Client.Signals);
    }

    [Fact]
    public async Task Missing_local_object_is_reported_without_uploading_any_content()
    {
        await using var harness = new DownloadTaskHarness();
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope([1, 2, 3])));
        await Wait.UntilAsync(() => !harness.Client.Completions.IsEmpty, "missing source refusal");
        Assert.Equal(DownloadAgentFailureCodes.SourceMissing, Assert.Single(harness.Client.Completions).FailureCode);
        Assert.Empty(harness.Client.Signals);
    }

    [Fact]
    public async Task A_provider_symlink_escape_cannot_read_an_outside_file()
    {
        if (OperatingSystem.IsWindows()) return; // Creating links requires an elevated privilege on Windows; containment is shared by both platforms.
        await using var harness = new DownloadTaskHarness();
        var outside = Path.Combine(Path.GetTempPath(), "download-outside-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(outside, [1, 2, 3]);
        try
        {
            var envelope = harness.Envelope([1, 2, 3], "linked.bak");
            File.CreateSymbolicLink(Path.Combine(harness.Root, "linked.bak"), outside);
            harness.Start();
            Assert.True(harness.Worker.TryPublish(envelope));
            await Wait.UntilAsync(() => !harness.Client.Completions.IsEmpty, "link refusal");
            Assert.Equal(DownloadAgentFailureCodes.SourceInaccessible, Assert.Single(harness.Client.Completions).FailureCode);
            Assert.Empty(harness.Client.Signals);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task Renewed_cancellation_stops_peer_negotiation_and_reports_cancelled()
    {
        await using var harness = new DownloadTaskHarness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Client.OnSignal = async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return AgentBackendResult<DownloadSignal>.Success(new DownloadSignal(null));
        };
        harness.Client.OnRenew = () => AgentBackendResult<BackupTaskLeaseResponse>.Success(new BackupTaskLeaseResponse
        {
            LeaseExpiresAt = harness.Time.GetUtcNow().AddSeconds(60), CancellationRequested = true
        });
        var envelope = harness.Envelope([1, 2, 3]);
        await harness.PutAsync(envelope, [1, 2, 3]);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Time.Advance(TimeSpan.FromSeconds(10));
        await Wait.UntilAsync(() => !harness.Client.Completions.IsEmpty, "cancelled completion");
        var completion = Assert.Single(harness.Client.Completions);
        Assert.Equal("Cancelled", completion.Status);
        Assert.Equal(DownloadAgentFailureCodes.Cancelled, completion.FailureCode);
    }

    [Fact]
    public async Task Lost_lease_cancels_transfer_without_stale_completion()
    {
        await using var harness = new DownloadTaskHarness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Client.OnSignal = async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopped.SetResult(); }
            return AgentBackendResult<DownloadSignal>.Success(new DownloadSignal(null));
        };
        harness.Client.OnRenew = () => Failures.Fail<BackupTaskLeaseResponse>(AgentBackendFailureKind.Rejected);
        var envelope = harness.Envelope([1, 2, 3]);
        await harness.PutAsync(envelope, [1, 2, 3]);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(envelope));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Time.Advance(TimeSpan.FromSeconds(10));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(harness.Client.Completions);
    }

    [Fact]
    public async Task Start_refusal_never_opens_or_uploads_the_source()
    {
        await using var harness = new DownloadTaskHarness();
        harness.Client.OnStart = () => Failures.Fail<BackupTaskLeaseResponse>(AgentBackendFailureKind.Unauthorized);
        harness.Start();
        Assert.True(harness.Worker.TryPublish(harness.Envelope([1, 2, 3])));
        await Wait.UntilAsync(() => !harness.Client.Starts.IsEmpty, "start rejection");
        Assert.Empty(harness.Client.Signals);
        Assert.Empty(harness.Client.Completions);
    }
}
