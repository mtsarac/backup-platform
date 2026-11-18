using System.Net;
using System.Text;
using System.Text.Json;

namespace BackupPlatform.IntegrationTests;

public sealed class AgentDistributionApiTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task Setup_wizard_is_package_not_found_until_published_then_carries_the_server_address()
    {
        var root = Path.Combine(Path.GetTempPath(), "bp-dist-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var world = await AgentWorld.CreateAsync(fixture, configuration: new Dictionary<string, string?>
            {
                ["AgentDistribution:PackagesDirectory"] = root,
                ["AgentDistribution:PublicBaseUrl"] = "https://backup.example.com"
            });

            async Task<bool> PublishedAsync()
            {
                using var status = JsonDocument.Parse(await world.Anonymous.GetStringAsync("/api/agent-dist/status"));
                return status.RootElement.GetProperty("packages").GetProperty("win-x64-setup").GetBoolean();
            }

            Assert.False(await PublishedAsync());
            using var missing = await world.Anonymous.GetAsync("/api/agent-dist/packages/win-x64-setup");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Contains("package_not_found", await missing.Content.ReadAsStringAsync());

            await File.WriteAllBytesAsync(Path.Combine(root, "backup-agent-win-x64-setup.exe"), "MZ-setup"u8.ToArray());

            Assert.True(await PublishedAsync());
            var body = await world.Anonymous.GetByteArrayAsync("/api/agent-dist/packages/win-x64-setup");
            Assert.Equal("MZ-setup\nBackupPlatformServer=https://backup.example.com\n", Encoding.UTF8.GetString(body));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
