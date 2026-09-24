using System.Collections.Concurrent;
using DailyMusings.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace DailyMusings.Infrastructure.Diagnostics;

/// <summary>
/// Keeps the most recent log lines in memory so the admin page can show what the instance has been doing
/// (docs/开发指导.md §16).
/// <para>
/// A bounded ring, not a log file. The product logs to stdout on purpose — the container's own log handling is
/// the operator's business — and writing a second copy of every line into the instance volume would put
/// diagnostics into the backup set, which is exactly what §15.2 excludes. What this adds is a page that answers
/// "what has it been saying" without a shell, and it can only ever show what was already being logged: it does
/// not add fields, unwrap exceptions or enrich anything, so §16's promise holds by construction rather than by
/// this type remembering to be careful.
/// </para>
/// <para>
/// Exceptions are recorded as their type name only. A stack trace names internal paths and, for an upstream
/// failure, occasionally the request; the type is what an operator actually needs in order to search.
/// </para>
/// </summary>
public sealed class RecentLogBuffer : ILoggerProvider, IRecentLogReader
{
    /// <summary>Enough to cover a burst of failures, small enough that a long-running instance cannot grow.</summary>
    public const int DefaultCapacity = 500;

    private readonly ConcurrentQueue<LogLine> _lines = new();
    private readonly int _capacity;
    private readonly LogLevel _minimumLevel;
    private int _count;

    public RecentLogBuffer(LogLevel minimumLevel = LogLevel.Information, int capacity = DefaultCapacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The capacity must be positive.");
        }

        _minimumLevel = minimumLevel;
        _capacity = capacity;
    }

    public string MinimumLevel => _minimumLevel.ToString();

    public IReadOnlyList<LogLine> Read(int lines)
    {
        var snapshot = _lines.ToArray();

        return lines >= snapshot.Length
            ? snapshot
            : snapshot[^Math.Max(lines, 0)..];
    }

    public ILogger CreateLogger(string categoryName) => new BufferedLogger(this, categoryName);

    public void Dispose()
    {
        // Nothing unmanaged is held. The queue is dropped with the provider.
        _lines.Clear();
        _count = 0;
    }

    private void Add(LogLine line)
    {
        _lines.Enqueue(line);

        // Trim by count rather than by a timer: the queue is only appended to, so the oldest entries are always
        // at the front and dropping one is O(1).
        while (Interlocked.Increment(ref _count) > _capacity)
        {
            if (_lines.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _count);
                continue;
            }

            Interlocked.Decrement(ref _count);
            break;
        }
    }

    private sealed class BufferedLogger : ILogger
    {
        private readonly RecentLogBuffer _buffer;
        private readonly string _category;

        public BufferedLogger(RecentLogBuffer buffer, string category)
        {
            _buffer = buffer;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _buffer._minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || formatter is null)
            {
                return;
            }

            var message = formatter(state, exception);

            // The exception type travels with the line because "which failure" is the operator's first question;
            // the stack does not, because a stack trace is where internal paths and request fragments appear.
            if (exception is not null)
            {
                message = string.IsNullOrEmpty(message)
                    ? exception.GetType().Name
                    : $"{message} ({exception.GetType().Name})";
            }

            _buffer.Add(new LogLine(DateTimeOffset.UtcNow, logLevel.ToString(), _category, message));
        }
    }
}
