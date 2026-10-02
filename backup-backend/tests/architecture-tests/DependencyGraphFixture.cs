using System.Diagnostics;
using System.Text.Json;

namespace BackupPlatform.ArchitectureTests;

public sealed class DependencyGraphFixture : IAsyncLifetime
{
    public Dictionary<string, Dictionary<string, ProjectNode>> Graphs { get; } = new(StringComparer.Ordinal);

    public async Task InitializeAsync()
    {
        var root = FindSolutionDirectory();
        var projects = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var graph = new Dictionary<string, ProjectNode>(StringComparer.Ordinal);
            foreach (var project in projects)
            {
                var node = await EvaluateProjectAsync(root, project, configuration);
                graph.Add(node.Name, node);
            }
            foreach (var node in graph.Values)
            {
                foreach (var dependency in node.References)
                {
                    Assert.True(graph.ContainsKey(dependency), $"{node.Name} references unknown project {dependency} ({configuration}).");
                }
            }
            Graphs.Add(configuration, graph);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public static HashSet<string> GetDependencies(Dictionary<string, ProjectNode> graph, string source)
    {
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(graph[source].References);
        while (pending.TryPop(out var dependency))
        {
            if (!dependencies.Add(dependency))
            {
                continue;
            }
            foreach (var reference in graph[dependency].References)
            {
                pending.Push(reference);
            }
        }
        return dependencies;
    }

    private static string FindSolutionDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BackupPlatform.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Architecture tests require the checkout containing BackupPlatform.sln.");
    }

    private static async Task<ProjectNode> EvaluateProjectAsync(string root, string project, string configuration)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("msbuild");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add("-nologo");
        startInfo.ArgumentList.Add("-getItem:ProjectReference,PackageReference");
        startInfo.ArgumentList.Add("-getProperty:ProjectAssetsFile");
        startInfo.ArgumentList.Add($"-p:Configuration={configuration}");
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start dotnet msbuild.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"MSBuild evaluation timed out for {project}.");
        }
        var output = await outputTask;
        var error = await errorTask;
        Assert.True(process.ExitCode == 0, $"MSBuild evaluation failed for {project}: {output}{error}");
        using var document = JsonDocument.Parse(output);
        var references = document.RootElement.GetProperty("Items").GetProperty("ProjectReference")
            .EnumerateArray()
            .Select(item => Path.GetFileNameWithoutExtension(item.GetProperty("FullPath").GetString()
                ?? throw new InvalidDataException("ProjectReference has no FullPath.")))
            .ToArray();
        var assetsPath = document.RootElement.GetProperty("Properties").GetProperty("ProjectAssetsFile").GetString()
            ?? throw new InvalidDataException("ProjectAssetsFile was not evaluated.");
        if (!Path.IsPathRooted(assetsPath))
        {
            assetsPath = Path.Combine(Path.GetDirectoryName(project)!, assetsPath);
        }
        using var assets = JsonDocument.Parse(await File.ReadAllTextAsync(assetsPath));
        var packages = document.RootElement.GetProperty("Items").GetProperty("PackageReference")
            .EnumerateArray()
            .Select(item => item.GetProperty("Identity").GetString()!)
            .Concat(assets.RootElement.GetProperty("libraries").EnumerateObject()
                .Where(library => library.Value.GetProperty("type").GetString() == "package")
                .Select(library => library.Name.Split('/')[0]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ProjectNode(Path.GetFileNameWithoutExtension(project), Path.GetRelativePath(root, project).Replace('\\', '/'), references, packages);
    }
}

public sealed record ProjectNode(string Name, string RelativePath, string[] References, string[] Packages);
