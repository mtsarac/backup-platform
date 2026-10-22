using System.Net;
using BackupPlatform.Storage;
using BackupPlatform.Domain.Storage;

namespace BackupPlatform.IntegrationTests.Storage.Connections;

public sealed class NetworkStorageApiIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Theory]
    [InlineData("s3")]
    [InlineData("smb")]
    public async Task Tenant_can_manage_network_credentials_without_exposing_them_or_crossing_tenants(string provider)
    {
        await using var env = await StorageConnectionApiIntegrationTests.ApiWorld.CreateAsync(fixture);
        await using var foreign = await StorageConnectionApiIntegrationTests.ApiWorld.CreateAsync(fixture);
        var configuration = provider == "s3"
            ? new Dictionary<string, string> { ["endpoint"] = "https://objects.example", ["bucket"] = "backups", ["region"] = "us-east-1", ["site"] = "remote" }
            : new Dictionary<string, string> { ["host"] = "nas.example", ["share"] = "backups", ["site"] = "office" };
        var secret = provider == "s3"
            ? new Dictionary<string, string> { ["accessKey"] = "private-access-123", ["secretKey"] = "private-secret-456" }
            : new Dictionary<string, string> { ["username"] = "private-user-123", ["password"] = "private-secret-456" };
        using var created = await env.Admin.SendAsync(HttpMethod.Post, env.TenantRoot + "/storage-connections",
            new { providerType = provider, name = "Network backups", configuration, secret });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var view = AgentWorld.ReadJson(created);
        var id = view.GetProperty("id").GetGuid();
        Assert.True(view.GetProperty("hasSecret").GetBoolean());
        var responseText = await created.Content.ReadAsStringAsync();
        Assert.All(secret.Values, value => Assert.DoesNotContain(value, responseText));
        using var forbidden = await foreign.Admin.GetAsync(foreign.TenantRoot + $"/storage-connections/{id}");
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        secret[provider == "s3" ? "secretKey" : "password"] = "rotated-secret-789";
        using var rotated = await env.Admin.SendAsync(HttpMethod.Put, env.TenantRoot + $"/storage-connections/{id}/secret", new { values = secret });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.DoesNotContain("rotated-secret-789", await rotated.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WORM_configuration_requires_an_explicit_mode_and_positive_retention()
    {
        await using var env = await StorageConnectionApiIntegrationTests.ApiWorld.CreateAsync(fixture);
        using var response = await env.Admin.SendAsync(HttpMethod.Post, env.TenantRoot + "/storage-connections", new
        {
            providerType = "s3", name = "Invalid WORM",
            configuration = new { endpoint = "https://objects.example", bucket = "backups", region = "us-east-1", objectLockMode = "COMPLIANCE", retentionDays = "0" },
            secret = new { accessKey = "access", secretKey = "secret" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
