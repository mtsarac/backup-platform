using System.Text.RegularExpressions;

namespace BackupPlatform.ArchitectureTests;

public sealed partial class TenantEndpointSeamTests
{
    private const string Seam = "hosts/api/Security/TenantEndpointRouteBuilderExtensions.cs";

    [GeneratedRegex(@"""[^""$]*/[^""]*\{tenantId[:}]")]
    private static partial Regex TenantRoute();

    private static IEnumerable<(string File, string Line)> ApiSourceLines()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/hosts/api"));
        Assert.True(Directory.Exists(root), $"Source directory not found: {root}");
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var relative = "hosts/api/" + Path.GetRelativePath(root, file).Replace('\\', '/');
            foreach (var line in File.ReadLines(file)) yield return (relative, line);
        }
    }

    [Fact]
    public void TenantRoutesAreRegisteredOnlyThroughTheTenantGroup()
    {
        // Platform admin routes address a tenant by id but resolve no membership; they sit under the platform group or /api/platform.
        var violations = ApiSourceLines()
            .Where(item => item.File != Seam && TenantRoute().IsMatch(item.Line)
                && !item.Line.TrimStart().StartsWith("platform.", StringComparison.Ordinal) && !item.Line.Contains("/api/platform/", StringComparison.Ordinal))
            .Select(item => $"{item.File}: {item.Line.Trim()}").ToArray();
        Assert.Empty(violations);
    }

    [Fact]
    public void TenantScopeMetadataIsAttachedOnlyByTheSeam()
    {
        var violations = ApiSourceLines()
            .Where(item => item.File != Seam && item.Line.Contains("new TenantScopedEndpoint()", StringComparison.Ordinal))
            .Select(item => item.File).Distinct().ToArray();
        Assert.Empty(violations);
    }
}
