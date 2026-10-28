using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vusos.Core.Platform;

namespace Vusos.Core.Tests.Usos;

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public FixedClock(string iso) : this(DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture)) { }
    public DateTimeOffset Now { get; set; } = now;
}

/// <summary>Reads the hand-written fixtures copied to Fixtures/Usos.</summary>
internal static class Fx
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Usos");

    public static string Text(string name) => File.ReadAllText(Path.Combine(Dir, name));

    public static JsonElement Json(string name) => Parse(Text(name));

    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

internal sealed record Recorded(HttpMethod Method, Uri Url, IReadOnlyDictionary<string, string> Headers, string Body)
{
    /// <summary>Wire path without the leading slash, e.g. services/users/user.</summary>
    public string Path => Url.AbsolutePath.TrimStart('/');

    public string? Authorization => Headers.GetValueOrDefault("Authorization");

    /// <summary>Query parameters for GET, form-body parameters otherwise (decoded).</summary>
    public Dictionary<string, string> Params
    {
        get
        {
            var d = new Dictionary<string, string>();
            foreach (var (k, v) in ParseForm(Method == HttpMethod.Get ? Url.Query : Body)) d[k] = v;
            return d;
        }
    }

    public static List<KeyValuePair<string, string>> ParseForm(string form) =>
        form.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p =>
        {
            var i = p.IndexOf('=');
            var k = i < 0 ? p : p[..i];
            var v = i < 0 ? "" : p[(i + 1)..];
            return KeyValuePair.Create(Uri.UnescapeDataString(k.Replace('+', ' ')), Uri.UnescapeDataString(v.Replace('+', ' ')));
        }).ToList();
}

/// <summary>Fake transport: records every request, answers with the supplied function.</summary>
internal sealed class FakeHandler(Func<Recorded, HttpResponseMessage> responder) : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<Recorded> _requests = [];

    public IReadOnlyList<Recorded> Requests
    {
        get { lock (_gate) return [.. _requests]; }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) headers[h.Key] = string.Join(", ", h.Value);
        if (request.Content is not null)
            foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(", ", h.Value);
        var rec = new Recorded(request.Method, request.RequestUri!, headers, body);
        lock (_gate) _requests.Add(rec);
        return responder(rec);
    }
}

internal static class Http
{
    public static HttpResponseMessage Json(object? body, HttpStatusCode status = HttpStatusCode.OK) =>
        JsonText(body as string ?? JsonSerializer.Serialize(body), status);

    public static HttpResponseMessage JsonText(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Text(string text, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "text/plain") =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, mediaType) };

    public static HttpResponseMessage Html(string html, HttpStatusCode status = HttpStatusCode.OK) => Text(html, status, "text/html");

    public static HttpResponseMessage Redirect(string location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var res = new HttpResponseMessage(status);
        res.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return res;
    }

    public static (HttpClient Client, FakeHandler Handler) Client(Func<Recorded, HttpResponseMessage> responder)
    {
        var handler = new FakeHandler(responder);
        return (new HttpClient(handler), handler);
    }
}

/// <summary>
/// Independent OAuth 1.0a (RFC 5849) verifier: shares no code with Vusos.Core's OAuth1 (uses Uri.EscapeDataString and
/// HMACSHA1 directly), so a bug in the implementation cannot cancel itself out.
/// </summary>
internal static partial class OAuthVerifier
{
    [GeneratedRegex("(\\w+)=\"([^\"]*)\"")]
    private static partial Regex HeaderPair();

    public static Dictionary<string, string> ParseHeader(string? header)
    {
        var d = new Dictionary<string, string>();
        foreach (Match m in HeaderPair().Matches((header ?? "").Replace("OAuth ", "", StringComparison.Ordinal)))
            d[m.Groups[1].Value] = Uri.UnescapeDataString(m.Groups[2].Value);
        return d;
    }

    private static string Enc(string s) => Uri.EscapeDataString(s);

    public static bool Verify(Recorded r, string urlWithoutQuery, string consumerSecret, string tokenSecret)
    {
        var oauth = ParseHeader(r.Authorization);
        if (!oauth.Remove("oauth_signature", out var sig)) return false;
        var all = Recorded.ParseForm(r.Method == HttpMethod.Get ? r.Url.Query : r.Body).Concat(oauth);
        var paramString = string.Join('&', all
            .Select(p => (K: Enc(p.Key), V: Enc(p.Value)))
            .OrderBy(p => p.K, StringComparer.Ordinal).ThenBy(p => p.V, StringComparer.Ordinal)
            .Select(p => $"{p.K}={p.V}"));
        var baseString = $"{r.Method.Method}&{Enc(urlWithoutQuery)}&{Enc(paramString)}";
        var key = Encoding.UTF8.GetBytes($"{Enc(consumerSecret)}&{Enc(tokenSecret)}");
        var expected = Convert.ToBase64String(HMACSHA1.HashData(key, Encoding.UTF8.GetBytes(baseString)));
        return sig == expected;
    }
}
