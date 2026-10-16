using System.Diagnostics;
using System.Text;

namespace BackupPlatform.BackupProviders.PostgreSql.Tools;

internal enum PostgreSqlStdoutMode { Discard, Lines }

internal sealed record PostgreSqlProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout,
    PostgreSqlStdoutMode StdoutMode = PostgreSqlStdoutMode.Discard,
    Stream? Output = null);

internal sealed record PostgreSqlProcessResult(int ExitCode, IReadOnlyList<string> ErrorTail, bool TimedOut, bool StoppedEarly, long StdoutBytes);

/// <summary>Never invokes a shell or inherits libpq connection overrides. Every started process and both output pumps are joined before returning.</summary>
internal static class PostgreSqlProcessRunner
{
    public static async Task<PostgreSqlProcessResult> RunAsync(
        PostgreSqlProcessSpec spec, Action<string>? onErrorLine, Func<string, bool>? onOutputLine, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(spec.FileName)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in spec.Arguments) info.ArgumentList.Add(argument);
        // libpq's environment must not redirect this task or inherit the agent's own database password/service/options.
        foreach (var name in info.Environment.Keys.Where(name => name.StartsWith("PG", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(name);
        info.Environment["LC_ALL"] = "C";
        info.Environment["PGCLIENTENCODING"] = "UTF8";
        foreach (var (name, value) in spec.Environment) info.Environment[name] = value;

        using var process = new Process { StartInfo = info };
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(spec.Timeout);
        var tail = new Queue<string>(60);
        var stoppedEarly = false;
        long stdoutBytes = 0;
        process.Start();
        process.StandardInput.Close();
        using var registration = bounded.Token.Register(() => Kill(process));
        var stderr = ReadErrorAsync();
        var stdout = ReadOutputAsync();
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        finally
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            info.Environment.Remove("PGPASSWORD");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new PostgreSqlProcessResult(process.ExitCode, tail.ToArray(), bounded.IsCancellationRequested, stoppedEarly, stdoutBytes);

        async Task ReadErrorAsync()
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    if (tail.Count == 60) tail.Dequeue();
                    tail.Enqueue(line.Length > 2000 ? line[..2000] : line);
                    onErrorLine?.Invoke(line);
                }
            }
            catch { Kill(process); throw; }
        }

        async Task ReadOutputAsync()
        {
            try
            {
                if (spec.StdoutMode == PostgreSqlStdoutMode.Lines)
                {
                    string? line;
                    while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
                    {
                        stdoutBytes += line.Length + 1;
                        if (onOutputLine is not null && !onOutputLine(line))
                        {
                            stoppedEarly = true;
                            Kill(process);
                            return;
                        }
                    }
                }
                else
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                    {
                        if (spec.Output is not null) await spec.Output.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None).ConfigureAwait(false);
                        stdoutBytes += read;
                    }
                }
            }
            catch { Kill(process); throw; }
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
    }
}
