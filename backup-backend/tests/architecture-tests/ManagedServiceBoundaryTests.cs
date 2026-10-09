using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace BackupPlatform.ArchitectureTests;

/// <summary>
/// The commercial layer (plans, subscriptions, quota) is a module that only the persistence/composition layers know. Backup orchestration, providers
/// and agents see at most <c>BackupPlatform.Application.Backups.IBackupAdmissionPolicy</c>.
/// </summary>
public sealed class ManagedServiceBoundaryTests(DependencyGraphFixture fixture) : IClassFixture<DependencyGraphFixture>
{
    private const string ManagedServiceProject = "BackupPlatform.ManagedService";
    private const string ManagedServiceNamespace = "BackupPlatform.ManagedService";
    private const string AdmissionPolicyType = "BackupPlatform.Application.Backups.IBackupAdmissionPolicy";

    private static readonly string[] CommercialTypes =
        ["QuotaPolicy", "QuotaScopeRule", "TenantStorageUsage", "QuotaReservation", "Plan", "TenantSubscription", "TenantEntitlement"];

    private static bool IsAgentSide(ProjectNode node) =>
        node.RelativePath.StartsWith("src/agent/", StringComparison.Ordinal)
        || node.RelativePath.StartsWith("src/hosts/agent/", StringComparison.Ordinal);

    private static bool IsProvider(ProjectNode node) =>
        node.RelativePath.StartsWith("src/providers/storage/", StringComparison.Ordinal)
        || node.RelativePath.StartsWith("src/providers/backup/", StringComparison.Ordinal)
        || node.RelativePath.StartsWith("src/providers/secret/", StringComparison.Ordinal);

    [Theory]
    [InlineData("BackupPlatform.Agent.Core")]
    [InlineData("BackupPlatform.Agent.Transport")]
    [InlineData("BackupPlatform.Agent.Host")]
    [InlineData("BackupPlatform.Agent.Platform.Windows")]
    [InlineData("BackupPlatform.Agent.Platform.Linux")]
    public void AgentProjectsMustNotDependOnManagedService(string project) => AssertNoManagedServiceDependency(project);

    [Fact]
    public void EveryAgentSideProjectIsCoveredByTheBoundary()
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            foreach (var node in graph.Values.Where(IsAgentSide))
            {
                AssertNoManagedServiceDependency(configuration, graph, node.Name);
            }
        }
    }

    [Theory]
    [InlineData("BackupPlatform.StorageProviders.Abstractions")]
    [InlineData("BackupPlatform.StorageProviders.Local")]
    [InlineData("BackupPlatform.StorageProviders.Central")]
    [InlineData("BackupPlatform.BackupProviders.Abstractions")]
    [InlineData("BackupPlatform.BackupProviders.MSSQL")]
    [InlineData("BackupPlatform.BackupProviders.FileSystem")]
    [InlineData("BackupPlatform.SecretProviders.Abstractions")]
    [InlineData("BackupPlatform.SecretProviders.DataProtection")]
    public void ProviderProjectsMustNotDependOnManagedService(string project) => AssertNoManagedServiceDependency(project);

    [Fact]
    public void EveryProviderProjectIsCoveredByTheBoundary()
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            foreach (var node in graph.Values.Where(IsProvider))
            {
                AssertNoManagedServiceDependency(configuration, graph, node.Name);
            }
        }
    }

    [Fact]
    public void ProviderAssembliesMustNotReferenceCommercialTypes()
    {
        var release = fixture.Graphs["Release"];
        var commercial = CommercialTypes.Select(name => $"{ManagedServiceNamespace}.{name}").ToHashSet(StringComparer.Ordinal);
        foreach (var provider in release.Values.Where(IsProvider))
        {
            foreach (var node in ReachableAssemblies(release, provider.Name))
            {
                var violations = node.Types
                    .Where(type => commercial.Contains(type) || IsManagedServiceNamespace(type))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                Assert.True(violations.Length == 0,
                    $"{provider.Name} references commercial-layer types through {node.Name} (Release): {string.Join(", ", violations)}.");
            }
        }
    }

    [Theory]
    [InlineData("BackupPlatform.Domain")]
    [InlineData("BackupPlatform.Application")]
    [InlineData("BackupPlatform.Contracts")]
    public void FoundationProjectsMustNotDependOnManagedService(string project) => AssertNoManagedServiceDependency(project);

    [Fact]
    public void BackupOrchestrationTypesInApplicationMustNotReferenceManagedServiceTypes()
    {
        var application = fixture.Graphs["Release"]["BackupPlatform.Application"];
        var violations = ManagedServiceMetadata.TypeMemberReferences(application.AssemblyPath, "BackupPlatform.Application.Backups")
            .Where(reference => IsManagedServiceNamespace(reference.Referenced))
            .Select(reference => $"{reference.Owner}.{reference.Member} -> {reference.Referenced}")
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(violations.Length == 0,
            $"Backup orchestration types in {application.Name} reference ManagedService types (Release): {string.Join("; ", violations)}.");
        Assert.DoesNotContain(application.Types, IsManagedServiceNamespace);
    }

    [Fact]
    public void AdmissionPolicyIsDefinedByApplicationBackups()
    {
        var application = fixture.Graphs["Release"]["BackupPlatform.Application"];
        var definedTypes = ManagedServiceMetadata.DefinedTypes(application.AssemblyPath);
        Assert.True(definedTypes.Contains(AdmissionPolicyType),
            $"{AdmissionPolicyType} must be defined in {application.Name}, not in the commercial module.");
        var managedService = fixture.Graphs["Release"][ManagedServiceProject];
        Assert.False(ManagedServiceMetadata.DefinedTypes(managedService.AssemblyPath).Contains(AdmissionPolicyType),
            $"{AdmissionPolicyType} must not be defined in {ManagedServiceProject}.");
    }

    [Theory]
    [InlineData("BackupPlatform.Infrastructure.Backups.PostgreSqlBackupRunStore")]
    [InlineData("BackupPlatform.Infrastructure.Backups.PostgreSqlBackupScheduleStore")]
    public void BackupStoresTakeTheAdmissionPolicyInterface(string store)
    {
        var infrastructure = fixture.Graphs["Release"]["BackupPlatform.Infrastructure"];
        var constructors = ManagedServiceMetadata.Constructors(infrastructure.AssemblyPath, store);
        Assert.True(constructors.Count > 0, $"{store} was not found in {infrastructure.Name} (Release).");
        Assert.True(constructors.Any(parameters => parameters.Contains(AdmissionPolicyType)),
            $"{store} must take {AdmissionPolicyType} in a constructor; found: {string.Join(" | ", constructors.Select(parameters => string.Join(", ", parameters)))}.");
    }

    [Fact]
    public void InfrastructureBackupTypesMustNotSignatureReferenceManagedServiceTypes()
    {
        var infrastructure = fixture.Graphs["Release"]["BackupPlatform.Infrastructure"];
        var references = ManagedServiceMetadata.TypeMemberReferences(infrastructure.AssemblyPath, "BackupPlatform.Infrastructure.Backups").ToArray();
        Assert.True(references.Length > 0, $"No members found in namespace BackupPlatform.Infrastructure.Backups of {infrastructure.Name}; the rule would be vacuous.");
        var violations = references
            .Where(reference => IsManagedServiceNamespace(reference.Referenced))
            .Select(reference => $"{reference.Owner}.{reference.Member} -> {reference.Referenced}")
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(violations.Length == 0,
            $"Types in BackupPlatform.Infrastructure.Backups expose or hold ManagedService types instead of {AdmissionPolicyType}: {string.Join("; ", violations)}.");
    }

    private static bool IsManagedServiceNamespace(string name) =>
        name.Contains(ManagedServiceNamespace + ".", StringComparison.Ordinal);

    private void AssertNoManagedServiceDependency(string project)
    {
        foreach (var (configuration, graph) in fixture.Graphs)
        {
            AssertNoManagedServiceDependency(configuration, graph, project);
        }
    }

    private static void AssertNoManagedServiceDependency(string configuration, Dictionary<string, ProjectNode> graph, string project)
    {
        Assert.True(graph.ContainsKey(project), $"Unknown project {project} ({configuration}).");
        var projects = DependencyGraphFixture.GetDependencies(graph, project).Where(name => name == ManagedServiceProject).ToArray();
        Assert.True(projects.Length == 0, $"{project} depends on project {ManagedServiceProject} ({configuration}).");
        if (configuration != "Release")
        {
            return;
        }
        foreach (var node in ReachableAssemblies(graph, project))
        {
            Assert.True(node.Name != ManagedServiceProject, $"{project} depends on assembly {ManagedServiceProject} (Release).");
            Assert.True(!node.Assemblies.Contains(ManagedServiceProject, StringComparer.Ordinal),
                $"{project} depends on assembly {ManagedServiceProject} through {node.Name} (Release).");
            var types = node.Types.Where(IsManagedServiceNamespace).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            Assert.True(types.Length == 0,
                $"{project} references ManagedService types through {node.Name} (Release): {string.Join(", ", types)}.");
        }
    }

    private static IEnumerable<ProjectNode> ReachableAssemblies(Dictionary<string, ProjectNode> release, string source)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.TryPop(out var name))
        {
            if (!seen.Add(name) || !release.TryGetValue(name, out var node))
            {
                continue;
            }
            yield return node;
            foreach (var reference in node.Assemblies)
            {
                pending.Push(reference);
            }
        }
    }
}

/// <summary>Reads compiled assemblies without loading them: member signatures are decoded from metadata so no dependency has to resolve.</summary>
internal static class ManagedServiceMetadata
{
    public sealed record MemberReference(string Owner, string Member, string Referenced);

    public static HashSet<string> DefinedTypes(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var names = new NameProvider(reader);
        return reader.TypeDefinitions.Select(names.DefinitionName).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Parameter type lists of every constructor of the named type.</summary>
    public static List<string[]> Constructors(string assemblyPath, string typeName)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var names = new NameProvider(reader);
        var result = new List<string[]>();
        foreach (var handle in reader.TypeDefinitions.Where(handle => names.DefinitionName(handle) == typeName))
        {
            foreach (var method in reader.GetTypeDefinition(handle).GetMethods().Select(reader.GetMethodDefinition))
            {
                if (reader.GetString(method.Name) == ".ctor")
                {
                    result.Add(method.DecodeSignature(names, null).ParameterTypes.ToArray());
                }
            }
        }
        return result;
    }

    /// <summary>Every type a member of the types under <paramref name="namespacePrefix"/> (nested types included) exposes: base, interfaces, fields, properties, method returns and parameters.</summary>
    public static IEnumerable<MemberReference> TypeMemberReferences(string assemblyPath, string namespacePrefix)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var names = new NameProvider(reader);
        var result = new List<MemberReference>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var owner = names.DefinitionName(handle);
            var outermost = owner.Split('+')[0];
            if (!outermost.StartsWith(namespacePrefix + ".", StringComparison.Ordinal))
            {
                continue;
            }
            var definition = reader.GetTypeDefinition(handle);
            if (!definition.BaseType.IsNil)
            {
                result.Add(new(owner, "<base>", names.Entity(definition.BaseType)));
            }
            foreach (var implementation in definition.GetInterfaceImplementations().Select(reader.GetInterfaceImplementation))
            {
                result.Add(new(owner, "<interface>", names.Entity(implementation.Interface)));
            }
            foreach (var field in definition.GetFields().Select(reader.GetFieldDefinition))
            {
                result.Add(new(owner, reader.GetString(field.Name), field.DecodeSignature(names, null)));
            }
            foreach (var property in definition.GetProperties().Select(reader.GetPropertyDefinition))
            {
                result.Add(new(owner, reader.GetString(property.Name), property.DecodeSignature(names, null).ReturnType));
            }
            foreach (var method in definition.GetMethods().Select(reader.GetMethodDefinition))
            {
                var signature = method.DecodeSignature(names, null);
                var member = reader.GetString(method.Name);
                result.Add(new(owner, $"{member}(return)", signature.ReturnType));
                foreach (var parameter in signature.ParameterTypes)
                {
                    result.Add(new(owner, $"{member}(parameter)", parameter));
                }
            }
        }
        return result;
    }

    private sealed class NameProvider(MetadataReader reader) : ISignatureTypeProvider<string, object?>
    {
        public string DefinitionName(TypeDefinitionHandle handle)
        {
            var definition = reader.GetTypeDefinition(handle);
            var name = reader.GetString(definition.Name);
            var declaring = definition.GetDeclaringType();
            if (!declaring.IsNil)
            {
                return $"{DefinitionName(declaring)}+{name}";
            }
            var @namespace = reader.GetString(definition.Namespace);
            return @namespace.Length == 0 ? name : $"{@namespace}.{name}";
        }

        public string Entity(EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => DefinitionName((TypeDefinitionHandle)handle),
            HandleKind.TypeReference => ReferenceName((TypeReferenceHandle)handle),
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null),
            _ => handle.Kind.ToString()
        };

        private string ReferenceName(TypeReferenceHandle handle)
        {
            var reference = reader.GetTypeReference(handle);
            var name = reader.GetString(reference.Name);
            if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                return $"{ReferenceName((TypeReferenceHandle)reference.ResolutionScope)}+{name}";
            }
            var @namespace = reader.GetString(reference.Namespace);
            return @namespace.Length == 0 ? name : $"{@namespace}.{name}";
        }

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetTypeFromDefinition(MetadataReader metadataReader, TypeDefinitionHandle handle, byte rawTypeKind) => DefinitionName(handle);

        public string GetTypeFromReference(MetadataReader metadataReader, TypeReferenceHandle handle, byte rawTypeKind) => ReferenceName(handle);

        public string GetTypeFromSpecification(MetadataReader metadataReader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            metadataReader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetSZArrayType(string elementType) => $"{elementType}[]";

        public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', Math.Max(shape.Rank - 1, 0))}]";

        public string GetByReferenceType(string elementType) => $"{elementType}&";

        public string GetPointerType(string elementType) => $"{elementType}*";

        public string GetPinnedType(string elementType) => elementType;

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            $"{genericType}<{string.Join(", ", typeArguments)}>";

        public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";

        public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";

        public string GetFunctionPointerType(MethodSignature<string> signature) =>
            $"fnptr({string.Join(", ", signature.ParameterTypes)}) -> {signature.ReturnType}";

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    }
}
