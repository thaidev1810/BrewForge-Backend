namespace BrewForge.Infrastructure.Persistence;

/// <summary>
/// The system clock, cut to the microsecond. PostgreSQL stores a
/// <c>TIMESTAMPTZ</c> to the microsecond while .NET counts in units of 100
/// nanoseconds, so a timestamp would otherwise read back one digit shorter
/// than it was written - and the answer to a request would differ from the
/// answer to the same request repeated.
/// </summary>
public sealed class DatabasePrecisionTimeProvider(TimeProvider inner) : TimeProvider
{
    private const long TicksPerMicrosecond = 10;

    public override DateTimeOffset GetUtcNow()
    {
        var now = inner.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TicksPerMicrosecond));
    }

    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override long GetTimestamp() => inner.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        inner.CreateTimer(callback, state, dueTime, period);
}
