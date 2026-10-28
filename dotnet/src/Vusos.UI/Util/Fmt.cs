using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Vusos.Core.Domain;

namespace Vusos.UI.Util;

/// <summary>Polskie formatowanie dat, kwot i rozmiarów. Czas zawsze w strefie Europe/Warsaw.</summary>
public static partial class Fmt
{
    public static readonly CultureInfo Pl = ResolveCulture();
    public static readonly TimeZoneInfo Warsaw = ResolveZone();

    private static CultureInfo ResolveCulture()
    {
        try { return CultureInfo.GetCultureInfo("pl-PL"); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    private static TimeZoneInfo ResolveZone()
    {
        foreach (var id in new[] { "Europe/Warsaw", "Central European Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }

    public static DateTimeOffset ToLocal(DateTimeOffset d) => TimeZoneInfo.ConvertTime(d, Warsaw);
    public static DateOnly DateOf(DateTimeOffset d) => DateOnly.FromDateTime(ToLocal(d).DateTime);
    public static DateOnly Today(TimeProvider clock) => DateOf(clock.GetUtcNow());

    /// <summary>Początek doby (Warsaw) jako moment w czasie.</summary>
    public static DateTimeOffset StartOfDay(DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Warsaw.GetUtcOffset(local));
    }

    public static string Time(DateTimeOffset d) => ToLocal(d).ToString("HH:mm", Pl);
    public static string Date(DateTimeOffset d) => ToLocal(d).ToString("d MMM yyyy", Pl);
    public static string Date(DateOnly d) => d.ToString("d MMM yyyy", Pl);
    public static string Date(DateOnly? d) => d is { } v ? Date(v) : "—";
    public static string DateTime(DateTimeOffset d) => ToLocal(d).ToString("d MMM yyyy, HH:mm", Pl);
    public static string DateTime(DateTimeOffset? d) => d is { } v ? DateTime(v) : "—";
    public static string DayLong(DateOnly d) => d.ToString("dddd, d MMMM", Pl);
    public static string DayShort(DateOnly d) => d.ToString("ddd", Pl);
    public static string DayNumber(DateOnly d) => d.ToString("d MMM", Pl);

    public static string Money(decimal amount, string currency = "PLN")
        => currency.Equals("PLN", StringComparison.OrdinalIgnoreCase)
            ? amount.ToString("N2", Pl) + " zł"
            : amount.ToString("N2", Pl) + " " + currency;

    public static string Bytes(long? n)
    {
        if (n is null) return "";
        double v = n.Value;
        if (v < 1024) return $"{n} B";
        if (v < 1024 * 1024) return (v / 1024).ToString("0.#", Pl) + " KB";
        if (v < 1024L * 1024 * 1024) return (v / 1024 / 1024).ToString("0.#", Pl) + " MB";
        return (v / 1024 / 1024 / 1024).ToString("0.##", Pl) + " GB";
    }

    public static string Average(double? v) => v is { } x ? x.ToString("0.00", Pl) : "—";

    public static string Plural(int n, string one, string few, string many)
    {
        if (n == 1) return one;
        var m10 = n % 10;
        var m100 = n % 100;
        return m10 is >= 2 and <= 4 && m100 is not (>= 12 and <= 14) ? few : many;
    }

    public static string SystemName(SourceSystem s) => s == SourceSystem.Usos ? "USOS" : "Platon";

    /// <summary>„za 2 dni”, „jutro”, „3 dni temu”, „za 40 min”.</summary>
    public static string Relative(DateTimeOffset target, DateTimeOffset now)
    {
        var diff = target - now;
        var abs = diff.Duration();
        if (abs < TimeSpan.FromMinutes(1)) return "teraz";
        if (abs < TimeSpan.FromHours(1))
        {
            var m = (int)Math.Round(abs.TotalMinutes);
            return diff > TimeSpan.Zero ? $"za {m} min" : $"{m} min temu";
        }
        var days = DateOf(target).DayNumber - DateOf(now).DayNumber;
        if (abs < TimeSpan.FromHours(24) && days == 0)
        {
            var h = (int)Math.Round(abs.TotalHours);
            return diff > TimeSpan.Zero ? $"za {h} godz." : $"{h} godz. temu";
        }
        return days switch
        {
            0 => "dziś",
            1 => "jutro",
            2 => "pojutrze",
            -1 => "wczoraj",
            > 0 => $"za {days} {Plural(days, "dzień", "dni", "dni")}",
            _ => $"{-days} {Plural(-days, "dzień", "dni", "dni")} temu",
        };
    }

    public static string ClassTypeLabel(string? t)
    {
        var s = (t ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) return "";
        if (s == "w" || s.StartsWith("wyk") || s.StartsWith("lec")) return "wykład";
        if (s.StartsWith("ćw") || s.StartsWith("cw") || s.StartsWith("exerc") || s.StartsWith("class") || s.StartsWith("conv")) return "ćwiczenia";
        if (s.StartsWith("lab")) return "laboratorium";
        if (s.StartsWith("sem")) return "seminarium";
        if (s.StartsWith("proj")) return "projekt";
        return t ?? "";
    }

    /// <summary>Wariant koloru plakietki dla typu zajęć.</summary>
    public static string ClassTypeTone(string? t) => ClassTypeLabel(t) switch
    {
        "wykład" => "brand",
        "ćwiczenia" => "info",
        "laboratorium" => "ok",
        "seminarium" or "projekt" => "warn",
        _ => "neutral",
    };

    /// <summary>Tekst bez znaczników, skrócony – do podglądów (nie do renderowania HTML!).</summary>
    public static string StripHtml(string? html, int max = 160)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = WebUtility.HtmlDecode(TagRegex().Replace(html, " "));
        text = SpaceRegex().Replace(text, " ").Trim();
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    public static string Lecturers(IReadOnlyList<string> l) => string.Join(", ", l);

    [GeneratedRegex("<[^>]*>")] private static partial Regex TagRegex();
    [GeneratedRegex(@"\s+")] private static partial Regex SpaceRegex();
}
