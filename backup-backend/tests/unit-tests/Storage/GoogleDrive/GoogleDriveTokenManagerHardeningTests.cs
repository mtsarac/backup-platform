using System.Net;
using System.Net.Http.Json;
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
