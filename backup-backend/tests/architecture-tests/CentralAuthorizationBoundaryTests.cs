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

    [Fact]
    public void RemovedTenantRolePoliciesDoNotReturn()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src"));
        Assert.True(Directory.Exists(root), $"Source directory not found: {root}");
        var removed = new[] { "AuthorizationPolicies.TenantAdministrator", "AuthorizationPolicies.TenantOperator",
            "AuthorizationPolicies.TenantViewer", "TenantRoleRequirement", "enum TenantRole" };
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            var content = File.ReadAllText(file);
            foreach (var identifier in removed)
                Assert.DoesNotContain(identifier, content, StringComparison.Ordinal);
        }
    }
    [Fact]
    public void RemovedRoleNamesDoNotAppearInProductionSource()
    {
        var backendRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src"));
        var frontendRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../backup-frontend/src"));

        var legacyRolePatterns = new[]
        {
            "TenantRole", "MembershipRole", "membershipRole",
            "AuthorizationPolicies.TenantAdministrator", "AuthorizationPolicies.TenantOperator", "AuthorizationPolicies.TenantViewer"
        };

        if (Directory.Exists(backendRoot))
        {
            foreach (var file in Directory.EnumerateFiles(backendRoot, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                var text = File.ReadAllText(file);
                foreach (var pattern in legacyRolePatterns)
                {
                    Assert.DoesNotContain(pattern, text, StringComparison.Ordinal);
                }
            }
        }

        if (Directory.Exists(frontendRoot))
        {
            foreach (var file in Directory.EnumerateFiles(frontendRoot, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file);
                if (ext is not (".ts" or ".tsx")) continue;
                if (file.Contains(".test.") || file.Contains(".spec.")) continue;

                var text = File.ReadAllText(file);
                foreach (var pattern in legacyRolePatterns)
                {
                    Assert.DoesNotContain(pattern, text, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void AllContentAuthorizationDecisionsAreMadeInOneComponent()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src"));
        Assert.True(Directory.Exists(root), $"Source directory not found: {root}");

        var interfaceFile = Path.Combine(root, "core", "application", "Tenancy", "ICentralAuthorizationService.cs");
        Assert.True(File.Exists(interfaceFile), $"Interface file missing: {interfaceFile}");
        var interfaceContent = File.ReadAllText(interfaceFile);
        Assert.Contains("EvaluateDownloadAccessAsync", interfaceContent, StringComparison.Ordinal);
        Assert.Contains("EvaluateAgentSessionAccessAsync", interfaceContent, StringComparison.Ordinal);
    }
}
