namespace BackupPlatform.ArchitectureTests;

/// <summary>
/// Notifications observe the platform; the platform never calls them. Backup and restore execution, the agent side, the other modules and the providers
/// know nothing about notification delivery, so an SMTP, webhook or SMS failure has no code path into them. The notifications module in turn knows no
/// other feature module: detectors that read platform facts live in infrastructure.
/// </summary>
public sealed class NotificationsBoundaryTests(DependencyGraphFixture fixture) : IClassFixture<DependencyGraphFixture>
{
    private const string Notifications = "BackupPlatform.Notifications";

    [Theory]
    [InlineData("BackupPlatform.Domain")]
    [InlineData("BackupPlatform.Application")]
    [InlineData("BackupPlatform.Contracts")]
    [InlineData("BackupPlatform.Agents")]
    [InlineData("BackupPlatform.Identity")]
    [InlineData("BackupPlatform.Tenancy")]
    [InlineData("BackupPlatform.ManagedService")]
    [InlineData("BackupPlatform.Storage")]
    [InlineData("BackupPlatform.SecretProviders.Abstractions")]
    [InlineData("BackupPlatform.SecretProviders.DataProtection")]
    [InlineData("BackupPlatform.StorageProviders.Abstractions")]
    [InlineData("BackupPlatform.StorageProviders.Local")]
    [InlineData("BackupPlatform.StorageProviders.Central")]
    [InlineData("BackupPlatform.StorageProviders.Sftp")]
    [InlineData("BackupPlatform.StorageProviders.GoogleDrive")]
    [InlineData("BackupPlatform.BackupProviders.Abstractions")]
    [InlineData("BackupPlatform.BackupProviders.MSSQL")]
    [InlineData("BackupPlatform.BackupProviders.FileSystem")]
    public void Features_and_providers_do_not_depend_on_notifications(string project) => AssertNotDependingOn(project, Notifications);

    [Fact]
    public void Agent_side_projects_do_not_depend_on_notifications()
    {
        foreach (var (_, graph) in fixture.Graphs)
        {
            foreach (var node in graph.Values.Where(node => node.RelativePath.StartsWith("src/agent/", StringComparison.Ordinal) || node.RelativePath.StartsWith("src/hosts/agent/", StringComparison.Ordinal)))
                Assert.DoesNotContain(Notifications, DependencyGraphFixture.GetDependencies(graph, node.Name));
        }
    }

    [Theory]
    [InlineData("BackupPlatform.Agents")]
    [InlineData("BackupPlatform.Identity")]
    [InlineData("BackupPlatform.Tenancy")]
    [InlineData("BackupPlatform.ManagedService")]
    [InlineData("BackupPlatform.Storage")]
    [InlineData("BackupPlatform.Infrastructure")]
    [InlineData("BackupPlatform.Api")]
    [InlineData("BackupPlatform.StorageProviders.Sftp")]
    [InlineData("BackupPlatform.BackupProviders.Abstractions")]
    public void The_notifications_module_knows_no_other_feature_infrastructure_or_provider(string forbidden) => AssertNotDependingOn(Notifications, forbidden);

    private void AssertNotDependingOn(string project, string forbidden)
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            Assert.True(graph.ContainsKey(project), $"Unknown project {project} ({configuration}).");
            Assert.False(DependencyGraphFixture.GetDependencies(graph, project).Contains(forbidden), $"{project} depends on {forbidden} ({configuration}).");
        }
    }
}
