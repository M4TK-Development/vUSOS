using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vusos.Core.Adapters.Usos;

public sealed record UsosRequestToken(string Token, string Secret, string AuthorizeUrl);

public sealed record UsosAccessToken(string Token, string Secret);

/// <summary>Three-legged OAuth 1.0a against a USOS API installation (request token, then access token).</summary>
public interface IUsosOAuthClient
{
    Task<UsosRequestToken> RequestTokenAsync(string callbackUrl, IReadOnlyList<string> scopes, CancellationToken ct = default);

    /// <summary>A rejected verifier surfaces as <see cref="AdapterAuthException"/>.</summary>
    Task<UsosAccessToken> AccessTokenAsync(string token, string secret, string verifier, CancellationToken ct = default);
}

/// <summary>
/// Signs with the consumer secret on the device. Parameters (oauth_callback, scopes, oauth_verifier) are sent as an
/// x-www-form-urlencoded POST body and included in the HMAC-SHA1 signature.
/// </summary>
public sealed class LocalUsosOAuthClient(HttpClient http, string apiBaseUrl, string consumerKey, string consumerSecret) : IUsosOAuthClient
{
    private readonly string _base = UsosHttp.NormalizeBaseUrl(apiBaseUrl);

    private async Task<(int Status, string Text)> PostAsync(
        string path, List<KeyValuePair<string, string>> data, string? token, string? tokenSecret, CancellationToken ct)
    {
        var url = $"{_base}{path}";
        var header = OAuth1.CreateAuthorizationHeader("POST", new Uri(url), data, consumerKey, consumerSecret, token, tokenSecret);
        var (status, text) = await UsosHttp.SendForTextAsync(c =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(OAuth1.FormEncode(data), Encoding.UTF8, "application/x-www-form-urlencoded"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", header);
            req.Headers.TryAddWithoutValidation("Accept", "application/x-www-form-urlencoded, text/plain, */*");
            return http.SendAsync(req, c);
        }, $"USOS OAuth niedostępny ({path})", UsosHttp.DefaultTimeout, ct).ConfigureAwait(false);
        return ((int)status, text);
    }

    private static (string? Token, string? Secret) ParseTokens(string text)
    {
        var form = OAuth1.ParseForm(text);
        string? Get(string k) => form.FirstOrDefault(p => p.Key == k).Value;
        return (Get("oauth_token"), Get("oauth_token_secret"));
    }

    public async Task<UsosRequestToken> RequestTokenAsync(string callbackUrl, IReadOnlyList<string> scopes, CancellationToken ct = default)
    {
        var data = new List<KeyValuePair<string, string>> { new("oauth_callback", callbackUrl) };
        if (scopes.Count > 0) data.Add(new("scopes", string.Join('|', scopes)));
        var (status, text) = await PostAsync("services/oauth/request_token", data, null, null, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300)
            // 400/401 here means a bad consumer key/secret or an unregistered callback: a configuration problem.
            throw new AdapterUnavailableException($"USOS request_token: HTTP {status} {UsosHttp.Truncate(text, 300)}") { IsTransient = status >= 500 };
        var (token, secret) = ParseTokens(text);
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(secret))
            throw new AdapterUnavailableException("USOS request_token: brak oauth_token w odpowiedzi") { IsTransient = false };
        return new UsosRequestToken(token, secret, $"{_base}services/oauth/authorize?oauth_token={OAuth1.PercentEncode(token)}");
    }

    public async Task<UsosAccessToken> AccessTokenAsync(string token, string secret, string verifier, CancellationToken ct = default)
    {
        var (status, text) = await PostAsync(
            "services/oauth/access_token", [new("oauth_verifier", verifier)], token, secret, ct).ConfigureAwait(false);
        if (status is 401 or 400)
            throw new AdapterAuthException($"USOS access_token odrzucony (HTTP {status}): {UsosHttp.Truncate(text, 200)}");
        if (status is < 200 or >= 300)
            throw new AdapterUnavailableException($"USOS access_token: HTTP {status} {UsosHttp.Truncate(text, 300)}") { IsTransient = status >= 500 };
        var (t, s) = ParseTokens(text);
        if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(s))
            throw new AdapterUnavailableException("USOS access_token: brak oauth_token w odpowiedzi") { IsTransient = false };
        return new UsosAccessToken(t, s);
    }
}

/// <summary>Delegates both steps to Vusos.Server (<c>/usos/oauth/request_token</c>, <c>/usos/oauth/access_token</c>).</summary>
public sealed class RemoteUsosOAuthClient(HttpClient http, Uri signingServerUrl, string? appKey = null) : IUsosOAuthClient
{
    private async Task<(int Status, string Text)> PostJsonAsync(string path, JsonObject body, CancellationToken ct)
    {
        var url = $"{signingServerUrl.ToString().TrimEnd('/')}/{path}";
        var (status, text) = await UsosHttp.SendForTextAsync(c =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(appKey)) req.Headers.TryAddWithoutValidation(RemoteUsosSigner.AppKeyHeader, appKey);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return http.SendAsync(req, c);
        }, $"Serwer podpisujący niedostępny ({path})", UsosHttp.DefaultTimeout, ct).ConfigureAwait(false);
        return ((int)status, text);
    }

    private static JsonElement? Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement? obj, string name) =>
        obj is { } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public async Task<UsosRequestToken> RequestTokenAsync(string callbackUrl, IReadOnlyList<string> scopes, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["callbackUrl"] = callbackUrl,
            ["scopes"] = new JsonArray(scopes.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
        };
        var (status, text) = await PostJsonAsync("usos/oauth/request_token", body, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300)
            throw new AdapterUnavailableException($"USOS request_token: HTTP {status} {UsosHttp.ExtractMessage(text)}".Trim()) { IsTransient = status >= 500 };
        var json = Parse(text);
        var token = Str(json, "token");
        var secret = Str(json, "secret");
        var authorizeUrl = Str(json, "authorizeUrl");
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(authorizeUrl))
            throw new AdapterUnavailableException("USOS request_token: nieprawidłowa odpowiedź serwera podpisującego") { IsTransient = false };
        return new UsosRequestToken(token, secret, authorizeUrl);
    }

    public async Task<UsosAccessToken> AccessTokenAsync(string token, string secret, string verifier, CancellationToken ct = default)
    {
        var body = new JsonObject { ["token"] = token, ["secret"] = secret, ["verifier"] = verifier };
        var (status, text) = await PostJsonAsync("usos/oauth/access_token", body, ct).ConfigureAwait(false);
        if (status is 401 or 400)
            throw new AdapterAuthException($"USOS access_token odrzucony (HTTP {status}): {UsosHttp.ExtractMessage(text)}");
        if (status is < 200 or >= 300)
            throw new AdapterUnavailableException($"USOS access_token: HTTP {status} {UsosHttp.ExtractMessage(text)}".Trim()) { IsTransient = status >= 500 };
        var json = Parse(text);
        var t = Str(json, "token");
        var s = Str(json, "secret");
        if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(s))
            throw new AdapterUnavailableException("USOS access_token: nieprawidłowa odpowiedź serwera podpisującego") { IsTransient = false };
        return new UsosAccessToken(t, s);
    }
}
