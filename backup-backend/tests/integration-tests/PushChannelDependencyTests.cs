using System.Reflection;

namespace BackupPlatform.IntegrationTests;

/// <summary>
/// The push channel must stay a transport: it may know the Agents module and API security, never a backup, storage,
/// snapshot or secret provider implementation. Checked on the compiled member signatures of every type in the
/// <c>BackupPlatform.Api.Push</c> namespace.
/// </summary>
public sealed class PushChannelDependencyTests
{
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "BackupPlatform.BackupProviders", "BackupPlatform.StorageProviders", "BackupPlatform.SnapshotProviders", "BackupPlatform.SecretProviders",
        "BackupPlatform.Backups", "BackupPlatform.Storage", "BackupPlatform.Scheduling"
    ];

    private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact]
    public void PushChannelTypesReferenceNoProviderOrBackupDomainTypes()
    {
        var pushTypes = typeof(BackupPlatform.Api.Push.AgentHub).Assembly.GetTypes()
            .Where(type => type.Namespace == "BackupPlatform.Api.Push")
            .ToArray();
        Assert.NotEmpty(pushTypes);
        var violations = pushTypes.SelectMany(type => ReferencedTypes(type).Where(IsForbidden).Select(reference => $"{type.Name} -> {reference.FullName}"))
            .Distinct().Order().ToArray();
        Assert.True(violations.Length == 0, "Forbidden push channel dependencies: " + string.Join(", ", violations));
    }

    private static bool IsForbidden(Type type) =>
        type.Namespace is { } ns && ForbiddenNamespacePrefixes.Any(prefix => ns.StartsWith(prefix, StringComparison.Ordinal));

    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        var references = new List<Type>();
        if (type.BaseType is not null)
        {
            references.Add(type.BaseType);
        }
        references.AddRange(type.GetInterfaces());
        foreach (var field in type.GetFields(AllMembers))
        {
            references.Add(field.FieldType);
        }
        foreach (var property in type.GetProperties(AllMembers))
        {
            references.Add(property.PropertyType);
        }
        foreach (var constructor in type.GetConstructors(AllMembers))
        {
            references.AddRange(constructor.GetParameters().Select(parameter => parameter.ParameterType));
        }
        foreach (var method in type.GetMethods(AllMembers))
        {
            references.Add(method.ReturnType);
            references.AddRange(method.GetParameters().Select(parameter => parameter.ParameterType));
        }
        return references.SelectMany(Flatten);
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;
        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var inner in Flatten(element))
            {
                yield return inner;
            }
        }
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments().SelectMany(Flatten))
            {
                yield return argument;
            }
        }
    }
}
