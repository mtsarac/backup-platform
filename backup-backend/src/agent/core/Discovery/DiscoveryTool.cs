using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace BackupPlatform.Agent.Core.Discovery;

internal enum ToolOutcome
{
    Completed,
    NotFound,
    AccessDenied,
    TimedOut,
    Failed,
}

/// <summary>The captured result of a read-only helper tool. <see cref="StdErr"/> only classifies failures; it is never forwarded.</summary>
internal sealed record ToolResult(ToolOutcome Outcome, int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => Outcome == ToolOutcome.Completed && ExitCode == 0;
}

/// <summary>
/// Runs a helper tool (<c>docker</c>, <c>systemctl</c>, <c>pg_lsclusters</c>) without a shell: the arguments are an argument list,
/// the run has a timeout and an output cap, and a tool that overstays is killed with its children.
/// </summary>
internal static class DiscoveryTool
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorAccessDeniedWindows = 5;
    private const int ErrorAccessDeniedPosix = 13;
    private const int MaximumErrorCharacters = 4096;
    private const long MaximumConfigurationBytes = 1024 * 1024;

    public static async Task<ToolResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, int maximumOutputCharacters, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("The process did not start.");
        }
        catch (Win32Exception exception)
        {
            return new ToolResult(exception.NativeErrorCode switch
            {
                ErrorFileNotFound => ToolOutcome.NotFound,
                ErrorAccessDeniedWindows or ErrorAccessDeniedPosix => ToolOutcome.AccessDenied,
                _ => ToolOutcome.Failed,
            }, -1, string.Empty, string.Empty);
        }
        catch (InvalidOperationException)
        {
            return new ToolResult(ToolOutcome.Failed, -1, string.Empty, string.Empty);
        }

        using (process)
        {
            process.StandardInput.Close();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var output = ReadCappedAsync(process.StandardOutput, maximumOutputCharacters);
            var error = ReadCappedAsync(process.StandardError, MaximumErrorCharacters);
            try
            {
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                await Task.WhenAll(output, error).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new ToolResult(ToolOutcome.TimedOut, -1, string.Empty, string.Empty);
            }
            return new ToolResult(ToolOutcome.Completed, process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
    }

    /// <summary>The text of a small configuration file, or null when it is absent, unreadable or too large to be a configuration file.</summary>
    public static string? ReadSmallFile(string path)
    {
        try
        {
            return new FileInfo(path) is { Exists: true, Length: <= MaximumConfigurationBytes } file ? File.ReadAllText(file.FullName) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Already gone, or not ours to kill: either way nothing is left to wait for.
        }
    }

    /// <summary>Reads to the end so the child never blocks on a full pipe, but keeps only the first <paramref name="limit"/> characters.</summary>
    private static async Task<string> ReadCappedAsync(StreamReader reader, int limit)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            text.Append(buffer, 0, Math.Min(read, Math.Max(0, limit - text.Length)));
        }
        return text.ToString();
    }
}
