namespace Vusos.UI.Util;

/// <summary>Zakres dat włącznie (od–do). Tygodnie liczone od poniedziałku.</summary>
public readonly record struct DateRange(DateOnly From, DateOnly To)
{
    public static DateOnly MondayOf(DateOnly d)
    {
        var offset = ((int)d.DayOfWeek + 6) % 7; // pon = 0
        return d.AddDays(-offset);
    }

    public static DateRange WeekOf(DateOnly d)
    {
        var monday = MondayOf(d);
        return new DateRange(monday, monday.AddDays(6));
    }

    public IEnumerable<DateOnly> Days()
    {
        for (var d = From; d <= To; d = d.AddDays(1)) yield return d;
    }

    public bool Contains(DateOnly d) => d >= From && d <= To;
    public DateRange Shift(int days) => new(From.AddDays(days), To.AddDays(days));
    public int Length => To.DayNumber - From.DayNumber + 1;

    /// <summary>np. „26 paź – 1 lis 2026”.</summary>
    public string Label()
    {
        var pl = Fmt.Pl;
        var from = From.Year == To.Year ? From.ToString("d MMM", pl) : From.ToString("d MMM yyyy", pl);
        return $"{from} – {To.ToString("d MMM yyyy", pl)}";
    }
}
