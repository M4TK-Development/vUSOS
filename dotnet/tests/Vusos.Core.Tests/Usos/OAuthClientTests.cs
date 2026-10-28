using System.Net;
using Vusos.Core.Adapters;
using Vusos.Core.Adapters.Usos;

namespace Vusos.Core.Tests.Usos;

public class OAuthClientTests
{
    private const string Api = "https://usosapps.vistula.example";

    private static (LocalUsosOAuthClient Client, FakeHandler Handler) Local(HttpStatusCode status, string text)
    {
        var (http, handler) = Http.Client(_ => Http.Text(text, status));
        return (new LocalUsosOAuthClient(http, Api, "ckey", "csecret"), handler);
    }

    [Fact]
    public async Task Local_RequestToken_posts_signed_callback_and_pipe_separated_scopes_and_builds_authorize_url()
    {
        var (client, handler) = Local(HttpStatusCode.OK, "oauth_token=REQ%2Ftok&oauth_token_secret=REQsecret&oauth_callback_confirmed=true");
        var res = await client.RequestTokenAsync("https://app.example/api/connections/usos/oauth/callback", ["offline_access", "studies", "grades"]);

        Assert.Equal(new UsosRequestToken("REQ/tok", "REQsecret", $"{Api}/services/oauth/authorize?oauth_token=REQ%2Ftok"), res);
        var r = Assert.Single(handler.Requests);
        Assert.Equal($"{Api}/services/oauth/request_token", r.Url.ToString());
        Assert.Equal("https://app.example/api/connections/usos/oauth/callback", r.Params["oauth_callback"]);
        Assert.Equal("offline_access|studies|grades", r.Params["scopes"]);
        Assert.StartsWith("application/x-www-form-urlencoded", r.Headers["Content-Type"]);
        Assert.Contains("oauth_signature_method=\"HMAC-SHA1\"", r.Authorization);
        Assert.Contains("oauth_consumer_key=\"ckey\"", r.Authorization);
        Assert.True(OAuthVerifier.Verify(r, $"{Api}/services/oauth/request_token", "csecret", ""));
    }

    [Fact]
    public async Task Local_RequestToken_errors_are_unavailable()
    {
        var (bad, _) = Local(HttpStatusCode.Unauthorized, "bad consumer");
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => bad.RequestTokenAsync("https://cb", []));

        var (empty, _) = Local(HttpStatusCode.OK, "nothing=here");
        var ex = await Assert.ThrowsAsync<AdapterUnavailableException>(() => empty.RequestTokenAsync("https://cb", []));
        Assert.Contains("oauth_token", ex.Message);

        var http = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("ENOTFOUND")));
        var down = new LocalUsosOAuthClient(http, Api, "ckey", "csecret");
        var ex2 = await Assert.ThrowsAsync<AdapterUnavailableException>(() => down.RequestTokenAsync("https://cb", []));
        Assert.Contains("ENOTFOUND", ex2.Message);
        Assert.True(ex2.IsTransient);
    }

    [Fact]
    public async Task Local_RequestToken_omits_scopes_when_none_are_requested()
    {
        var (client, handler) = Local(HttpStatusCode.OK, "oauth_token=a&oauth_token_secret=b");
        await client.RequestTokenAsync("https://cb", []);
        Assert.False(handler.Requests[0].Params.ContainsKey("scopes"));
    }

    [Fact]
    public async Task Local_AccessToken_signs_with_the_request_token_and_verifier()
    {
        var (client, handler) = Local(HttpStatusCode.OK, "oauth_token=ACC&oauth_token_secret=ACCsecret");
        Assert.Equal(new UsosAccessToken("ACC", "ACCsecret"), await client.AccessTokenAsync("REQ", "REQsecret", "12345678"));

        var r = Assert.Single(handler.Requests);
        Assert.Equal($"{Api}/services/oauth/access_token", r.Url.ToString());
        Assert.Equal("12345678", r.Params["oauth_verifier"]);
        Assert.Contains("oauth_token=\"REQ\"", r.Authorization);
        Assert.True(OAuthVerifier.Verify(r, $"{Api}/services/oauth/access_token", "csecret", "REQsecret"));
    }

    [Fact]
    public async Task Local_AccessToken_rejected_verifier_is_auth_error_other_failures_are_unavailable()
    {
        await Assert.ThrowsAsync<AdapterAuthException>(() => Local(HttpStatusCode.Unauthorized, "Invalid verifier").Client.AccessTokenAsync("t", "s", "v"));
        var ex = await Assert.ThrowsAsync<AdapterUnavailableException>(() => Local(HttpStatusCode.InternalServerError, "oops").Client.AccessTokenAsync("t", "s", "v"));
        Assert.True(ex.IsTransient);
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => Local(HttpStatusCode.OK, "").Client.AccessTokenAsync("t", "s", "v"));
    }

    [Fact]
    public async Task Remote_RequestToken_posts_json_with_app_key_and_returns_server_authorize_url()
    {
        var (http, handler) = Http.Client(_ => Http.JsonText("""{"token":"T","secret":"S","authorizeUrl":"https://usosapps.vistula.example/services/oauth/authorize?oauth_token=T"}"""));
        var client = new RemoteUsosOAuthClient(http, new Uri("https://signing.example/"), "app-key");
        var res = await client.RequestTokenAsync("vusos://usos", ["offline_access", "studies"]);

        Assert.Equal(new UsosRequestToken("T", "S", "https://usosapps.vistula.example/services/oauth/authorize?oauth_token=T"), res);
        var r = Assert.Single(handler.Requests);
        Assert.Equal("https://signing.example/usos/oauth/request_token", r.Url.ToString());
        Assert.Equal("app-key", r.Headers["X-Vusos-App-Key"]);
        var body = Fx.Parse(r.Body);
        Assert.Equal("vusos://usos", body.GetProperty("callbackUrl").GetString());
        Assert.Equal(["offline_access", "studies"], body.GetProperty("scopes").EnumerateArray().Select(x => x.GetString()!).ToArray());
    }

    [Fact]
    public async Task Remote_AccessToken_posts_token_secret_verifier()
    {
        var (http, handler) = Http.Client(_ => Http.JsonText("""{"token":"ACC","secret":"ACCs"}"""));
        var client = new RemoteUsosOAuthClient(http, new Uri("https://signing.example"));
        Assert.Equal(new UsosAccessToken("ACC", "ACCs"), await client.AccessTokenAsync("REQ", "REQs", "999"));

        var r = Assert.Single(handler.Requests);
        Assert.Equal("https://signing.example/usos/oauth/access_token", r.Url.ToString());
        var body = Fx.Parse(r.Body);
        Assert.Equal(("REQ", "REQs", "999"), (body.GetProperty("token").GetString(), body.GetProperty("secret").GetString(), body.GetProperty("verifier").GetString()));
        Assert.False(r.Headers.ContainsKey("X-Vusos-App-Key"));
    }

    [Fact]
    public async Task Remote_errors_map_to_auth_or_unavailable()
    {
        var (rejected, _) = Http.Client(_ => Http.JsonText("""{"code":"OAUTH_REJECTED"}""", HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<AdapterAuthException>(() =>
            new RemoteUsosOAuthClient(rejected, new Uri("https://s.example")).AccessTokenAsync("t", "s", "v"));

        var (bad, _) = Http.Client(_ => Http.JsonText("""{"code":"UPSTREAM_ERROR","message":"USOS request_token: HTTP 500"}""", HttpStatusCode.BadGateway));
        var ex = await Assert.ThrowsAsync<AdapterUnavailableException>(() =>
            new RemoteUsosOAuthClient(bad, new Uri("https://s.example")).RequestTokenAsync("vusos://usos", []));
        Assert.Contains("HTTP 500", ex.Message);
        Assert.True(ex.IsTransient);

        var (garbage, _) = Http.Client(_ => Http.Text("not json"));
        await Assert.ThrowsAsync<AdapterUnavailableException>(() =>
            new RemoteUsosOAuthClient(garbage, new Uri("https://s.example")).RequestTokenAsync("vusos://usos", []));
    }
}
