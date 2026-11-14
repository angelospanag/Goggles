namespace Goggles.Desktop.Services;

/// <summary>
/// Formats a timestamp as a short relative string ("2 days ago"), matching what GOG Galaxy's
/// activity feed shows. Hand-rolled rather than a date library — this is the one place that needs
/// it.
/// </summary>
public static class RelativeTime
{
    public static string Format(DateTimeOffset then, DateTimeOffset? now = null)
    {
        var seconds = Math.Max(0, ((now ?? DateTimeOffset.UtcNow) - then).TotalSeconds);

        (double Size, string Unit)[] units =
        [
            (60, "second"),
            (60, "minute"),
            (24, "hour"),
            (7, "day"),
            (4.345, "week"),
            (12, "month"),
            (double.PositiveInfinity, "year"),
        ];

        var value = seconds;
        foreach (var (size, unit) in units)
        {
            if (value < size)
            {
                var n = (int)value;
                return n <= 0 ? "just now" : $"{n} {unit}{(n == 1 ? "" : "s")} ago";
            }

            value /= size;
        }

        return string.Empty;
    }

    /// <summary>The calendar-day part of a timestamp, for grouping activity entries by day.</summary>
    public static DateOnly DateKey(DateTimeOffset value) => DateOnly.FromDateTime(value.UtcDateTime);
}
