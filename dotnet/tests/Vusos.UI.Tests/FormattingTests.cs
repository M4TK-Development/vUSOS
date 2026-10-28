using Vusos.Core.Domain;
using Vusos.UI.Util;

namespace Vusos.UI.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData(1, "plik")]
    [InlineData(2, "pliki")]
    [InlineData(4, "pliki")]
    [InlineData(5, "plików")]
    [InlineData(12, "plików")]
    [InlineData(22, "pliki")]
    public void Plural(int n, string expected) => Assert.Equal(expected, Fmt.Plural(n, "plik", "pliki", "plików"));

    [Fact]
    public void Week_starts_on_monday()
    {
        var week = DateRange.WeekOf(new DateOnly(2026, 10, 28)); // środa
        Assert.Equal(new DateOnly(2026, 10, 26), week.From);
        Assert.Equal(new DateOnly(2026, 11, 1), week.To);
        Assert.Equal(7, week.Length);
        Assert.Equal(DayOfWeek.Monday, week.From.DayOfWeek);
        Assert.Equal(DayOfWeek.Sunday, DateRange.WeekOf(new DateOnly(2026, 11, 1)).To.DayOfWeek); // niedziela należy do poprzedniego tygodnia
        Assert.Equal(new DateOnly(2026, 10, 26), DateRange.WeekOf(new DateOnly(2026, 11, 1)).From);
    }

    [Fact]
    public void Polish_dates_use_warsaw_time()
    {
        var utc = new DateTimeOffset(2026, 10, 28, 22, 30, 0, TimeSpan.Zero); // 23:30 CET (od 25 X obowiązuje czas zimowy)
        Assert.Equal("23:30", Fmt.Time(utc));
        Assert.Equal(new DateOnly(2026, 10, 28), Fmt.DateOf(utc));
        Assert.Equal(new DateOnly(2026, 10, 29), Fmt.DateOf(utc.AddHours(1)));
        Assert.Equal("środa, 28 października", Fmt.DayLong(new DateOnly(2026, 10, 28)));
    }

    [Fact]
    public void Money_and_bytes()
    {
        Assert.Equal("1 450,00 zł", Fmt.Money(1450m).Replace(' ', ' '));
        Assert.Equal("1,4 MB", Fmt.Bytes(1_480_000));
        Assert.Equal("512 B", Fmt.Bytes(512));
    }

    [Fact]
    public void Relative_time()
    {
        var now = new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.FromHours(1));
        Assert.Equal("jutro", Fmt.Relative(now.AddDays(1), now));
        Assert.Equal("za 3 dni", Fmt.Relative(now.AddDays(3), now));
        Assert.Equal("za 30 min", Fmt.Relative(now.AddMinutes(30), now));
        Assert.Equal("wczoraj", Fmt.Relative(now.AddDays(-1), now));
    }

    [Fact]
    public void Grade_average_is_unweighted_and_skips_non_numeric()
    {
        Grade G(string symbol, bool counts = true) => new("g", "t", "c", LangText.Of("x"), null, null, symbol, null, true, counts, null, null, null);
        Assert.Equal(4.25, GradeMath.Average([G("4,5"), G("4"), G("5"), G("3,5")])!.Value, 3);
        Assert.Null(GradeMath.Average([G("zal"), G("5", counts: false)]));
    }
}
