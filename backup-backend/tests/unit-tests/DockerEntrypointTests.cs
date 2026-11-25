using System.Diagnostics;

namespace BackupPlatform.UnitTests;

public sealed class DockerEntrypointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("entrypoint_").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string EntrypointPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "docker-entrypoint.sh");
            if (File.Exists(candidate) && Directory.Exists(Path.Combine(directory.FullName, "src"))) return candidate;
        }
        throw new InvalidOperationException("docker-entrypoint.sh was not found above the test binaries.");
    }

    private string Fake(string name, string body)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private async Task<(int Exit, string Output)> RunAsync(string bundleBody)
    {
        var bundle = Fake("efbundle", bundleBody);
        Fake("sleep", "exit 0");
        Fake("dotnet", "echo API_STARTED");
        var start = new ProcessStartInfo("sh", EntrypointPath()) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment["PATH"] = _dir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        start.Environment["EFBUNDLE"] = bundle;
        start.Environment["BACKUP_PLATFORM_CONNECTION_STRING"] = "Host=db";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output + await error);
    }

    [Fact]
    public async Task Failed_migrations_keep_the_api_from_starting()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (exit, output) = await RunAsync("exit 1");
        Assert.NotEqual(0, exit);
        Assert.DoesNotContain("API_STARTED", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migrations_that_succeed_after_a_retry_start_the_api()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (exit, output) = await RunAsync($"marker='{_dir}/tried'\nif [ -f \"$marker\" ]; then exit 0; fi\ntouch \"$marker\"\nexit 1");
        Assert.Equal(0, exit);
        Assert.Contains("API_STARTED", output, StringComparison.Ordinal);
    }
}
