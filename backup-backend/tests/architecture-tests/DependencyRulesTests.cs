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
    public void ApplicationMustNotDependOnBackupOrStorageImplementations() =>
        AssertForbiddenDependencies("BackupPlatform.Application", IsProviderImplementation);

    [Fact]
    public void DomainMustNotDependOnTransportOrContracts() =>
        AssertForbiddenDependencies("BackupPlatform.Domain", node =>
            node.Name == "BackupPlatform.Contracts" || node.Name == "BackupPlatform.Agent.Transport");

    [Fact]
    public void ApiAndSignalRHubsMustNotReachBackupProviderImplementations() =>
        AssertForbiddenDependencies("BackupPlatform.Api", node => IsProviderImplementation(node)
            && node.RelativePath.StartsWith("src/providers/backup/", StringComparison.Ordinal));

    [Fact]
    public void AgentCoreMustNotDependOnPlatformImplementations()
    {
        AssertForbiddenDependencies("BackupPlatform.Agent.Core", node => node.Name.StartsWith("BackupPlatform.Agent.Platform.", StringComparison.Ordinal));
    }

    [Fact]
    public void AgentCoreMustNotDependOnAspNetOrPersistence() =>
        AssertForbiddenExternalDependencies("BackupPlatform.Agent.Core", IsWebDependency, IsPersistenceDependency);

    [Fact]
    public void DomainMustNotDependOnCryptographyOrPersistence() =>
        AssertForbiddenExternalDependencies("BackupPlatform.Domain", IsCryptographyDependency, IsPersistenceDependency);

    [Fact]
    public void ContractsMustNotDependOnBackendInternals()
    {
        AssertForbiddenDependencies("BackupPlatform.Contracts", node => IsBackendInternal(node));
        // The one cryptographic primitive contracts may use is SHA-256, for AgentCapabilityRevision: the agent and the backend
        // must compute the same revision with the same code, and the contracts assembly is the only one both reference.
        AssertForbiddenExternalDependencies("BackupPlatform.Contracts",
            name => IsCryptographyDependency(name) && name is not ("System.Security.Cryptography" or "System.Security.Cryptography.SHA256"),
            IsPersistenceDependency);
    }

    [Fact]
    public void AgentSideProjectsMustNotDependOnBackendInternals()
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            foreach (var agentSide in graph.Values.Where(node =>
                node.RelativePath.StartsWith("src/agent/", StringComparison.Ordinal)
                || node.RelativePath.StartsWith("src/hosts/agent/", StringComparison.Ordinal)))
            {
                AssertForbiddenDependencies(configuration, graph, agentSide.Name, IsBackendInternal);
            }
        }
    }

    [Fact]
    public void AgentPlatformImplementationsMustNotDependOnWebOrPersistence()
    {
        AssertForbiddenExternalDependencies("BackupPlatform.Agent.Platform.Linux", IsWebDependency, IsPersistenceDependency);
        AssertForbiddenExternalDependencies("BackupPlatform.Agent.Platform.Windows", IsWebDependency, IsPersistenceDependency);
    }

    [Fact]
    public void AgentAuthenticationAndUserAuthenticationMustNotDependOnEachOther()
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            AssertForbiddenDependencies(configuration, graph, "BackupPlatform.Agents", node => node.Name == "BackupPlatform.Identity");
            AssertForbiddenDependencies(configuration, graph, "BackupPlatform.Identity", node => node.Name == "BackupPlatform.Agents");
        }
        AssertForbiddenExternalDependencies("BackupPlatform.Agents", IsIdentityDependency, IsWebDependency, IsPersistenceDependency);
        AssertForbiddenExternalDependencies("BackupPlatform.Identity", type => type.StartsWith("BackupPlatform.Agents", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("BackupPlatform.Agent.Core")]
    [InlineData("BackupPlatform.Domain")]
    [InlineData("BackupPlatform.Contracts")]
    [InlineData("BackupPlatform.Application")]
    [InlineData("BackupPlatform.Agents")]
    [InlineData("BackupPlatform.Agent.Platform.Linux")]
    [InlineData("BackupPlatform.Agent.Platform.Windows")]
    public void TransportNeutralProjectsMustNotDependOnSignalR(string project) =>
        AssertForbiddenExternalDependencies(project, IsSignalRDependency);

    [Fact]
    public void AgentCoreConnectivityMustNotKnowHowBytesTravel() =>
        AssertForbiddenExternalDependencies("BackupPlatform.Agent.Core", IsNetworkTransportDependency, IsWebDependency, IsPersistenceDependency);

    [Fact]
    public void AgentsModuleMustNotKnowAnyPushTransport() =>
        AssertForbiddenExternalDependencies("BackupPlatform.Agents", IsNetworkTransportDependency, IsSignalRDependency);

    [Fact]
    public void AgentTransportMayUseSignalRClientButNeverBackendOrPersistence()
    {
        AssertForbiddenExternalDependencies("BackupPlatform.Agent.Transport", IsPersistenceDependency, IsIdentityDependency);
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            AssertForbiddenDependencies(configuration, graph, "BackupPlatform.Agent.Transport", IsBackendInternal);
        }
    }

    private static bool IsSignalRDependency(string name) =>
        name.StartsWith("Microsoft.AspNetCore.SignalR", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.AspNetCore.Http.Connections", StringComparison.OrdinalIgnoreCase);

    private static bool IsNetworkTransportDependency(string name) =>
        name.StartsWith("System.Net.Http", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("System.Net.Sockets", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("System.Net.WebSockets", StringComparison.OrdinalIgnoreCase);

    private static bool IsBackendInternal(ProjectNode node) =>
        node.RelativePath.StartsWith("src/modules/", StringComparison.Ordinal)
        || node.RelativePath.StartsWith("src/hosts/api/", StringComparison.Ordinal)
        || node.Name is "BackupPlatform.Infrastructure" or "BackupPlatform.Application" or "BackupPlatform.Domain";

    private static bool IsPersistenceDependency(string name) =>
        name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase);

    private static bool IsCryptographyDependency(string name) =>
        name.StartsWith("System.Security.Cryptography", StringComparison.OrdinalIgnoreCase);

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

    [Theory]
    [InlineData("BackupPlatform.Domain")]
    [InlineData("BackupPlatform.Application")]
    [InlineData("BackupPlatform.Contracts")]
    [InlineData("BackupPlatform.Agent.Core")]
    [InlineData("BackupPlatform.BackupProviders.Abstractions")]
    [InlineData("BackupPlatform.BackupProviders.FileSystem")]
    public void CoreAndNeutralProjectsMustNotDependOnSqlClient(string project) =>
        AssertForbiddenExternalDependencies(project, IsSqlClientDependency);

    [Fact]
    public void OnlyTheMssqlProviderAndHostCompositionRootsMayReachSqlClient()
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            foreach (var node in graph.Values.Where(node =>
                node.Name != "BackupPlatform.BackupProviders.MSSQL"
                && !node.RelativePath.StartsWith("src/hosts/", StringComparison.Ordinal)))
            {
                var violations = node.Packages.Where(IsSqlClientDependency).ToArray();
                Assert.True(violations.Length == 0,
                    $"{node.Name} reaches the SQL Server client outside the MSSQL provider ({configuration}): {string.Join(", ", violations)}.");
            }
        }
    }

    [Fact]
    public void NoProjectOtherThanTheMssqlProviderMayCompileAgainstSqlClient()
    {
        var release = fixture.Graphs["Release"];
        foreach (var node in release.Values.Where(node => node.Name != "BackupPlatform.BackupProviders.MSSQL"))
        {
            var violations = node.Types.Concat(node.Assemblies).Where(IsSqlClientDependency).Distinct(StringComparer.Ordinal).ToArray();
            Assert.True(violations.Length == 0,
                $"{node.Name} compiles directly against the SQL Server client: {string.Join(", ", violations)}.");
        }
    }

    [Fact]
    public void MssqlProviderMayOnlyDependOnTheBackupProviderAbstractions()
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            AssertForbiddenDependencies(configuration, graph, "BackupPlatform.BackupProviders.MSSQL",
                node => node.Name != "BackupPlatform.BackupProviders.Abstractions");
        }
    }

    [Fact]
    public void MssqlProviderMustNotDependOnStorageProviderImplementations() =>
        AssertForbiddenDependencies("BackupPlatform.BackupProviders.MSSQL", node =>
            node.RelativePath.StartsWith("src/providers/storage/", StringComparison.Ordinal));

    [Fact]
    public void FileSystemProviderMustNotDependOnTheMssqlProvider() =>
        AssertForbiddenDependencies("BackupPlatform.BackupProviders.FileSystem", node => node.Name == "BackupPlatform.BackupProviders.MSSQL");

    private static bool IsSqlClientDependency(string name) =>
        name.StartsWith("Microsoft.Data.SqlClient", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("System.Data.SqlClient", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.SqlServer", StringComparison.OrdinalIgnoreCase);

    // ---- scheduling: Quartz is wake-up infrastructure at the Infrastructure/host boundary; cron parsing lives in one domain type ----

    [Theory]
    [InlineData("BackupPlatform.Domain")]
    [InlineData("BackupPlatform.Application")]
    [InlineData("BackupPlatform.Contracts")]
    [InlineData("BackupPlatform.Agent.Core")]
    [InlineData("BackupPlatform.Agent.Transport")]
    [InlineData("BackupPlatform.Agent.Platform.Linux")]
    [InlineData("BackupPlatform.Agent.Platform.Windows")]
    [InlineData("BackupPlatform.BackupProviders.Abstractions")]
    [InlineData("BackupPlatform.BackupProviders.MSSQL")]
    [InlineData("BackupPlatform.BackupProviders.FileSystem")]
    [InlineData("BackupPlatform.StorageProviders.Abstractions")]
    [InlineData("BackupPlatform.StorageProviders.Local")]
    [InlineData("BackupPlatform.StorageProviders.Central")]
    [InlineData("BackupPlatform.Agent.Host")]
    public void OnlyTheBackendInfrastructureAndHostMayDependOnTheSchedulerLibrary(string project) =>
        AssertForbiddenExternalDependencies(project, IsSchedulerLibraryDependency);

    [Fact]
    public void NoProjectOtherThanInfrastructureMayCompileAgainstTheSchedulerLibrary()
    {
        var release = fixture.Graphs["Release"];
        foreach (var node in release.Values.Where(node => node.Name != "BackupPlatform.Infrastructure"))
        {
            var violations = node.Types.Concat(node.Assemblies).Where(IsSchedulerLibraryDependency).Distinct(StringComparer.Ordinal).ToArray();
            Assert.True(violations.Length == 0, $"{node.Name} compiles directly against the scheduler library: {string.Join(", ", violations)}.");
        }
    }

    [Fact]
    public void OnlyTheDomainMayParseCronExpressions()
    {
        var release = fixture.Graphs["Release"];
        foreach (var node in release.Values.Where(node => node.Name != "BackupPlatform.Domain"))
        {
            var violations = node.Types.Concat(node.Assemblies).Where(IsCronParserDependency).Distinct(StringComparer.Ordinal).ToArray();
            Assert.True(violations.Length == 0, $"{node.Name} compiles directly against the cron parser: {string.Join(", ", violations)}.");
        }
    }

    [Fact]
    public void TheSchedulerInfrastructureMustNotReachBackupOrStorageProviderImplementations() =>
        AssertForbiddenDependencies("BackupPlatform.Infrastructure", IsProviderImplementation);

    private static bool IsSchedulerLibraryDependency(string name) =>
        name.Equals("Quartz", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Quartz.", StringComparison.OrdinalIgnoreCase);

    private static bool IsCronParserDependency(string name) =>
        name.Equals("Cronos", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Cronos.", StringComparison.OrdinalIgnoreCase);

    // ---- managed service (plans, subscriptions): a commercial module that backup execution, providers and agents never know ----

    private const string ManagedServiceProject = "BackupPlatform.ManagedService";

    [Fact]
    public void AgentSideProjectsMustNotDependOnManagedService()
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            foreach (var agentSide in graph.Values.Where(node =>
                node.RelativePath.StartsWith("src/agent/", StringComparison.Ordinal)
                || node.RelativePath.StartsWith("src/hosts/agent/", StringComparison.Ordinal)))
            {
                AssertForbiddenDependencies(configuration, graph, agentSide.Name, node => node.Name == ManagedServiceProject);
            }
        }
    }

    [Fact]
    public void StorageProvidersMustNotDependOnManagedService()
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            foreach (var provider in graph.Values.Where(node => node.RelativePath.StartsWith("src/providers/storage/", StringComparison.Ordinal)))
            {
                AssertForbiddenDependencies(configuration, graph, provider.Name, node => node.Name == ManagedServiceProject);
            }
        }
    }

    [Fact]
    public void BackupProvidersIncludingMssqlMustNotDependOnManagedService()
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            foreach (var provider in graph.Values.Where(node => node.RelativePath.StartsWith("src/providers/backup/", StringComparison.Ordinal)))
            {
                AssertForbiddenDependencies(configuration, graph, provider.Name, node => node.Name == ManagedServiceProject);
            }
            AssertForbiddenDependencies(configuration, graph, "BackupPlatform.BackupProviders.MSSQL", node => node.Name == ManagedServiceProject);
        }
    }

    [Fact]
    public void ManagedServiceMustNotKnowProviderImplementationsAgentsOrOtherFeatureModules()
    {
        // It builds on the domain and application foundations only (whatever those pull in transitively, such as the storage
        // abstractions, is theirs). No provider implementation, Agent, host, infrastructure or other feature module is reachable.
        AssertForbiddenDependencies(ManagedServiceProject, node =>
            IsProviderImplementation(node)
            || node.RelativePath.StartsWith("src/agent/", StringComparison.Ordinal)
            || node.RelativePath.StartsWith("src/hosts/", StringComparison.Ordinal)
            || (node.RelativePath.StartsWith("src/modules/", StringComparison.Ordinal) && node.Name != ManagedServiceProject)
            || node.Name == "BackupPlatform.Infrastructure");
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            var direct = graph[ManagedServiceProject].References.Order(StringComparer.Ordinal).ToArray();
            Assert.True(direct.SequenceEqual(["BackupPlatform.Application", "BackupPlatform.Domain"]),
                $"{ManagedServiceProject} may only reference the domain and application foundations directly ({configuration}): {string.Join(", ", direct)}.");
        }
        AssertForbiddenExternalDependencies(ManagedServiceProject, IsWebDependency, IsPersistenceDependency, IsSqlClientDependency);
    }

    [Theory]
    [InlineData("BackupPlatform.Domain")]
    [InlineData("BackupPlatform.Application")]
    [InlineData("BackupPlatform.Contracts")]
    [InlineData("BackupPlatform.Agents")]
    [InlineData("BackupPlatform.Backups")]
    [InlineData("BackupPlatform.Scheduling")]
    [InlineData("BackupPlatform.Storage")]
    [InlineData("BackupPlatform.Identity")]
    [InlineData("BackupPlatform.Tenancy")]
    [InlineData("BackupPlatform.Audit")]
    [InlineData("BackupPlatform.Notifications")]
    public void BackupOrchestrationAndFeatureModulesMustNotDependOnManagedService(string project) =>
        AssertForbiddenDependencies(project, node => node.Name == ManagedServiceProject);

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

    [Fact]
    public void AgentCoreMustNotDependOnProviderImplementations() =>
        AssertForbiddenDependencies("BackupPlatform.Agent.Core", IsProviderImplementation);

    [Fact]
    public void OnlyHostsMayComposeProviderImplementations()
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            foreach (var node in graph.Values.Where(node =>
                !node.RelativePath.StartsWith("src/hosts/", StringComparison.Ordinal) && !IsProviderImplementation(node)))
            {
                AssertForbiddenDependencies(configuration, graph, node.Name, IsProviderImplementation);
            }
        }
    }

    [Fact]
    public void BackupAndStorageProviderAbstractionsMustBeIndependentOfEachOther()
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            Assert.True(graph["BackupPlatform.BackupProviders.Abstractions"].References.Length == 0, $"The backup provider abstractions must stand alone ({configuration}).");
            Assert.True(graph["BackupPlatform.StorageProviders.Abstractions"].References.Length == 0, $"The storage provider abstractions must stand alone ({configuration}).");
        }
    }

    [Theory]
    [InlineData("BackupPlatform.StorageProviders.Local")]
    [InlineData("BackupPlatform.StorageProviders.Central")]
    public void StorageProvidersMayOnlyDependOnTheStorageAbstractions(string project)
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            AssertForbiddenDependencies(configuration, graph, project, node => node.Name != "BackupPlatform.StorageProviders.Abstractions");
        }

        AssertForbiddenExternalDependencies(project, IsSqlClientDependency, IsPersistenceDependency, IsWebDependency);
    }

    [Fact]
    public void StorageAbstractionsMustNotKnowDatabasesOrPersistence() =>
        AssertForbiddenExternalDependencies("BackupPlatform.StorageProviders.Abstractions", IsSqlClientDependency, IsPersistenceDependency, IsWebDependency);

    [Fact]
    public void DomainAndContractsMustNotUseFileSystemOrSqlClientApis()
    {
        AssertForbiddenExternalDependencies("BackupPlatform.Domain", IsFileSystemApi, IsSqlClientDependency);
        AssertForbiddenExternalDependencies("BackupPlatform.Contracts", IsFileSystemApi, IsSqlClientDependency);
    }

    private static bool IsFileSystemApi(string name) =>
        name is "System.IO.File" or "System.IO.Directory" or "System.IO.FileInfo" or "System.IO.DirectoryInfo"
            or "System.IO.FileStream" or "System.IO.DriveInfo" or "System.IO.FileSystemInfo";

    [Theory]
    [InlineData("BackupPlatform.Agent.Core")]
    [InlineData("BackupPlatform.Agent.Transport")]
    [InlineData("BackupPlatform.Agent.Platform.Linux")]
    public void PlatformNeutralAndLinuxAgentProjectsMustNotUseWindowsApis(string project) =>
        AssertForbiddenExternalDependencies(project, IsDpapiApi, IsWindowsServiceApi, IsWindowsAccessControlApi);

    [Fact]
    public void OnlyTheWindowsPlatformProjectMayUseDpapi()
    {
        // Hosts compose the platform and therefore reach DPAPI through it; everything else must not know it.
        // SqlClient legitimately brings ProtectedData as a transitive package. Check what each assembly actually uses, not its
        // dependency's implementation; the Agent.Core/Transport/Linux rule above still forbids even that package dependency.
        foreach (var node in fixture.Graphs["Release"].Values.Where(node =>
            !node.RelativePath.StartsWith("src/hosts/", StringComparison.Ordinal) && node.Name != "BackupPlatform.Agent.Platform.Windows"))
        {
            var forbidden = node.Assemblies.Concat(node.Types).Where(IsDpapiApi).Distinct(StringComparer.Ordinal).ToArray();
            Assert.True(forbidden.Length == 0, $"{node.Name} directly uses DPAPI outside the Windows platform: {string.Join(", ", forbidden)}.");
        }
    }

    [Fact]
    public void OnlyTheAgentHostMayReachTheWindowsServiceManager()
    {
        foreach (var node in fixture.Graphs["Release"].Values.Where(node => !node.RelativePath.StartsWith("src/hosts/agent/", StringComparison.Ordinal)))
        {
            AssertForbiddenExternalDependencies(node.Name, IsWindowsServiceApi);
        }
    }

    [Fact]
    public void TheWindowsPlatformMustNotKnowTheGenericHostOrTheLinuxPlatform()
    {
        AssertForbiddenExternalDependencies("BackupPlatform.Agent.Platform.Windows", IsWindowsServiceApi, name => name.StartsWith("Microsoft.Extensions.Hosting", StringComparison.OrdinalIgnoreCase));
        AssertForbiddenDependencies("BackupPlatform.Agent.Platform.Windows", node => node.Name == "BackupPlatform.Agent.Platform.Linux");
        AssertForbiddenDependencies("BackupPlatform.Agent.Platform.Linux", node => node.Name == "BackupPlatform.Agent.Platform.Windows");
    }

    private static bool IsDpapiApi(string name) =>
        name.StartsWith("System.Security.Cryptography.ProtectedData", StringComparison.Ordinal)
        || name == "System.Security.Cryptography.DataProtectionScope";

    private static bool IsWindowsServiceApi(string name) =>
        name.StartsWith("Microsoft.Extensions.Hosting.WindowsServices", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("System.ServiceProcess", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.Extensions.Logging.EventLog", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowsAccessControlApi(string name) =>
        name is "System.Security.AccessControl.FileSecurity" or "System.Security.AccessControl.DirectorySecurity"
            or "System.Security.AccessControl.FileSystemAccessRule" or "System.IO.FileSystemAclExtensions"
            or "System.Security.Principal.WindowsIdentity" or "System.Security.Principal.WindowsPrincipal";
}
