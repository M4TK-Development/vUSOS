using System.Globalization;
using System.Text.RegularExpressions;

namespace Vusos.Core.Adapters.Usos;

/// <summary>
/// Date helpers shared by the USOS adapters. USOS returns wall-clock times of the installation's time zone
/// ("YYYY-MM-DD HH:MM:SS", no offset); for Vistula that is Europe/Warsaw. Results carry the Warsaw UTC offset
/// (e.g. 2025-10-06T08:30:00+02:00).
/// </summary>
public static partial class WarsawTime
{
    private static readonly Lazy<TimeZoneInfo?> Zone = new(ResolveZone);

    private static TimeZoneInfo? ResolveZone()
    {
        foreach (var id in new[] { "Europe/Warsaw", "Central European Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return null;
    }

    /// <summary>UTC offset of Europe/Warsaw at the given instant (+01:00 or +02:00).</summary>
    public static TimeSpan OffsetAt(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        var zone = Zone.Value;
        if (zone is not null) return zone.GetUtcOffset(utc);
        // No tz database available (stripped container): EU rule, DST from last Sunday of March 01:00 UTC
        // to last Sunday of October 01:00 UTC.
        var year = utc.Year;
        var start = new DateTimeOffset(LastSunday(year, 3).ToDateTime(new TimeOnly(1, 0)), TimeSpan.Zero);
        var end = new DateTimeOffset(LastSunday(year, 10).ToDateTime(new TimeOnly(1, 0)), TimeSpan.Zero);
        return utc >= start && utc < end ? TimeSpan.FromHours(2) : TimeSpan.FromHours(1);
    }

    private static DateOnly LastSunday(int year, int month)
    {
        var d = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        return d.AddDays(-(int)d.DayOfWeek);
    }

    [GeneratedRegex(@"^(\d{4})-(\d{2})-(\d{2})(?:[ T](\d{2}):(\d{2})(?::(\d{2}))?)?$")]
    private static partial Regex LocalRegex();

    [GeneratedRegex(@"(?:Z|[+-]\d{2}:?\d{2})$")]
    private static partial Regex HasZoneRegex();

    /// <summary>
    /// Converts a Warsaw wall-clock string ("YYYY-MM-DD HH:MM:SS", "YYYY-MM-DDTHH:MM", or a date only) to a
    /// <see cref="DateTimeOffset"/> with the Warsaw offset. Strings that already carry a zone keep it.
    /// Returns null when the input is not recognisable.
    /// </summary>
    public static DateTimeOffset? ParseLocal(string? local)
    {
        if (string.IsNullOrWhiteSpace(local)) return null;
        var trimmed = local.Trim();
        if (trimmed.Length > 10 && HasZoneRegex().IsMatch(trimmed))
        {
            return DateTimeOffset.TryParse(trimmed.Replace(' ', 'T'), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var zoned) ? zoned : null;
        }
        var m = LocalRegex().Match(trimmed);
        if (!m.Success) return null;
        int Num(int g, int dflt) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : dflt;
        DateTime wall;
        try { wall = new DateTime(Num(1, 0), Num(2, 0), Num(3, 0), Num(4, 0), Num(5, 0), Num(6, 0), DateTimeKind.Unspecified); }
        catch (ArgumentOutOfRangeException) { return null; }

        var guess = new DateTimeOffset(wall, TimeSpan.Zero); // the wall clock read as if it were UTC
        var off = OffsetAt(guess);
        var off2 = OffsetAt(guess - off);
        if (off2 != off) off = off2; // around the DST switch
        return new DateTimeOffset(wall, off);
    }

    /// <summary>ISO-8601 with numeric offset, e.g. 2025-10-06T08:30:00+02:00.</summary>
    public static string ToIso(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>Calendar date (as seen in Europe/Warsaw) of an instant.</summary>
    public static DateOnly DateOf(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.ToOffset(OffsetAt(instant)).DateTime);

    /// <summary>Warsaw wall-clock date of an entry produced by <see cref="ParseLocal"/>.</summary>
    public static DateOnly LocalDateOf(DateTimeOffset value) => DateOnly.FromDateTime(value.DateTime);

    /// <summary>"yyyy-MM-dd" prefix of a string, else null.</summary>
    public static DateOnly? ToDateOnly(string? value)
    {
        if (value is null) return null;
        var t = value.Trim();
        if (t.Length < 10) return null;
        return DateOnly.TryParseExact(t[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
    }

    /// <summary>b - a in whole days.</summary>
    public static int DiffDays(DateOnly a, DateOnly b) => b.DayNumber - a.DayNumber;

    /// <summary>Inclusive list of dates between from and to (empty if to is before from).</summary>
    public static IEnumerable<DateOnly> EachDay(DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to; d = d.AddDays(1)) yield return d;
    }

    /// <summary>Monday on or before the given date.</summary>
    public static DateOnly MondayOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    /// <summary>yyyy-MM-dd.</summary>
    public static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
