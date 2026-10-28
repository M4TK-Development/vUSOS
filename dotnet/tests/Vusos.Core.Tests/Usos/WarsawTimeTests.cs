using Vusos.Core.Adapters.Usos;

namespace Vusos.Core.Tests.Usos;

public class WarsawTimeTests
{
    private static string? Iso(string local) => WarsawTime.ParseLocal(local) is { } d ? WarsawTime.ToIso(d) : null;

    [Fact]
    public void Uses_plus_one_in_winter_and_plus_two_in_summer()
    {
        Assert.Equal("2025-01-15T12:00:00+01:00", Iso("2025-01-15 12:00:00"));
        Assert.Equal("2025-07-01T12:00:00+02:00", Iso("2025-07-01 12:00:00"));
    }

    [Fact]
    public void Handles_the_DST_switch_days()
    {
        Assert.Equal("2025-03-30T01:59:00+01:00", Iso("2025-03-30 01:59:00"));
        Assert.Equal("2025-03-30T04:00:00+02:00", Iso("2025-03-30 04:00:00"));
        Assert.Equal("2025-10-26T01:00:00+02:00", Iso("2025-10-26 01:00:00"));
        Assert.Equal("2025-10-26T04:00:00+01:00", Iso("2025-10-26 04:00:00"));
    }

    [Fact]
    public void Accepts_T_separator_missing_seconds_date_only_and_zoned_input()
    {
        Assert.Equal("2025-01-15T08:30:00+01:00", Iso("2025-01-15T08:30"));
        Assert.Equal("2025-01-15T00:00:00+01:00", Iso("2025-01-15"));
        var zoned = WarsawTime.ParseLocal("2025-01-15T08:30:00Z");
        Assert.Equal(new DateTimeOffset(2025, 1, 15, 8, 30, 0, TimeSpan.Zero), zoned);
        Assert.Equal(TimeSpan.Zero, zoned!.Value.Offset);
        Assert.Equal(TimeSpan.FromHours(5), WarsawTime.ParseLocal("2025-01-15 08:30:00+05:00")!.Value.Offset);
    }

    [Fact]
    public void Rejects_garbage_and_impossible_dates()
    {
        Assert.Null(WarsawTime.ParseLocal("garbage"));
        Assert.Null(WarsawTime.ParseLocal(""));
        Assert.Null(WarsawTime.ParseLocal(null));
        Assert.Null(WarsawTime.ParseLocal("2025-13-45 10:00:00"));
        Assert.Null(WarsawTime.ParseLocal("2025-01-15 25:00:00"));
    }

    [Fact]
    public void Produces_the_right_instant()
    {
        Assert.Equal(new DateTimeOffset(2025, 7, 1, 10, 0, 0, TimeSpan.Zero), WarsawTime.ParseLocal("2025-07-01 12:00:00"));
    }

    [Fact]
    public void Date_helpers_cross_month_boundaries()
    {
        Assert.Equal(new DateOnly(2025, 2, 1), new DateOnly(2025, 1, 31).AddDays(1));
        Assert.Equal(7, WarsawTime.DiffDays(new(2025, 10, 6), new(2025, 10, 13)));
        Assert.Equal([new(2025, 10, 6), new(2025, 10, 7), new(2025, 10, 8)], WarsawTime.EachDay(new(2025, 10, 6), new(2025, 10, 8)));
        Assert.Empty(WarsawTime.EachDay(new(2025, 10, 8), new(2025, 10, 6)));
    }

    [Fact]
    public void Knows_mondays_and_weekdays()
    {
        Assert.Equal(DayOfWeek.Monday, new DateOnly(2025, 10, 6).DayOfWeek);
        Assert.Equal(new DateOnly(2025, 10, 6), WarsawTime.MondayOf(new(2025, 10, 12)));
        Assert.Equal(new DateOnly(2025, 10, 6), WarsawTime.MondayOf(new(2025, 10, 6)));
    }

    [Fact]
    public void Derives_the_Warsaw_calendar_date_of_an_instant()
    {
        Assert.Equal(new DateOnly(2026, 1, 1), WarsawTime.DateOf(DateTimeOffset.Parse("2025-12-31T23:30:00Z", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(new DateOnly(2025, 7, 1), WarsawTime.DateOf(DateTimeOffset.Parse("2025-06-30T22:30:00Z", System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void ToDateOnly_reads_the_yyyy_MM_dd_prefix()
    {
        Assert.Equal(new DateOnly(2025, 11, 20), WarsawTime.ToDateOnly(" 2025-11-20 10:00:00"));
        Assert.Null(WarsawTime.ToDateOnly("20.11.2025"));
        Assert.Null(WarsawTime.ToDateOnly(null));
    }
}
