using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace Vusos.Core.Adapters.Platon;

/// <summary>
/// Niskopoziomowy dostęp do funkcji Moodle web services. Warstwa funkcji (<see cref="PlatonFunctions"/>) nie zależy od
/// transportu; są dwie implementacje: <see cref="WsTransport"/> (REST + wstoken) i <see cref="AjaxTransport"/>
/// (cookie sesji + sesskey, <c>lib/ajax/service.php</c>).
/// </summary>
public interface IMoodleTransport
{
    /// <summary>Adres główny Moodle, zawsze zakończony „/”.</summary>
    Uri BaseUrl { get; }

    /// <summary>Wywołuje funkcję WS; zwraca samo „data” (sklonowany <see cref="JsonElement"/>).</summary>
    Task<JsonElement> CallAsync(string wsfunction, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default);

    /// <summary>Nagłówki do pobierania plików (np. Cookie w trybie web).</summary>
    IReadOnlyDictionary<string, string> FileHeaders();

    /// <summary>Zamienia adres pluginfile.php na taki, który da się pobrać z <see cref="FileHeaders"/> (i ewentualnie tokenem).</summary>
    string RewriteFileUrl(string url);
}

/// <summary>Kody błędów Moodle i ich mapowanie na wyjątki adapterów.</summary>
public static class MoodleErrors
{
    /// <summary>
    /// Kody oznaczające „poświadczenia nieważne / niezalogowany”.
    /// Uwaga: <c>accessexception</c> bywa też zwracany przy zwykłym braku uprawnień do funkcji — wywołania
    /// „best effort” muszą więc tolerować <see cref="AdapterAuthException"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> AuthErrorCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "invalidtoken", "accessexception", "requireloginerror", "servicerequireslogin",
        "invalidsesskey", "sessionexpired", "forcepasswordchangenotice"
    };

    public static Exception FromCode(string? errorCode, string? message)
    {
        var text = !string.IsNullOrEmpty(message) ? message : (!string.IsNullOrEmpty(errorCode) ? errorCode : "Moodle error");
        if (errorCode is not null && AuthErrorCodes.Contains(errorCode))
            return new AdapterAuthException(text);
        // invalidrecord / invalidparameter / ... — ponowienie niczego nie zmieni.
        var permanent = errorCode is not null &&
            (errorCode.StartsWith("invalid", StringComparison.Ordinal) || errorCode == "dml_missing_record_exception");
        return new AdapterUnavailableException(text) { IsTransient = !permanent };
    }
}

/// <summary>Kodowanie zagnieżdżonych argumentów w stylu tablic PHP: <c>courseids[0]=12</c>, <c>events[timesortfrom]=5</c>.</summary>
public static class MoodleArgsEncoder
{
    /// <summary>
    /// Spłaszcza argumenty. Tablice → <c>key[i]</c>, słowniki → <c>key[k]</c>, bool → 1/0, null pomijany,
    /// liczby zawsze w kulturze niezmiennej.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Flatten(IReadOnlyDictionary<string, object?> args)
    {
        var output = new List<KeyValuePair<string, string>>();
        foreach (var (key, value) in args) Walk(output, key, value);
        return output;
    }

    private static void Walk(List<KeyValuePair<string, string>> output, string key, object? value)
    {
        switch (value)
        {
            case null:
                return;
            case string s:
                output.Add(new(key, s));
                return;
            case bool b:
                output.Add(new(key, b ? "1" : "0"));
                return;
            case IReadOnlyDictionary<string, object?> ro:
                foreach (var (k, v) in ro) Walk(output, $"{key}[{k}]", v);
                return;
            case IDictionary dict:
                foreach (DictionaryEntry e in dict) Walk(output, $"{key}[{Convert.ToString(e.Key, CultureInfo.InvariantCulture)}]", e.Value);
                return;
            case IEnumerable seq:
                var index = 0;
                foreach (var item in seq) Walk(output, $"{key}[{index++}]", item);
                return;
            case IFormattable f:
                output.Add(new(key, f.ToString(null, CultureInfo.InvariantCulture)));
                return;
            default:
                output.Add(new(key, value.ToString() ?? string.Empty));
                return;
        }
    }
}

internal static class MoodleUrls
{
    public static Uri Normalize(string baseUrl)
    {
        var trimmed = baseUrl.Trim();
        if (!trimmed.EndsWith('/')) trimmed += "/";
        return new Uri(trimmed, UriKind.Absolute);
    }

    public static bool IsSameOrigin(string url, Uri baseUrl)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        return string.Equals(u.Scheme, baseUrl.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(u.Host, baseUrl.Host, StringComparison.OrdinalIgnoreCase)
            && u.Port == baseUrl.Port;
    }
}
