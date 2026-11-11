using System.Text.RegularExpressions;

namespace BackupPlatform.ArchitectureTests;

public sealed class RemovedArtifactAccessBoundaryTests
{
    [Fact]
    public void RemovedArtifactAccessIdentifiersDoNotAppearInSources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BackupPlatform.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var backend = directory.FullName;
        var frontend = Path.Combine(directory.Parent!.FullName, "backup-frontend", "src");
        var roots = new[] { Path.Combine(backend, "src"), frontend };
        var pattern = new Regex("break[-_]?glass", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var violations = roots.SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "Migrations" or "bin" or "obj" or "node_modules"))
            .Where(path => pattern.IsMatch(path) || pattern.IsMatch(File.ReadAllText(path)))
            .ToArray();
        Assert.True(violations.Length == 0, $"Removed artifact access identifiers remain in: {string.Join(", ", violations)}");
    }
}
