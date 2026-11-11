using Microsoft.Extensions.Logging;

namespace Mize.Tests.Helpers;

/// <summary>One captured log call.</summary>
/// <param name="Level">Severity.</param>
/// <param name="Message">Fully formatted message.</param>
/// <param name="Exception">Attached exception, if any.</param>
public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

/// <summary>Thread-safe collection of captured log entries shared by loggers.</summary>
public sealed class LogSink
{
    private readonly List<LogEntry> _entries = new();

    /// <summary>Appends an entry.</summary>
    public void Add(LogEntry entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    /// <summary>A snapshot copy of all entries captured so far.</summary>
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }
}

/// <summary>An <see cref="ILogger"/> that records every call into a <see cref="LogSink"/> so tests can assert levels and messages.</summary>
public class ListLogger : ILogger
{
    /// <summary>Creates a logger writing to <paramref name="sink"/> (or a fresh sink).</summary>
    public ListLogger(LogSink? sink = null)
    {
        Sink = sink ?? new LogSink();
    }

    /// <summary>Where entries are captured.</summary>
    public LogSink Sink { get; }

    /// <summary>Shortcut for <c>Sink.Entries</c>.</summary>
    public IReadOnlyList<LogEntry> Entries => Sink.Entries;

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Sink.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}

/// <summary>Typed variant of <see cref="ListLogger"/> for injection as <c>ILogger&lt;T&gt;</c>.</summary>
public sealed class ListLogger<T> : ListLogger, ILogger<T>
{
}

/// <summary>An <see cref="ILoggerProvider"/> that routes every category's logs into one shared <see cref="LogSink"/>; used to observe logs of a running API.</summary>
public sealed class ListLoggerProvider : ILoggerProvider
{
    /// <summary>All entries from all categories.</summary>
    public LogSink Sink { get; } = new();

    /// <summary>Shortcut for <c>Sink.Entries</c>.</summary>
    public IReadOnlyList<LogEntry> Entries => Sink.Entries;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new ListLogger(Sink);

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
