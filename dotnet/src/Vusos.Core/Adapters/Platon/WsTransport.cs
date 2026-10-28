using System.Net.Http.Headers;
using System.Text.Json;

namespace Vusos.Core.Adapters.Platon;

/// <summary>Moodle REST web services: <c>POST webservice/rest/server.php</c> z <c>wstoken</c> (formularz, tablice w stylu PHP).</summary>
public sealed class WsTransport : IMoodleTransport
{
    private readonly HttpClient _http;
    private readonly string _wsToken;
    private readonly TimeSpan _timeout;

    public WsTransport(HttpClient http, string baseUrl, string wsToken, TimeSpan? timeout = null)
    {
        _http = http;
        _wsToken = wsToken;
        _timeout = timeout ?? MoodleHttp.DefaultTimeout;
        BaseUrl = MoodleUrls.Normalize(baseUrl);
    }

    public Uri BaseUrl { get; }

    public async Task<JsonElement> CallAsync(string wsfunction, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("wstoken", _wsToken),
            new("wsfunction", wsfunction),
            new("moodlewsrestformat", "json")
        };
        form.AddRange(MoodleArgsEncoder.Flatten(args));

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUrl, "webservice/rest/server.php"))
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var result = await MoodleHttp.SendAsync(_http, request, _timeout, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        MoodleHttp.ThrowForStatus(result.Code);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(result.Body);
        }
        catch (JsonException)
        {
            throw new AdapterUnavailableException($"Platon zwrócił odpowiedź nie-JSON dla {wsfunction}");
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("exception", out _))
                throw MoodleErrors.FromCode(MoodleHttp.GetString(root, "errorcode"), MoodleHttp.GetString(root, "message") ?? "Moodle error");
            return root.Clone();
        }
    }

    public IReadOnlyDictionary<string, string> FileHeaders() => new Dictionary<string, string>();

    /// <summary>
    /// pluginfile.php → webservice/pluginfile.php?token=… . Obce hosty i adresy niebędące plikami zostają bez zmian
    /// (token nigdy nie trafia do obcego hosta).
    /// </summary>
    public string RewriteFileUrl(string url)
    {
        if (!MoodleUrls.IsSameOrigin(url, BaseUrl)) return url;

        var fragmentAt = url.IndexOf('#');
        var fragment = fragmentAt >= 0 ? url[fragmentAt..] : string.Empty;
        var rest = fragmentAt >= 0 ? url[..fragmentAt] : url;
        var queryAt = rest.IndexOf('?');
        var path = queryAt >= 0 ? rest[..queryAt] : rest;
        var query = queryAt >= 0 ? rest[(queryAt + 1)..] : string.Empty;

        if (path.Contains("/webservice/pluginfile.php/", StringComparison.Ordinal))
        {
            // już adres WS
        }
        else if (path.Contains("/pluginfile.php/", StringComparison.Ordinal))
        {
            var i = path.IndexOf("/pluginfile.php/", StringComparison.Ordinal);
            path = path[..i] + "/webservice/pluginfile.php/" + path[(i + "/pluginfile.php/".Length)..];
        }
        else
        {
            return url; // nie adres pliku (np. mod/resource/view.php)
        }

        var hasToken = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Any(p => p.StartsWith("token=", StringComparison.Ordinal) || p == "token");
        if (!hasToken)
        {
            var token = "token=" + Uri.EscapeDataString(_wsToken);
            query = query.Length == 0 ? token : query + "&" + token;
        }
        return path + "?" + query + fragment;
    }
}
