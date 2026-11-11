using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace BackupPlatform.ArchitectureTests;

public sealed class CentralAuthorizationBoundaryTests(DependencyGraphFixture fixture) : IClassFixture<DependencyGraphFixture>
{
    private const string ApiProject = "BackupPlatform.Api";
    private const string TenantMembershipType = "BackupPlatform.Domain.Tenancy.TenantMembership";

    [Fact]
    public void ApiHostDoesNotReferenceTenantMembershipDirectly()
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            if (configuration != "Release") continue;
            var node = graph[ApiProject];
            Assert.DoesNotContain(TenantMembershipType, node.Types);
        }
    }

    [Fact]
    public void InfrastructureStoresDoNotScatterMembershipQueriesForAuthorization()
    {
        var allowedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PostgreSqlCentralAuthorizationService.cs",
            "PostgreSqlPlatformAdministrationStore.cs",
            "PostgreSqlUserAccessStore.cs",
            "PlatformDbContext.cs",
            "TenantMembershipConfiguration.cs",
            "PlatformDbContextModelSnapshot.cs"
        };

        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/core/infrastructure"));
        if (!Directory.Exists(root)) return;

        foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.StartsWith("2026", StringComparison.Ordinal) || allowedFiles.Contains(fileName)) continue;

            var content = File.ReadAllText(file);
            Assert.False(content.Contains("TenantMemberships", StringComparison.Ordinal),
                $"File {fileName} queries TenantMemberships directly instead of using ICentralAuthorizationService.");
        }
    }
}
