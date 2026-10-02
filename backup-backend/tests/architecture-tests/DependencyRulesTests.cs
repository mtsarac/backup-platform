namespace BackupPlatform.ArchitectureTests;

public sealed class DependencyRulesTests(DependencyGraphFixture fixture) : IClassFixture<DependencyGraphFixture>
{
    [Theory]
    [InlineData("BackupPlatform.Domain")]
    [InlineData("BackupPlatform.Contracts")]
    public void FoundationProjectsMustHaveNoProjectDependencies(string project)
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            Assert.True(graph[project].References.Length == 0, $"{project} must be independent ({configuration}).");
        }
    }

    [Theory]
    [InlineData("BackupPlatform.Domain")]
    [InlineData("BackupPlatform.Contracts")]
    public void FoundationProjectsMustNotDependOnPersistencePackages(string project)
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            var violations = graph[project].Packages
                .Where(package => package.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
                    || package.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.True(violations.Length == 0, $"{project} has forbidden direct/transitive persistence packages ({configuration}): {string.Join(", ", violations)}.");
        }
    }

    [Fact]
    public void DomainMustNotDependOnWebOrIdentityImplementations() =>
        AssertForbiddenExternalDependencies("BackupPlatform.Domain", IsWebDependency, IsIdentityDependency);

    [Fact]
    public void ApplicationMustNotDependOnAspNetOrHttpContext() =>
        AssertForbiddenExternalDependencies("BackupPlatform.Application", IsWebDependency);

    [Fact]
    public void ContractsMustNotContainAuthenticationImplementationDependencies()
    {
        AssertForbiddenDependencies("BackupPlatform.Contracts", node =>
            node.Name == "BackupPlatform.Identity" || node.Name == "BackupPlatform.Api");
        AssertForbiddenExternalDependencies("BackupPlatform.Contracts", IsWebDependency, IsIdentityDependency);
    }

    private void AssertForbiddenExternalDependencies(string source, params Func<string, bool>[] forbidden)
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            var nodes = DependencyGraphFixture.GetDependencies(graph, source)
                .Append(source).Select(name => graph[name]).ToArray();
            var violations = nodes.SelectMany(node => node.Packages.Concat(node.Frameworks))
                .Where(name => forbidden.Any(predicate => predicate(name)))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            Assert.True(violations.Length == 0,
                $"{source} has forbidden evaluated package/framework dependencies ({configuration}): {string.Join(", ", violations)}.");
        }
        var release = fixture.Graphs["Release"];
        var assemblies = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.TryPop(out var name))
        {
            if (!assemblies.Add(name))
            {
                continue;
            }
            if (!release.TryGetValue(name, out var node))
            {
                continue;
            }
            foreach (var reference in node.Assemblies)
            {
                pending.Push(reference);
            }
            var forbiddenTypes = node.Types.Where(type => forbidden.Any(predicate => predicate(type))).ToArray();
            Assert.True(forbiddenTypes.Length == 0,
                $"{source} has forbidden compiled type references through {name} (Release): {string.Join(", ", forbiddenTypes)}.");
        }
        var forbiddenAssemblies = assemblies.Where(name => forbidden.Any(predicate => predicate(name))).Order(StringComparer.Ordinal).ToArray();
        Assert.True(forbiddenAssemblies.Length == 0,
            $"{source} has forbidden compiled assembly dependencies (Release): {string.Join(", ", forbiddenAssemblies)}.");
    }

    private static bool IsWebDependency(string name) =>
        name.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("System.Web", StringComparison.OrdinalIgnoreCase);

    private static bool IsIdentityDependency(string name) =>
        name.StartsWith("Microsoft.Extensions.Identity", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.IdentityModel", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("System.IdentityModel", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("IdentityModel", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("BackupPlatform.Identity", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void DomainMustNotDependOnInfrastructure()
    {
        AssertForbiddenDependencies("BackupPlatform.Domain", node => node.Name == "BackupPlatform.Infrastructure");
    }

    [Fact]
    public void DomainMustNotDependOnProviderImplementations()
    {
        AssertForbiddenDependencies("BackupPlatform.Domain", IsProviderImplementation);
    }

    [Fact]
    public void ApplicationMustNotDependOnInfrastructure()
    {
        AssertForbiddenDependencies("BackupPlatform.Application", node => node.Name == "BackupPlatform.Infrastructure");
    }

    [Fact]
    public void AgentCoreMustNotDependOnPlatformImplementations()
    {
        AssertForbiddenDependencies("BackupPlatform.Agent.Core", node => node.Name.StartsWith("BackupPlatform.Agent.Platform.", StringComparison.Ordinal));
    }

    [Fact]
    public void BackendModulesMustNotDependOnProviderImplementations()
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            foreach (var module in graph.Values.Where(node => node.RelativePath.StartsWith("src/modules/", StringComparison.Ordinal)))
            {
                AssertForbiddenDependencies(configuration, graph, module.Name, IsProviderImplementation);
            }
        }
    }

    [Fact]
    public void BackendModulesMustNotDependOnInfrastructure()
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            foreach (var module in graph.Values.Where(node => node.RelativePath.StartsWith("src/modules/", StringComparison.Ordinal)))
            {
                AssertForbiddenDependencies(configuration, graph, module.Name, node => node.Name == "BackupPlatform.Infrastructure");
            }
        }
    }

    [Fact]
    public void ProviderImplementationsMustNotDependOnEachOther()
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            foreach (var provider in graph.Values.Where(IsProviderImplementation))
            {
                AssertForbiddenDependencies(configuration, graph, provider.Name, node => node.Name != provider.Name && IsProviderImplementation(node));
            }
        }
    }

    [Fact]
    public void ProjectDependenciesMustBeAcyclic()
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            foreach (var node in graph.Values)
            {
                Assert.False(DependencyGraphFixture.GetDependencies(graph, node.Name).Contains(node.Name), $"Dependency cycle involving {node.Name} ({configuration}).");
            }
        }
    }

    private void AssertForbiddenDependencies(string source, Func<ProjectNode, bool> forbidden)
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            AssertForbiddenDependencies(configuration, graph, source, forbidden);
        }
    }

    private static void AssertForbiddenDependencies(string configuration, Dictionary<string, ProjectNode> graph, string source, Func<ProjectNode, bool> forbidden)
    {
        var violations = DependencyGraphFixture.GetDependencies(graph, source)
            .Where(name => forbidden(graph[name]))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(violations.Length == 0, $"{source} has forbidden direct/transitive dependencies ({configuration}): {string.Join(", ", violations)}.");
    }

    private static bool IsProviderImplementation(ProjectNode node) =>
        node.RelativePath.StartsWith("src/providers/", StringComparison.Ordinal)
        && !node.Name.EndsWith(".Abstractions", StringComparison.Ordinal);
}
