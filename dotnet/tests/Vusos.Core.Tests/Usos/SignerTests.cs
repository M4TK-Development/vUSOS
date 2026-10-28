using System.Net;
using Vusos.Core.Adapters;
using Vusos.Core.Adapters.Usos;

namespace Vusos.Core.Tests.Usos;

public class SignerTests
{
    private const string Base = "https://usosapps.vistula.example/";
    private static readonly UsosApiCredentials Creds = new("atoken", "asecret", Base);

    [Fact]
    public async Task Local_GET_signs_the_query_and_calls_services_path()
    {
        var (http, handler) = Http.Client(_ => Http.Json(new { ok = true }));
        var signer = new LocalUsosSigner(http, "ckey", "csecret");
        using var res = await signer.SendAsync(HttpMethod.Get, "users/user",
            new Dictionary<string, string> { ["fields"] = "id|first_name", ["note"] = "a b&c*ł" }, Creds, default);

        var r = Assert.Single(handler.Requests);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal($"{Base}services/users/user", r.Url.GetLeftPart(UriPartial.Path));
        Assert.Equal("id|first_name", r.Params["fields"]);
        Assert.Equal("a b&c*ł", r.Params["note"]);
        Assert.Contains("oauth_signature_method=\"HMAC-SHA1\"", r.Authorization);
        Assert.Contains("oauth_consumer_key=\"ckey\"", r.Authorization);
        Assert.Contains("oauth_token=\"atoken\"", r.Authorization);
        Assert.True(OAuthVerifier.Verify(r, $"{Base}services/users/user", "csecret", "asecret"));
    }

    [Fact]
    public async Task Local_POST_signs_the_form_body()
    {
        var (http, handler) = Http.Client(_ => Http.Json(new { id = 1 }));
        var signer = new LocalUsosSigner(http, "ckey", "csecret");
        using var _ = await signer.SendAsync(HttpMethod.Post, "mailclient/send",
            new Dictionary<string, string> { ["subject"] = "Cześć! (test)", ["body"] = "x y" }, Creds, default);

        var r = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("", r.Url.Query);
        Assert.Equal("Cześć! (test)", r.Params["subject"]);
        Assert.StartsWith("application/x-www-form-urlencoded", r.Headers["Content-Type"]);
        Assert.True(OAuthVerifier.Verify(r, $"{Base}services/mailclient/send", "csecret", "asecret"));
    }

    [Fact]
    public async Task Local_tolerates_missing_trailing_slash_and_services_prefix()
    {
        var (http, handler) = Http.Client(_ => Http.Json(new { }));
        var signer = new LocalUsosSigner(http, "ckey", "csecret");
        var creds = Creds with { BaseUrl = "https://other.example/api" };
        using var _ = await signer.SendAsync(HttpMethod.Get, "/services/tt/user", new Dictionary<string, string>(), creds, default);
        Assert.Equal("https://other.example/api/services/tt/user", handler.Requests[0].Url.ToString());
    }

    [Fact]
    public async Task Local_without_base_url_is_a_configuration_error()
    {
        var (http, _) = Http.Client(_ => Http.Json(new { }));
        var signer = new LocalUsosSigner(http, "ckey", "csecret");
        var ex = await Assert.ThrowsAsync<AdapterUnavailableException>(() =>
            signer.SendAsync(HttpMethod.Get, "users/user", new Dictionary<string, string>(), Creds with { BaseUrl = "" }, default));
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task Remote_GET_forwards_query_and_token_headers_without_signing()
    {
        var (http, handler) = Http.Client(_ => Http.Json(new { ok = true }));
        var signer = new RemoteUsosSigner(http, new Uri("https://signing.example/"), "app-key");
        using var _ = await signer.SendAsync(HttpMethod.Get, "users/user",
            new Dictionary<string, string> { ["fields"] = "id|first_name" }, Creds, default);

        var r = Assert.Single(handler.Requests);
        Assert.Equal("https://signing.example/usos/proxy/users/user", r.Url.GetLeftPart(UriPartial.Path));
        Assert.Equal("id|first_name", r.Params["fields"]);
        Assert.Equal("atoken", r.Headers["X-Usos-Token"]);
        Assert.Equal("asecret", r.Headers["X-Usos-Token-Secret"]);
        Assert.Equal("app-key", r.Headers["X-Vusos-App-Key"]);
        Assert.Null(r.Authorization);
    }

    [Fact]
    public async Task Remote_POST_sends_the_form_body_and_omits_absent_app_key()
    {
        var (http, handler) = Http.Client(_ => Http.Json(new { id = 1 }));
        var signer = new RemoteUsosSigner(http, new Uri("https://signing.example"));
        using var _ = await signer.SendAsync(HttpMethod.Post, "services/mailclient/send",
            new Dictionary<string, string> { ["recipients"] = "a@b.pl|c@d.pl", ["subject"] = "S" }, Creds, default);

        var r = Assert.Single(handler.Requests);
        Assert.Equal("https://signing.example/usos/proxy/mailclient/send", r.Url.ToString());
        Assert.Equal("a@b.pl|c@d.pl", r.Params["recipients"]);
        Assert.False(r.Headers.ContainsKey("X-Vusos-App-Key"));
    }
}
