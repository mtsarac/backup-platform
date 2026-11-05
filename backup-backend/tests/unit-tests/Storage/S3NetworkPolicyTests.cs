using System.Net;
using System.Net.Sockets;
using BackupPlatform.Domain.Identity;
using BackupPlatform.Domain.Storage;
using BackupPlatform.Storage;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.S3;
using Xunit;

namespace BackupPlatform.UnitTests.Storage;

public sealed class S3NetworkPolicyTests
{
    private static readonly TimeSpan Connect = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.5.5")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.1.1.1")]
    [InlineData("64:ff9b::7f00:1")]       // NAT64 of 127.0.0.1
    [InlineData("64:ff9b::a9fe:a9fe")]    // NAT64 of 169.254.169.254
    [InlineData("2002:7f00:1::1")]        // 6to4 of 127.0.0.1
    public void RestrictedAddressesAreRecognised(string address) => Assert.True(S3NetworkPolicy.IsRestricted(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    public void PublicAddressesAreNotRestricted(string address) => Assert.False(S3NetworkPolicy.IsRestricted(IPAddress.Parse(address)));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("169.254.169.254")]
    [InlineData("::ffff:10.0.0.5")]
    [InlineData("localhost")]
    public async Task ARestrictedHostIsRefusedWithoutAnAllowList(string host)
    {
        var error = await Assert.ThrowsAsync<StorageException>(() => new S3NetworkPolicy([], Connect).ResolveAsync(host, default));
        Assert.Equal(StorageFailureKind.NetworkDenied, error.Kind);
    }

    [Fact]
    public async Task AnAllowedNetworkPassesAndOnlyThatNetwork()
    {
        var policy = new S3NetworkPolicy(["10.0.0.0/8"], Connect);
        Assert.Equal(IPAddress.Parse("10.1.2.3"), Assert.Single(await policy.ResolveAsync("10.1.2.3", default)));
        Assert.Equal(IPAddress.Parse("10.1.2.3"), Assert.Single(await policy.ResolveAsync("::ffff:10.1.2.3", default)));
        foreach (var other in new[] { "192.168.0.1", "127.0.0.1" })
            Assert.Equal(StorageFailureKind.NetworkDenied, (await Assert.ThrowsAsync<StorageException>(() => policy.ResolveAsync(other, default))).Kind);
    }

    [Fact]
    public async Task APublicAddressNeedsNoAllowList() =>
        Assert.Equal(IPAddress.Parse("8.8.8.8"), Assert.Single(await new S3NetworkPolicy([], Connect).ResolveAsync("8.8.8.8", default)));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task ARefusedEndpointNeverReachesTheListener(string host)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = listener.AcceptTcpClientAsync();
        using var http = new HttpClient(new S3NetworkPolicy([], Connect).CreateHandler());

        var error = await Assert.ThrowsAsync<StorageException>(() => Provider(http, host, ((IPEndPoint)listener.LocalEndpoint).Port).CheckAccessAsync(default));

        Assert.Equal(StorageFailureKind.NetworkDenied, error.Kind);
        await Task.Delay(300);
        Assert.False(accepted.IsCompleted, "a connection reached the listener");
    }

    [Fact]
    public async Task AnAllowedLoopbackEndpointReachesTheListener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepted = listener.AcceptTcpClientAsync();
        using var http = new HttpClient(new S3NetworkPolicy(["127.0.0.0/8"], Connect).CreateHandler());
        var closer = Task.Run(async () => { using var client = await accepted; });

        var error = await Assert.ThrowsAsync<StorageException>(() => Provider(http, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port).CheckAccessAsync(default));

        Assert.NotEqual(StorageFailureKind.NetworkDenied, error.Kind); // the TLS handshake with a plain listener fails, after the connection was made
        await closer;
        Assert.True(accepted.IsCompleted);
    }

    [Fact]
    public async Task TheConnectionTestAnswersNotTestableForARefusedEndpointAndTheSameForAnyPort()
    {
        var results = new List<StorageTestResult>();
        foreach (var port in new[] { 1, 443, 65000 })
        {
            using var http = new HttpClient(new S3NetworkPolicy([], Connect).CreateHandler());
            var tester = new S3ConnectionTester((_, _) => Provider(http, "127.0.0.1", port));
            results.Add(await tester.TestAsync(Connection(), null!, default));
        }
        Assert.All(results, result => Assert.Equal((StorageTestStatus.NotTestable, StorageTestCodes.AgentSideStorage), (result.Status, result.Code)));
        Assert.Single(results.Select(result => result.Message).Distinct());
    }

    [Fact]
    public async Task TheConnectionTestEndsWithATimeoutCodeWhenNothingAnswers()
    {
        using var http = new HttpClient(new HangingHandler());
        var tester = new S3ConnectionTester((_, _) => Provider(http, "storage.example", 443), TimeSpan.FromMilliseconds(200));

        var result = await tester.TestAsync(Connection(), null!, default);

        Assert.Equal((StorageTestStatus.Failed, StorageTestCodes.Timeout), (result.Status, result.Code));
    }

    [Fact]
    public async Task ACallerCancellationIsStillACancellation()
    {
        using var http = new HttpClient(new HangingHandler());
        using var cancelled = new CancellationTokenSource(100);
        var tester = new S3ConnectionTester((_, _) => Provider(http, "storage.example", 443));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tester.TestAsync(Connection(), null!, cancelled.Token));
    }

    private static S3StorageProvider Provider(HttpClient http, string host, int port) => new(http,
        new(new Uri($"https://{(host.Contains(':') ? "[" + host.Trim('[', ']') + "]" : host)}:{port}"), "bucket", "us-east-1", Guid.NewGuid(), Guid.NewGuid()),
        _ => ValueTask.FromResult(new S3Credential("access", "secret")));

    private static StorageConnection Connection() =>
        new(Guid.NewGuid(), TenantId.New(), "S3", StorageProviderTypes.S3, StorageOwnership.TenantManaged, null, null, true, DateTimeOffset.UnixEpoch);

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        }
    }
}
