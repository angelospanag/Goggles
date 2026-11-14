using Goggles.Core.Api.Embed.Models;

namespace Goggles.Core.Tests.Api.Embed;

public class PhpDateTimeTests
{
    [Fact]
    public void Reads_a_literal_offset()
    {
        var value = new PhpDateTime
        {
            Date = "1996-09-09 00:00:00.000000",
            TimezoneType = 1,
            Timezone = "+02:00",
        };

        Assert.Equal(
            new DateTimeOffset(1996, 9, 9, 0, 0, 0, TimeSpan.FromHours(2)),
            value.ToDateTimeOffset());
    }

    [Fact]
    public void Reads_a_negative_offset()
    {
        var value = new PhpDateTime { Date = "2020-01-02 03:04:05.000000", Timezone = "-05:00" };

        Assert.Equal(
            new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.FromHours(-5)),
            value.ToDateTimeOffset());
    }

    [Fact]
    public void Reads_a_named_zone()
    {
        var value = new PhpDateTime
        {
            Date = "2020-06-01 12:00:00.000000",
            TimezoneType = 3,
            Timezone = "Europe/Warsaw",
        };

        Assert.Equal(
            new DateTimeOffset(2020, 6, 1, 12, 0, 0, TimeSpan.FromHours(2)),
            value.ToDateTimeOffset());
    }

    [Fact]
    public void Reads_a_date_without_a_fractional_part()
    {
        var value = new PhpDateTime { Date = "2020-06-01 12:00:00", Timezone = "+00:00" };

        Assert.Equal(new DateTimeOffset(2020, 6, 1, 12, 0, 0, TimeSpan.Zero), value.ToDateTimeOffset());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    public void Unreadable_dates_come_back_null(string? date)
    {
        Assert.Null(new PhpDateTime { Date = date, Timezone = "+02:00" }.ToDateTimeOffset());
    }

    [Fact]
    public void An_unknown_zone_falls_back_to_utc()
    {
        var value = new PhpDateTime { Date = "2020-06-01 12:00:00.000000", Timezone = "Mars/Olympus" };

        Assert.Equal(new DateTimeOffset(2020, 6, 1, 12, 0, 0, TimeSpan.Zero), value.ToDateTimeOffset());
    }
}
