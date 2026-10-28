using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Vusos.Core.Adapters.Platon;

/// <summary>
/// Ajax Moodle (<c>POST lib/ajax/service.php?sesskey=…&amp;info=fn</c>) z cookie sesji przeglądarkowej.
/// Przekierowanie na logowanie, odpowiedź nie-JSON, <c>servicerequireslogin</c> i <c>invalidsesskey</c> →
/// <see cref="AdapterAuthException"/>.
/// <para>
/// Zalecenie: przekazany <see cref="HttpClient"/> powinien mieć <c>AllowAutoRedirect = false</c>. Gdy go nie ma,
/// przekierowanie na stronę logowania i tak jest wykrywane (zmieniony adres końcowy / HTML zamiast JSON).
/// </para>
/// </summary>
public sealed class AjaxTransport : IMoodleTransport
{
    private readonly HttpClient _http;
    private readonly string _cookies;
    private readonly string _sessKey;
    private readonly TimeSpan _timeout;

    public AjaxTransport(HttpClient http, string baseUrl, string cookies, string sessKey, TimeSpan? timeout = null)
    {
        _http = http;
        _cookies = cookies;
        _sessKey = sessKey;
        _timeout = timeout ?? MoodleHttp.DefaultTimeout;
        BaseUrl = MoodleUrls.Normalize(baseUrl);
    }

    public Uri BaseUrl { get; }

    public async Task<JsonElement> CallAsync(string wsfunction, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        var url = new Uri(BaseUrl,
            $"lib/ajax/service.php?sesskey={Uri.EscapeDataString(_sessKey)}&info={Uri.EscapeDataString(wsfunction)}");
        var payload = JsonSerializer.Serialize(new object[]
        {
            new Dictionary<string, object?> { ["index"] = 0, ["methodname"] = wsfunction, ["args"] = args }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("Cookie", _cookies);

        var result = await MoodleHttp.SendAsync(_http, request, _timeout, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (result.Code is >= 300 and < 400) throw new AdapterAuthException("Platon przekierował na logowanie");
        // Auto-redirect włączony w kliencie: wylądowaliśmy gdzie indziej niż service.php.
        if (result.FinalUri is { } final && !final.AbsolutePath.EndsWith("/lib/ajax/service.php", StringComparison.Ordinal))
            throw new AdapterAuthException("Platon przekierował na logowanie");
        MoodleHttp.ThrowForStatus(result.Code);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(result.Body);
        }
        catch (JsonException)
        {
            // HTML (strona logowania, przerwa serwisowa za SSO) zamiast JSON
            throw new AdapterAuthException("Platon zwrócił odpowiedź nie-JSON (sesja prawdopodobnie wygasła)");
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                // TODO(verify): service.php odpowiada na nieprawidłowy sesskey gołym obiektem zamiast tablicą.
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err) && MoodleHttp.IsTruthy(err))
                {
                    var code = MoodleHttp.GetString(root, "errorcode");
                    var msg = MoodleHttp.GetString(root, "error") ?? MoodleHttp.GetString(root, "message") ?? "Moodle error";
                    throw code is not null ? MoodleErrors.FromCode(code, msg) : new AdapterAuthException(msg);
                }
                throw new AdapterUnavailableException($"Nieoczekiwany kształt odpowiedzi AJAX dla {wsfunction}");
            }
            if (root.GetArrayLength() == 0) throw new AdapterUnavailableException($"Pusta odpowiedź AJAX dla {wsfunction}");

            var first = root[0];
            if (first.ValueKind != JsonValueKind.Object)
                throw new AdapterUnavailableException($"Nieoczekiwany kształt odpowiedzi AJAX dla {wsfunction}");

            var failed = (first.TryGetProperty("error", out var e) && MoodleHttp.IsTruthy(e))
                || (first.TryGetProperty("exception", out var x) && MoodleHttp.IsTruthy(x));
            if (failed)
            {
                var ex = first.TryGetProperty("exception", out var exc) ? exc : default;
                throw MoodleErrors.FromCode(MoodleHttp.GetString(ex, "errorcode"), MoodleHttp.GetString(ex, "message") ?? "Moodle error");
            }
            return first.TryGetProperty("data", out var data) ? data.Clone() : MoodleHttp.NullElement();
        }
    }

    public IReadOnlyDictionary<string, string> FileHeaders() => new Dictionary<string, string> { ["Cookie"] = _cookies };

    public string RewriteFileUrl(string url)
    {
        // Cookie sesji działa na zwykłym pluginfile.php; adresy WS wymagałyby tokenu.
        if (!MoodleUrls.IsSameOrigin(url, BaseUrl)) return url;
        return url.Replace("/webservice/pluginfile.php/", "/pluginfile.php/", StringComparison.Ordinal);
    }
}
