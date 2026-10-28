using System.Globalization;
using Vusos.Core.Domain;

namespace Vusos.UI.Util;

public static class GradeMath
{
    /// <summary>Wartość liczbowa symbolu oceny („4,5” / „4.5”); null dla „zal”, „nzal” itp.</summary>
    public static bool TryNumber(string? symbol, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        return double.TryParse(symbol.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Średnia arytmetyczna (nieważona – bez ECTS) z ocen liczonych do średniej.</summary>
    public static double? Average(IEnumerable<Grade> grades)
    {
        var values = new List<double>();
        foreach (var g in grades)
            if (g.CountsIntoAverage && TryNumber(g.ValueSymbol, out var v)) values.Add(v);
        return values.Count == 0 ? null : values.Average();
    }

    public static string Tone(Grade g)
    {
        if (g.Passes is false) return "danger";
        if (TryNumber(g.ValueSymbol, out var v)) return v >= 4.5 ? "ok" : v >= 3 ? "info" : "danger";
        return g.Passes is true ? "ok" : "neutral";
    }
}
