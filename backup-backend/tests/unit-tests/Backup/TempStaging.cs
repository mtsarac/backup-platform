using BackupPlatform.BackupProviders.Abstractions.Database;
using BackupPlatform.Contracts.Backups;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.UnitTests.Backup;

/// <summary>
/// A real staging directory in a temporary location plus a sibling directory outside it, with files backdated by
/// <see cref="File.SetLastWriteTimeUtc(string, DateTime)"/> against a fixed <see cref="Now"/> so no test ever sleeps.
/// </summary>
internal sealed class TempStaging : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bp_test_staging_" + Guid.NewGuid().ToString("N")[..10]);

    public TempStaging(bool createStagingDirectory = true)
    {
        StagingPath = Path.Combine(_root, "staging");
        OutsidePath = Path.Combine(_root, "outside");
        Directory.CreateDirectory(OutsidePath);
        if (createStagingDirectory)
        {
            Directory.CreateDirectory(StagingPath);
        }
    }

    public string Root => _root;

    public string StagingPath { get; }

    /// <summary>A directory next to the staging directory: what a link inside staging must never lead the cleanup into.</summary>
    public string OutsidePath { get; }

    public AgentStagingDirectory Staging => new(new AgentPath(StagingPath));

    /// <summary>A name the platform itself would generate.</summary>
    public static string ManagedName(string database = "Sales") => ManagedBackupLayout.StagedFileName(database, Now.AddDays(-30), Guid.NewGuid());

    /// <summary>Writes a file whose last write is <paramref name="age"/> before <see cref="Now"/>.</summary>
    public string Write(string name, TimeSpan age, int size = 16) => WriteIn(StagingPath, name, age, size);

    public string WriteOutside(string name, TimeSpan age, int size = 16) => WriteIn(OutsidePath, name, age, size);

    public string PathOf(string name) => Path.Combine(StagingPath, name);

    /// <summary>True for a file, a directory or a link (even a dangling one) of that name.</summary>
    public bool Exists(string name) =>
        File.Exists(PathOf(name)) || Directory.Exists(PathOf(name)) || new FileInfo(PathOf(name)).LinkTarget is not null;

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows() && Directory.Exists(StagingPath))
        {
            File.SetUnixFileMode(StagingPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string WriteIn(string directory, string name, TimeSpan age, int size)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, (Now - age).UtcDateTime);
        return path;
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Records every entry as formatted text and lets a test react to one (for example to cancel mid-sweep).</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly object _gate = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public Action<LogLevel, string>? OnLog { get; set; }

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public string AllText => string.Join('\n', Entries.Select(entry => $"{entry.Level}|{entry.Message}"));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception) + (exception is null ? "" : "|" + exception);
        lock (_gate)
        {
            _entries.Add((logLevel, message));
        }

        OnLog?.Invoke(logLevel, message);
    }
}
