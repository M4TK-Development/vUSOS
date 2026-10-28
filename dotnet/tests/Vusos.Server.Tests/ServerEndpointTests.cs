using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vusos.Server.Security;

namespace Vusos.Server.Tests;

public class ServerEndpointTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static Dictionary<string, string> ParseAuth(string header) =>
        Regex.Matches(header.Replace("OAuth ", ""), "(\\w+)=\"([^\"]*)\"")
            .ToDictionary(m => m.Groups[1].Value, m => Uri.UnescapeDataString(m.Groups[2].Value));

    /// <summary>Niezależnie przelicza podpis HMAC-SHA1 na podstawie tego, co faktycznie wysłano do USOS.</summary>
    private static bool SignatureValid(SeenRequest s, string tokenSecret)
    {
        var oauth = ParseAuth(s.Authorization!);
        var sig = oauth["oauth_signature"];
        oauth.Remove("oauth_signature");
        var pairs = new List<(string, string)>();
        foreach (var (k, v) in oauth) pairs.Add((k, v));
        foreach (var part in s.Uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            pairs.Add(KV(part));
        foreach (var part in s.Body.Split('&', StringSplitOptions.RemoveEmptyEntries))
            pairs.Add(KV(part));
        static (string, string) KV(string part)
        {
            var i = part.IndexOf('=');
            return (Uri.UnescapeDataString(part[..i]), Uri.UnescapeDataString(part[(i + 1)..]));
        }
        static string Enc(string v) => Uri.EscapeDataString(v).Replace("!", "%21").Replace("'", "%27").Replace("(", "%28").Replace(")", "%29").Replace("*", "%2A");
        var paramString = string.Join("&", pairs.Select(p => (Enc(p.Item1), Enc(p.Item2))).OrderBy(p => p.Item1, StringComparer.Ordinal).ThenBy(p => p.Item2, StringComparer.Ordinal).Select(p => $"{p.Item1}={p.Item2}"));
        var baseUri = s.Uri.GetLeftPart(UriPartial.Path);
        var sigBase = $"{s.Method.Method}&{Enc(baseUri)}&{Enc(paramString)}";
        var expected = Convert.ToBase64String(new HMACSHA1(Encoding.UTF8.GetBytes($"csecret&{tokenSecret}")).ComputeHash(Encoding.UTF8.GetBytes(sigBase)));
        return sig == expected;
    }

    [Fact]
    public async Task Health_reports_mode()
    {
        using var f = new ServerFactory();
        var r = await f.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        Assert.Equal("ok", j.GetProperty("status").GetString());
        Assert.Equal("live", j.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Missing_consumer_credentials_fail_fast_unless_mock()
    {
        using var broken = new ServerFactory().With("Usos:ConsumerKey", null).With("Usos:ConsumerSecret", null);
        Assert.ThrowsAny<Exception>(() => broken.CreateClient());

        using var mock = new ServerFactory().With("Usos:ConsumerKey", null).With("Usos:ConsumerSecret", null).With("Usos:Mode", "mock");
        var j = await Json(await mock.CreateClient().GetAsync("/health"));
        Assert.Equal("mock", j.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task RequestToken_happy_path_signs_callback_and_scopes()
    {
        using var f = new ServerFactory();
        f.Upstream.Respond = _ => FakeUpstream.Text(HttpStatusCode.OK, "oauth_token=REQ%2Ftok&oauth_token_secret=REQsecret&oauth_callback_confirmed=true");
        var r = await f.CreateClient().PostAsJsonAsync("/usos/oauth/request_token",
            new { callbackUrl = "vusos://usos", scopes = new[] { "offline_access", "studies", "grades" } });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        Assert.Equal("REQ/tok", j.GetProperty("token").GetString());
        Assert.Equal("REQsecret", j.GetProperty("secret").GetString());
        Assert.Equal("https://usos.example.edu.pl/services/oauth/authorize?oauth_token=REQ%2Ftok", j.GetProperty("authorizeUrl").GetString());

        var s = Assert.Single(f.Upstream.Seen);
        Assert.Equal(HttpMethod.Post, s.Method);
        Assert.Equal("https://usos.example.edu.pl/services/oauth/request_token", s.Uri.ToString());
        Assert.Equal("application/x-www-form-urlencoded", s.ContentType!.Split(';')[0]);
        var form = System.Web.HttpUtility.ParseQueryString(s.Body);
        Assert.Equal("vusos://usos", form["oauth_callback"]);
        Assert.Equal("offline_access|studies|grades", form["scopes"]);
        Assert.Contains("oauth_consumer_key=\"ckey\"", s.Authorization);
        Assert.Contains("oauth_signature_method=\"HMAC-SHA1\"", s.Authorization);
        Assert.True(SignatureValid(s, ""));
    }

    [Fact]
    public async Task RequestToken_omits_scopes_when_empty_and_rejects_bad_scheme()
    {
        using var f = new ServerFactory();
        f.Upstream.Respond = _ => FakeUpstream.Text(HttpStatusCode.OK, "oauth_token=a&oauth_token_secret=b");
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/usos/oauth/request_token", new { callbackUrl = "https://cb.example/x", scopes = Array.Empty<string>() })).StatusCode);
        Assert.DoesNotContain("scopes", f.Upstream.Seen[0].Body);

        foreach (var bad in new[] { "javascript:alert(1)", "file:///etc/passwd", "not a url", "" })
        {
            var r = await c.PostAsJsonAsync("/usos/oauth/request_token", new { callbackUrl = bad, scopes = Array.Empty<string>() });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
        Assert.Single(f.Upstream.Seen);
    }

    [Fact]
    public async Task RequestToken_upstream_failure_maps_to_502()
    {
        using var f = new ServerFactory();
        f.Upstream.Respond = _ => FakeUpstream.Text(HttpStatusCode.Unauthorized, "bad consumer");
        var r = await f.CreateClient().PostAsJsonAsync("/usos/oauth/request_token", new { callbackUrl = "vusos://usos", scopes = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.Equal("UPSTREAM_ERROR", (await Json(r)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AccessToken_happy_path_signs_with_request_token()
    {
        using var f = new ServerFactory();
        f.Upstream.Respond = _ => FakeUpstream.Text(HttpStatusCode.OK, "oauth_token=ACC&oauth_token_secret=ACCsecret");
        var r = await f.CreateClient().PostAsJsonAsync("/usos/oauth/access_token", new { token = "REQ", secret = "REQsecret", verifier = "12345678" });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await Json(r);
        Assert.Equal("ACC", j.GetProperty("token").GetString());
        Assert.Equal("ACCsecret", j.GetProperty("secret").GetString());
        var s = Assert.Single(f.Upstream.Seen);
        Assert.Equal("https://usos.example.edu.pl/services/oauth/access_token", s.Uri.ToString());
        Assert.Contains("oauth_token=\"REQ\"", s.Authorization);
        Assert.Equal("oauth_verifier=12345678", s.Body);
        Assert.True(SignatureValid(s, "REQsecret"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task AccessToken_rejection_maps_to_401(HttpStatusCode upstream)
    {
        using var f = new ServerFactory();
        f.Upstream.Respond = _ => FakeUpstream.Text(upstream, "Invalid verifier");
        var r = await f.CreateClient().PostAsJsonAsync("/usos/oauth/access_token", new { token = "t", secret = "s", verifier = "v" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("OAUTH_REJECTED", (await Json(r)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AccessToken_requires_all_fields_and_maps_server_errors()
    {
        using var f = new ServerFactory();
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/usos/oauth/access_token", new { token = "t" })).StatusCode);
        f.Upstream.Respond = _ => FakeUpstream.Text(HttpStatusCode.InternalServerError, "oops");
        Assert.Equal(HttpStatusCode.BadGateway, (await c.PostAsJsonAsync("/usos/oauth/access_token", new { token = "t", secret = "s", verifier = "v" })).StatusCode);
    }

    private static HttpRequestMessage ProxyGet(string path, string? token = "ACC", string? secret = "ACCsecret")
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (token != null) req.Headers.Add("X-Usos-Token", token);
        if (secret != null) req.Headers.Add("X-Usos-Token-Secret", secret);
        return req;
    }

    [Fact]
    public async Task Proxy_get_signs_query_and_passes_response_through()
    {
        using var f = new ServerFactory();
        f.Upstream.Respond = _ => FakeUpstream.Text((HttpStatusCode)418, "{\"first_name\":\"Zażółć\"}", "application/json");
        var r = await f.CreateClient().SendAsync(ProxyGet("/usos/proxy/users/user?fields=id%7Cfirst_name&lang=pl%20PL"));

        Assert.Equal((HttpStatusCode)418, r.StatusCode);
        Assert.Equal("application/json", r.Content.Headers.ContentType!.MediaType);
        Assert.Equal("{\"first_name\":\"Zażółć\"}", await r.Content.ReadAsStringAsync());

        var s = Assert.Single(f.Upstream.Seen);
        Assert.Equal(HttpMethod.Get, s.Method);
        Assert.Equal("https://usos.example.edu.pl/services/users/user", s.Uri.GetLeftPart(UriPartial.Path));
        Assert.Equal("?fields=id%7Cfirst_name&lang=pl%20PL", s.Uri.Query);
        Assert.Contains("oauth_token=\"ACC\"", s.Authorization);
        Assert.Contains("oauth_consumer_key=\"ckey\"", s.Authorization);
        Assert.True(SignatureValid(s, "ACCsecret"));
        // Sekrety użytkownika nie są przekazywane dalej jako nagłówki ani w podpisie jawnym.
        Assert.DoesNotContain("ACCsecret", s.Authorization);
    }

    [Fact]
    public async Task Proxy_post_signs_form_body()
    {
        using var f = new ServerFactory();
        f.Upstream.Respond = _ => FakeUpstream.Text(HttpStatusCode.OK, "[]", "application/json");
        var req = new HttpRequestMessage(HttpMethod.Post, "/usos/proxy/mailclient/send?x=1")
        {
            Content = new FormUrlEncodedContent([new("to", "a@b.pl"), new("body", "Cześć & witaj+")]),
        };
        req.Headers.Add("X-Usos-Token", "ACC");
        req.Headers.Add("X-Usos-Token-Secret", "ACCsecret");
        var r = await f.CreateClient().SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var s = Assert.Single(f.Upstream.Seen);
        Assert.Equal(HttpMethod.Post, s.Method);
        Assert.Equal("?x=1", s.Uri.Query);
        var form = System.Web.HttpUtility.ParseQueryString(s.Body);
        Assert.Equal("Cześć & witaj+", form["body"]);
        Assert.True(SignatureValid(s, "ACCsecret"));
    }

    [Theory]
    [InlineData("/usos/proxy/admin/secret")]
    [InlineData("/usos/proxy/services/users/user")]
    [InlineData("/usos/proxy/oauth/access_token")]
    [InlineData("/usos/proxy/users/../oauth/request_token")]
    [InlineData("/usos/proxy/users%2F..%2Foauth/x")]
    [InlineData("/usos/proxy/oauth/")]
    public async Task Proxy_rejects_paths_outside_whitelist(string path)
    {
        using var f = new ServerFactory();
        var r = await f.CreateClient().SendAsync(ProxyGet(path));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Empty(f.Upstream.Seen);
    }

    [Theory]
    [InlineData("users/user")]
    [InlineData("tt/user")]
    [InlineData("grades/terms2")]
    [InlineData("payments/user_payments")]
    [InlineData("courses/user")]
    [InlineData("terms/terms")]
    [InlineData("mailclient/messages")]
    [InlineData("news/search")]
    [InlineData("fac/faculty")]
    [InlineData("apisrv/installation")]
    [InlineData("oauth/revoke_token")]
    public async Task Proxy_allows_whitelisted_prefixes(string path)
    {
        using var f = new ServerFactory();
        var r = await f.CreateClient().SendAsync(ProxyGet("/usos/proxy/" + path));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("https://usos.example.edu.pl/services/" + path, f.Upstream.Seen.Single().Uri.GetLeftPart(UriPartial.Path));
    }

    [Theory]
    [InlineData(null, "s")]
    [InlineData("t", null)]
    [InlineData(null, null)]
    public async Task Proxy_requires_token_headers(string? token, string? secret)
    {
        using var f = new ServerFactory();
        var r = await f.CreateClient().SendAsync(ProxyGet("/usos/proxy/users/user", token, secret));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(f.Upstream.Seen);
    }

    [Fact]
    public async Task Proxy_rejects_client_supplied_oauth_params()
    {
        using var f = new ServerFactory();
        var r = await f.CreateClient().SendAsync(ProxyGet("/usos/proxy/users/user?oauth_token=evil"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Proxy_network_failure_maps_to_502()
    {
        using var f = new ServerFactory();
        f.Upstream.Respond = _ => throw new HttpRequestException("boom");
        var r = await f.CreateClient().SendAsync(ProxyGet("/usos/proxy/users/user"));
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        Assert.Equal("UPSTREAM_UNAVAILABLE", (await Json(r)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Proxy_timeout_maps_to_504()
    {
        using var f = new ServerFactory().With("Usos:TimeoutSeconds", "1");
        f.Upstream.RespondAsync = async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return FakeUpstream.Text(HttpStatusCode.OK, "late"); };
        var r = await f.CreateClient().SendAsync(ProxyGet("/usos/proxy/users/user"));
        Assert.Equal(HttpStatusCode.GatewayTimeout, r.StatusCode);
        Assert.Equal("UPSTREAM_TIMEOUT", (await Json(r)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task AppKey_is_required_when_configured()
    {
        using var f = new ServerFactory().With("Usos:AppKey", "sekret-app");
        var c = f.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(ProxyGet("/usos/proxy/users/user"))).StatusCode);
        var wrong = ProxyGet("/usos/proxy/users/user");
        wrong.Headers.Add(AppKeyMiddleware.HeaderName, "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(wrong)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await c.PostAsJsonAsync("/usos/oauth/request_token", new { callbackUrl = "vusos://usos", scopes = Array.Empty<string>() })).StatusCode);
        Assert.Empty(f.Upstream.Seen);

        var ok = ProxyGet("/usos/proxy/users/user");
        ok.Headers.Add(AppKeyMiddleware.HeaderName, "sekret-app");
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(ok)).StatusCode);

        // /health i callback (otwierany przez przeglądarkę) nie wymagają klucza
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health")).StatusCode);
        var cb = await f.NoRedirectClient().GetAsync("/usos/oauth/callback?oauth_token=a&oauth_verifier=b");
        Assert.Equal(HttpStatusCode.Redirect, cb.StatusCode);
    }

    [Fact]
    public async Task Callback_redirects_to_allowed_scheme()
    {
        using var f = new ServerFactory();
        var c = f.NoRedirectClient();
        var r = await c.GetAsync("/usos/oauth/callback?oauth_token=a%2Fb&oauth_verifier=123");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("vusos://usos?oauth_token=a%2Fb&oauth_verifier=123", r.Headers.Location!.OriginalString);

        var https = await c.GetAsync("/usos/oauth/callback?oauth_token=a&oauth_verifier=1&scheme=https");
        Assert.StartsWith("https://usos?", https.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("file")]
    [InlineData("evil.com/x")]
    public async Task Callback_rejects_disallowed_scheme(string scheme)
    {
        using var f = new ServerFactory();
        var r = await f.NoRedirectClient().GetAsync($"/usos/oauth/callback?oauth_token=a&oauth_verifier=1&scheme={Uri.EscapeDataString(scheme)}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Null(r.Headers.Location);
    }

    [Fact]
    public async Task Callback_requires_token()
    {
        using var f = new ServerFactory();
        Assert.Equal(HttpStatusCode.BadRequest, (await f.NoRedirectClient().GetAsync("/usos/oauth/callback")).StatusCode);
    }

    [Fact]
    public async Task Rate_limit_returns_429_per_ip()
    {
        using var f = new ServerFactory().With("RateLimit:PermitPerMinute", "2");
        var c = f.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(ProxyGet("/usos/proxy/users/user"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(ProxyGet("/usos/proxy/users/user"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.SendAsync(ProxyGet("/usos/proxy/users/user"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health")).StatusCode);
    }
}
