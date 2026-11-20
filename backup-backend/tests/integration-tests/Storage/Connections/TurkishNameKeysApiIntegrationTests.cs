using System.Net;
using BackupPlatform.IntegrationTests.ManagedService;

namespace BackupPlatform.IntegrationTests.Storage.Connections;

/// <summary>Names with İ, ı and Ş are created, renamed and de-duplicated by one case-insensitive key on connections, plans and quota policies.</summary>
public sealed class TurkishNameKeysApiIntegrationTests(PostgreSqlFixture fixture) : IClassFixture<PostgreSqlFixture>
{
    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    [Theory]
    [InlineData("İstanbul NAS")]
    [InlineData("ŞİFRELİ ıığ")]
    public async Task A_storage_connection_with_Turkish_letters_in_its_name_is_created_and_its_case_variant_is_a_duplicate(string name)
    {
        await using var env = await StorageConnectionApiIntegrationTests.ApiWorld.CreateAsync(fixture);
        var tagged = $"{name} {Tag()}";
        object Body(string text) => new
        {
            providerType = "smb", name = text,
            configuration = new { host = "nas.example", share = "x", site = "o" }, secret = new { username = "u", password = "p" }
        };

        using var created = await env.Admin.SendAsync(HttpMethod.Post, env.TenantRoot + "/storage-connections", Body(tagged));
        using var duplicate = await env.Admin.SendAsync(HttpMethod.Post, env.TenantRoot + "/storage-connections", Body(tagged.Replace('İ', 'i').ToLowerInvariant()));

        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task A_plan_and_a_quota_policy_with_Turkish_letters_are_created_and_their_case_variants_are_duplicates()
    {
        await using var world = await AgentWorld.CreateAsync(fixture);
        var admin = await world.SignInAsync(await world.SeedUserAsync(platformAdmin: true));
        var tag = Tag();

        var plan = await ManagedServiceTestSupport.CallAsync(admin, HttpMethod.Post, "/api/platform/plans", new { name = $"İstanbul Şifreli {tag}", storageLimitBytes = 1_000_000_000L });
        var planDuplicate = await ManagedServiceTestSupport.CallAsync(admin, HttpMethod.Post, "/api/platform/plans", new { name = $"istanbul şifreli {tag}", storageLimitBytes = 1_000_000_000L });
        object Policy(string key, string name) => new
        {
            key, name, accountingMode = "LogicalArtifactBytes", enforcementMode = "BlockNewBackups",
            includedStorageScopes = new[] { new { kind = "all" } }, warningThresholdPercent = 80, criticalThresholdPercent = 90
        };
        var policy = await ManagedServiceTestSupport.CallAsync(admin, HttpMethod.Post, "/api/platform/quota-policies", Policy($"tr-{tag}", $"İlke IŞIK {tag}"));
        var policyDuplicate = await ManagedServiceTestSupport.CallAsync(admin, HttpMethod.Post, "/api/platform/quota-policies", Policy($"tr2-{tag}", $"ilke IŞIK {tag}"));

        Assert.Equal(HttpStatusCode.Created, plan.Status);
        Assert.Equal((HttpStatusCode.Conflict, "plan_name_taken"), (planDuplicate.Status, planDuplicate.Error));
        Assert.Equal(HttpStatusCode.Created, policy.Status);
        Assert.Equal(HttpStatusCode.Conflict, policyDuplicate.Status);
    }
}
