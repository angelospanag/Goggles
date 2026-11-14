using System.Globalization;

namespace Goggles.Core.Api.Embed.Models;

/// <summary>
/// A date as PHP's <c>DateTime</c> serialises it — <c>{"date":"1996-09-09 00:00:00.000000",
/// "timezone_type":1,"timezone":"+02:00"}</c> — which is what the embed endpoints return.
/// </summary>
public sealed record PhpDateTime
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd HH:mm:ss.ffffff",
        "yyyy-MM-dd HH:mm:ss",
    ];

    public string? Date { get; init; }

    public int TimezoneType { get; init; }

    public string? Timezone { get; init; }

    /// <summary>Converts to an absolute instant, or null if the value cannot be read.</summary>
    public DateTimeOffset? ToDateTimeOffset()
    {
        if (string.IsNullOrWhiteSpace(Date) ||
            !DateTime.TryParseExact(
                Date,
                Formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return null;
        }

        return new DateTimeOffset(parsed, ResolveOffset(parsed));
    }

    private TimeSpan ResolveOffset(DateTime local)
    {
        if (string.IsNullOrWhiteSpace(Timezone))
        {
            return TimeSpan.Zero;
        }

        // type 1 is a literal "+02:00" offset; type 3 is a named zone such as "Europe/Warsaw".
        if (TimeSpan.TryParseExact(
                Timezone.TrimStart('+', '-'),
                @"hh\:mm",
                CultureInfo.InvariantCulture,
                out var offset))
        {
            return Timezone.StartsWith('-') ? -offset : offset;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(Timezone).GetUtcOffset(local);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeSpan.Zero;
        }
    }
}
