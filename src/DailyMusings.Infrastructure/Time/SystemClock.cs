using DailyMusings.Application.Abstractions;

namespace DailyMusings.Infrastructure.Time;

/// <summary>The production clock. Tests substitute <see cref="IClock"/> rather than freezing the system.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
