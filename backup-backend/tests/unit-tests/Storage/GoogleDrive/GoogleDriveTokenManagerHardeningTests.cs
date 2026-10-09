using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using BackupPlatform.StorageProviders.Abstractions;
using BackupPlatform.StorageProviders.GoogleDrive;

namespace BackupPlatform.UnitTests.Storage.GoogleDrive;

public sealed class GoogleDriveTokenManagerHardeningTests
{
    [Fact]
    public async Task FailedRotationPersistenceDoesNotPublishTheAccessTokenCache()
    {
        using var handler = new TokenHandler(3600);
        using var http = new HttpClient(handler);
        var options = new GoogleDriveStorageOptions("client", "secret", Guid.NewGuid(), Guid.NewGuid(), "folder", Path.GetTempPath());
        var attempts = 0;
        var manager = new GoogleDriveTokenManager(http, options,
            _ => ValueTask.FromResult(new GoogleDriveCredential("initial")),
            (_, cancellationToken) =>
            {
                Assert.False(cancellationToken.CanBeCanceled);
                if (++attempts == 1) throw new IOException("Secret write failed.");
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<IOException>(() => manager.GetAccessTokenAsync(default).AsTask());
        Assert.Equal("access-2", await manager.GetAccessTokenAsync(default));
        Assert.Equal(2, handler.Requests);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task ShortLivedTokensAreNotCachedPastTheirActualLifetime()
    {
        using var handler = new TokenHandler(1);
        using var http = new HttpClient(handler);
        var options = new GoogleDriveStorageOptions("client", "secret", Guid.NewGuid(), Guid.NewGuid(), "folder", Path.GetTempPath());
        var manager = new GoogleDriveTokenManager(http, options,
            _ => ValueTask.FromResult(new GoogleDriveCredential("initial")), (_, _) => Task.CompletedTask);

        Assert.Equal("access-1", await manager.GetAccessTokenAsync(default));
        Assert.Equal("access-2", await manager.GetAccessTokenAsync(default));
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task TransientTokenFailuresStopAtTheAttemptLimitWithoutRevokingCredentials()
    {
        var requests = 0;
        var revocations = 0;
        using var handler = new CallbackHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        using var http = new HttpClient(handler);
        var manager = Manager(http, Options(), _ => { revocations++; return Task.CompletedTask; });

        var failure = await Assert.ThrowsAsync<StorageException>(() => manager.GetAccessTokenAsync(default).AsTask());

        Assert.Equal(StorageFailureKind.NetworkUnavailable, failure.Kind);
        Assert.Equal(3, requests);
        Assert.Equal(0, revocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryAfterDeltaAndDateNeverCauseAnEarlyRetryBeyondTheBudget(bool useDate)
    {
        var requests = 0;
        using var handler = new CallbackHandler((_, _) =>
        {
            requests++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = useDate
                ? new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(1))
                : new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var manager = Manager(http, Options(budget: TimeSpan.FromMilliseconds(100)));

        var failure = await Assert.ThrowsAsync<StorageException>(() => manager.GetAccessTokenAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(StorageFailureKind.NetworkUnavailable, failure.Kind);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task StalledHttpHasBothRequestTimeoutAndTotalRetryDeadline()
    {
        var requests = 0;
        using var handler = new CallbackHandler(async (_, token) =>
        {
            requests++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var manager = Manager(http, Options(attempts: 10, budget: TimeSpan.FromMilliseconds(150), timeout: TimeSpan.FromMilliseconds(50)));

        var failure = await Assert.ThrowsAsync<StorageException>(() => manager.GetAccessTokenAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(StorageFailureKind.NetworkUnavailable, failure.Kind);
        Assert.InRange(requests, 1, 10);
    }

    [Fact]
    public async Task CallerCancellationStopsARequestWithoutRetryingOrRevoking()
    {
        var requests = 0;
        using var handler = new CallbackHandler(async (_, token) =>
        {
            requests++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        var manager = Manager(http, Options());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.GetAccessTokenAsync(cancellation.Token).AsTask());

        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task CallerCancellationAlsoStopsRetryBackoff()
    {
        var requests = 0;
        using var cancellation = new CancellationTokenSource();
        using var handler = new CallbackHandler((_, _) =>
        {
            requests++;
            cancellation.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        using var http = new HttpClient(handler);
        var manager = Manager(http, Options());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.GetAccessTokenAsync(cancellation.Token).AsTask());

        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"error\":\"invalid_client\",\"error_description\":\"never-reflect-secret\"}")]
    public async Task MalformedOrClientAuthenticationErrorsAreSafeAndDoNotMarkTheGrantRevoked(string body)
    {
        var revocations = 0;
        using var handler = new CallbackHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));
        using var http = new HttpClient(handler);
        var manager = Manager(http, Options(), _ => { revocations++; return Task.CompletedTask; });

        var failure = await Assert.ThrowsAsync<StorageException>(() => manager.GetAccessTokenAsync(default).AsTask());

        Assert.Equal(StorageFailureKind.AuthenticationFailed, failure.Kind);
        Assert.DoesNotContain("never-reflect-secret", failure.Message);
        Assert.Equal(0, revocations);
    }

    [Fact]
    public async Task ARevocationPersistenceFailureIsNotRetriedAsANetworkRequest()
    {
        var requests = 0;
        using var handler = new CallbackHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = JsonContent.Create(new { error = "invalid_grant" })
            });
        });
        using var http = new HttpClient(handler);
        var manager = Manager(http, Options(), _ => throw new IOException("Persistence unavailable."));

        await Assert.ThrowsAsync<IOException>(() => manager.GetAccessTokenAsync(default).AsTask());

        Assert.Equal(1, requests);
    }

    private static GoogleDriveStorageOptions Options(int attempts = 3, TimeSpan? budget = null, TimeSpan? timeout = null) =>
        new("client", "secret", Guid.NewGuid(), Guid.NewGuid(), "folder", Path.GetTempPath(),
            maxRetryAttempts: attempts, maxRetryElapsed: budget ?? TimeSpan.FromSeconds(2), requestTimeout: timeout ?? TimeSpan.FromSeconds(1),
            retryBaseDelay: TimeSpan.FromMilliseconds(1), maxRetryDelay: TimeSpan.FromSeconds(1));

    private static GoogleDriveTokenManager Manager(HttpClient http, GoogleDriveStorageOptions options, Func<CancellationToken, Task>? revoked = null) =>
        new(http, options, _ => ValueTask.FromResult(new GoogleDriveCredential("initial")), onReauthorizationRequired: revoked);

    private sealed class CallbackHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class TokenHandler(int expiresIn) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { access_token = $"access-{++Requests}", refresh_token = "rotated", expires_in = expiresIn })
            });
    }
}
