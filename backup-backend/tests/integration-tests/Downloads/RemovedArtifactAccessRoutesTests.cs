using System.Net;

namespace BackupPlatform.IntegrationTests;

public sealed class RemovedArtifactAccessRoutesTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task FormerRoutesReturnNotFound()
    {
        await using var scenario = await DownloadApiScenario.CreateAsync(fixture);
        var platform = await scenario.World.SignInAsync(await scenario.World.SeedUserAsync(platformAdmin: true));
        var root = "/api/platform/artifact-break-glass";
        var request = Guid.NewGuid();
        var download = Guid.NewGuid();
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Post, root), (HttpMethod.Get, root), (HttpMethod.Get, root + "/artifacts"),
            (HttpMethod.Get, $"{root}/{request}"), (HttpMethod.Post, $"{root}/{request}/approve"),
            (HttpMethod.Post, $"{root}/{request}/deny"), (HttpMethod.Post, $"{root}/{request}/download"),
            (HttpMethod.Get, $"{root}/{request}/downloads/{download}"),
            (HttpMethod.Get, $"{root}/{request}/downloads/{download}/content"),
            (HttpMethod.Post, $"{root}/{request}/downloads/{download}/content")
        })
        {
            using var response = await platform.SendAsync(method, path, new { });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var tenantResponse = await scenario.Admin.GetAsync(scenario.Root + "/artifact-break-glass");
        Assert.Equal(HttpStatusCode.NotFound, tenantResponse.StatusCode);
    }
}
