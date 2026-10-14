using BackupPlatform.Agent.Core.Backup;
using BackupPlatform.Agent.Core.Connectivity;
using BackupPlatform.Agent.Core.Tasks;
using BackupPlatform.AgentTests.Support;
using BackupPlatform.Contracts.Backups;
using BackupPlatform.StorageProviders.Abstractions.Transfer;
using Microsoft.Extensions.Time.Testing;

namespace BackupPlatform.AgentTests;

public sealed class UploadBandwidthGovernorTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly TaskClient _client;
    private readonly UploadBandwidthGovernor _governor;
    private readonly Guid _lease = Guid.NewGuid();

    public UploadBandwidthGovernorTests()
    {
        _client = new TaskClient(_time);
        _governor = new UploadBandwidthGovernor(_client, new FakeCredentialStore(TestIdentity.Create()), Guid.NewGuid(), _lease, _time);
    }

    private static AgentBackendResult<UploadPolicyResponse> Policy(long? job = null, long? agent = null, long? share = null, int refresh = 3, int valid = 10) =>
        AgentBackendResult<UploadPolicyResponse>.Success(new UploadPolicyResponse
        {
            JobBytesPerSecond = job, AgentBytesPerSecond = agent, TenantShareBytesPerSecond = share,
            MinimumBytesPerSecond = job ?? agent ?? share, RefreshAfterSeconds = refresh, ValidForSeconds = valid
        });

    private static AgentBackendResult<UploadPolicyResponse> Failure(AgentBackendFailureKind kind) =>
        AgentBackendResult<UploadPolicyResponse>.Fail(new AgentBackendFailure(kind, "test"));

    // How many bytes one read may return is the visible effect of the rate: a slice while a limit applies, the whole buffer when unlimited.
    private static async Task<int> ReadAsync(IUploadBandwidthSession session, TimeSpan? timeout = null) =>
        await session.Wrap(new MemoryStream(new byte[1 << 20])).ReadAsync(new byte[1 << 20]).AsTask().WaitAsync(timeout ?? TimeSpan.FromSeconds(10));

    private async Task SettleAsync(TimeSpan advance)
    {
        _time.Advance(advance);
        for (var i = 0; i < 20; i++) await Task.Yield();
        await Task.Delay(1);
    }

    [Fact]
    public async Task The_lowest_ceiling_that_applies_is_the_rate()
    {
        _client.OnUploadPolicy = _ => Policy(job: 5_000_000, agent: 2_000_000, share: 9_000_000);
        await using var session = await _governor.BeginAsync(default);

        Assert.Equal(TransferRateLimiter.Chunk(2_000_000), await ReadAsync(session));
        var request = Assert.Single(_client.UploadPolicies);
        Assert.Equal(_lease, request.LeaseToken);
        Assert.False(request.Release);
    }

    [Fact]
    public async Task Without_any_limit_the_stream_is_read_at_full_size_and_no_slice_is_released()
    {
        await using (var session = await _governor.BeginAsync(default))
        {
            Assert.Equal(1 << 20, await ReadAsync(session));
            Assert.Equal(TimeSpan.Zero, session.DeadlineAllowance(1L << 40));
        }

        Assert.DoesNotContain(_client.UploadPolicies, request => request.Release);
    }

    [Fact]
    public async Task A_limit_set_while_the_upload_runs_reaches_it_at_the_next_renewal()
    {
        _client.OnUploadPolicy = _ => Policy(refresh: 15);
        await using var session = await _governor.BeginAsync(default);
        Assert.Equal(1 << 20, await ReadAsync(session));

        _client.OnUploadPolicy = _ => Policy(job: 100_000, refresh: 15);
        await SettleAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(TransferRateLimiter.Chunk(100_000), await ReadAsync(session));
    }

    [Fact]
    public async Task The_start_fails_closed_when_the_policy_is_refused_and_does_not_retry_a_refusal()
    {
        _client.OnUploadPolicy = _ => Failure(AgentBackendFailureKind.Rejected);

        await Assert.ThrowsAsync<UploadBandwidthUnavailableException>(() => _governor.BeginAsync(default));

        Assert.Single(_client.UploadPolicies);
    }

    [Fact]
    public async Task The_start_retries_a_transient_failure_a_few_times_and_then_fails_closed()
    {
        _client.OnUploadPolicy = _ => Failure(AgentBackendFailureKind.Transient);
        var start = _governor.BeginAsync(default);
        for (var i = 0; i < 5 && !start.IsCompleted; i++) await SettleAsync(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<UploadBandwidthUnavailableException>(() => start);
        Assert.Equal(3, _client.UploadPolicies.Count);

        var calls = 0;
        _client.OnUploadPolicy = _ => ++calls == 1 ? Failure(AgentBackendFailureKind.RateLimited) : Policy(job: 1_000_000);
        var recovered = _governor.BeginAsync(default);
        for (var i = 0; i < 5 && !recovered.IsCompleted; i++) await SettleAsync(TimeSpan.FromSeconds(3));
        await using var session = await recovered;
        Assert.Equal(TransferRateLimiter.Chunk(1_000_000), await ReadAsync(session));
    }

    [Fact]
    public async Task The_upload_does_not_begin_until_a_slice_is_free()
    {
        _client.OnUploadPolicy = _ => Policy(share: 0);
        var start = _governor.BeginAsync(default);
        await SettleAsync(TimeSpan.FromSeconds(3));
        await SettleAsync(TimeSpan.FromSeconds(3));
        Assert.False(start.IsCompleted, "with no slice the request would sit open without a byte flowing");
        Assert.All(_client.UploadPolicies, request => Assert.False(request.Release));
        Assert.True(_client.UploadPolicies.Count >= 3, "the waiting upload keeps renewing its place in the queue");

        _client.OnUploadPolicy = _ => Policy(share: 500_000);
        await SettleAsync(TimeSpan.FromSeconds(3));

        await using var session = await start;
        Assert.Equal(TransferRateLimiter.Chunk(500_000), await ReadAsync(session));
    }

    [Fact]
    public async Task An_upload_cancelled_while_it_waits_for_a_slice_gives_its_place_back()
    {
        _client.OnUploadPolicy = request => request.Release ? Policy() : Policy(share: 0);
        using var cancel = new CancellationTokenSource();
        var start = _governor.BeginAsync(cancel.Token);
        await SettleAsync(TimeSpan.FromSeconds(1));

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Single(_client.UploadPolicies, request => request.Release);
    }

    [Fact]
    public async Task A_policy_that_cannot_be_read_while_waiting_for_a_slice_still_fails_closed()
    {
        var calls = 0;
        _client.OnUploadPolicy = _ => ++calls == 1 ? Policy(share: 0) : Failure(AgentBackendFailureKind.Rejected);
        var start = _governor.BeginAsync(default);
        await SettleAsync(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<UploadBandwidthUnavailableException>(() => start);
    }

    [Fact]
    public async Task A_slice_that_cannot_be_renewed_in_time_stops_the_transfer_and_a_renewal_resumes_it()
    {
        _client.OnUploadPolicy = _ => Policy(share: 500_000, valid: 10);
        await using var session = await _governor.BeginAsync(default);
        Assert.Equal(TransferRateLimiter.Chunk(500_000), await ReadAsync(session));

        _client.OnUploadPolicy = _ => Failure(AgentBackendFailureKind.Transient);
        for (var i = 0; i < 12; i++) await SettleAsync(TimeSpan.FromSeconds(1));
        var held = ReadAsync(session, TimeSpan.FromMinutes(5));
        await SettleAsync(TimeSpan.Zero);
        Assert.False(held.IsCompleted, "the backend has given the slice to others; sending on would exceed the tenant limit");

        _client.OnUploadPolicy = _ => Policy(share: 250_000);
        await SettleAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(TransferRateLimiter.Chunk(250_000), await held);
    }

    [Fact]
    public async Task A_lost_lease_fails_the_transfer_at_its_next_read()
    {
        _client.OnUploadPolicy = _ => Policy(job: 1_000_000);
        await using var session = await _governor.BeginAsync(default);

        _client.OnUploadPolicy = _ => Failure(AgentBackendFailureKind.Rejected);
        await SettleAsync(TimeSpan.FromSeconds(3));

        await Assert.ThrowsAsync<UploadBandwidthUnavailableException>(() => ReadAsync(session));
    }

    [Fact]
    public async Task Ending_the_upload_gives_the_slice_back_and_stops_asking()
    {
        _client.OnUploadPolicy = request => request.Release ? Policy() : Policy(share: 500_000);
        var session = await _governor.BeginAsync(default);
        await session.DisposeAsync();

        Assert.True(Assert.Single(_client.UploadPolicies, request => request.Release).Release);
        var calls = _client.UploadPolicies.Count;
        await SettleAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(calls, _client.UploadPolicies.Count);
    }

    [Fact]
    public async Task The_deadline_is_widened_by_size_over_the_lowest_rate()
    {
        _client.OnUploadPolicy = _ => Policy(job: 1_000_000);
        await using var session = await _governor.BeginAsync(default);

        Assert.Equal(TimeSpan.FromSeconds(100), session.DeadlineAllowance(100_000_000));
    }
}
