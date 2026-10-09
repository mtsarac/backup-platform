namespace BackupPlatform.ArchitectureTests;

/// <summary>
/// Storage connections sit between providers (what a destination can do) and the commercial layer (what is counted): provider projects, agents and the
/// foundation know nothing about connection management, the connection module knows nothing about plans or quota, and quota reads connection facts
/// through persistence only. That is what lets a new provider be added without touching the quota engine.
/// </summary>
public sealed class StorageConnectionBoundaryTests(DependencyGraphFixture fixture) : IClassFixture<DependencyGraphFixture>
{
    private const string StorageModule = "BackupPlatform.Storage";
    private const string ManagedService = "BackupPlatform.ManagedService";

    [Fact]
    public void TheConnectionModuleDoesNotKnowPlansOrQuota() => AssertNotDependingOn(StorageModule, ManagedService);

    [Fact]
    public void TheQuotaModuleReadsConnectionFactsThroughPersistenceNotThroughTheConnectionModule() => AssertNotDependingOn(ManagedService, StorageModule);

    [Theory]
    [InlineData("BackupPlatform.StorageProviders.Abstractions")]
    [InlineData("BackupPlatform.StorageProviders.Local")]
    [InlineData("BackupPlatform.StorageProviders.Central")]
    [InlineData("BackupPlatform.BackupProviders.Abstractions")]
    [InlineData("BackupPlatform.SecretProviders.Abstractions")]
    [InlineData("BackupPlatform.SecretProviders.DataProtection")]
    [InlineData("BackupPlatform.Domain")]
    [InlineData("BackupPlatform.Application")]
    [InlineData("BackupPlatform.Contracts")]
    public void ProvidersAndTheFoundationDoNotDependOnConnectionManagement(string project) => AssertNotDependingOn(project, StorageModule);

    [Fact]
    public void AgentSideProjectsDoNotDependOnConnectionManagement()
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            foreach (var node in graph.Values.Where(node =>
                node.RelativePath.StartsWith("src/agent/", StringComparison.Ordinal) || node.RelativePath.StartsWith("src/hosts/agent/", StringComparison.Ordinal)))
            {
                Assert.DoesNotContain(StorageModule, DependencyGraphFixture.GetDependencies(graph, node.Name));
            }
        }
    }

    [Fact]
    public void ProviderProjectsDoNotReferenceTheDomain()
    {
        // A provider describes bytes and credentials; the tenant-owned StorageConnection entity is a Domain type they must never see.
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            foreach (var node in graph.Values.Where(node => node.RelativePath.StartsWith("src/providers/storage/", StringComparison.Ordinal) || node.RelativePath.StartsWith("src/providers/secret/", StringComparison.Ordinal)))
            {
                Assert.DoesNotContain("BackupPlatform.Domain", DependencyGraphFixture.GetDependencies(graph, node.Name));
            }
        }
    }

    private void AssertNotDependingOn(string project, string forbidden)
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            Assert.True(graph.ContainsKey(project), $"Unknown project {project} ({configuration}).");
            Assert.False(DependencyGraphFixture.GetDependencies(graph, project).Contains(forbidden), $"{project} depends on {forbidden} ({configuration}).");
        }
    }
}
