using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BackupPlatform.AgentTests.Support;

internal sealed record LogEntry(string Category, LogLevel Level, string Message, string? Exception);

/// <summary>Captures every log entry (message and full exception text) so tests can assert on what was written.</summary>
internal sealed class CaptureLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    public string AllText => string.Join('\n', Entries.Select(e => $"{e.Category}|{e.Level}|{e.Message}|{e.Exception}"));

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    public ILoggerFactory CreateFactory(LogLevel minimum = LogLevel.Debug) =>
        LoggerFactory.Create(builder => builder.SetMinimumLevel(minimum).AddProvider(this));

    private sealed class CaptureLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), exception?.ToString()));
    }
}
