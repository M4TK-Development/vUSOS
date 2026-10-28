using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Hosting;
using Vusos.Core.Services;
using Vusos.UI.Services;
using Vusos.WebHost.Hosting;

namespace Vusos.WebHost.Tests;

/// <summary>Auth:Mode=anonymous: cookie vusos.sid, użytkownik anon:{id}, tożsamość z połączeń z USOS/Platonem.</summary>
public sealed class AnonymousAuthTests : IDisposable
{
    // live: bez połączeń (mock łączy się sam); mock: tożsamość = profil mock.
    private readonly WebHostFactory _live = new(adapters: "live", auth: "anonymous");
    private readonly WebHostFactory _mock = new(adapters: "mock", auth: "anonymous");

    public void Dispose()
    {
        _live.Dispose();
        _mock.Dispose();
    }

    private static string? SidCookie(HttpResponseMessage response)
        => response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(c => c.StartsWith(AnonymousSessions.CookieName + "=", StringComparison.Ordinal))
            : null;

    /// <summary>Odczytuje identyfikator sesji z nagłówka Set-Cookie (rozszyfrowuje cookie tak jak serwer).</summary>
    private static string SessionIdOf(WebHostFactory factory, HttpResponseMessage response)
    {
        var setCookie = SidCookie(response) ?? throw new Xunit.Sdk.XunitException("Brak cookie vusos.sid w odpowiedzi.");
        var value = setCookie[(AnonymousSessions.CookieName.Length + 1)..].Split(';')[0];
        var http = new DefaultHttpContext();
        http.Request.Headers.Cookie = $"{AnonymousSessions.CookieName}={value}";
        return factory.Services.GetRequiredService<AnonymousSessions>().TryRead(http)
               ?? throw new Xunit.Sdk.XunitException("Cookie vusos.sid nie przechodzi weryfikacji.");
    }

    [Fact]
    public void Anonymous_is_the_default_auth_mode_in_appsettings()
    {
        using var f = new WebHostFactory(auth: "");
        var options = f.Services.GetRequiredService<IOptions<AuthOptions>>().Value;
        Assert.True(options.IsAnonymous);
        Assert.Equal("anonymous", options.Mode);
    }

    [Fact]
    public async Task First_visit_gets_a_signed_http_only_cookie_and_lands_on_the_login_page()
    {
        var client = _live.NewClient();

        var first = await client.GetAsync("/");

        // Odpowiedź dokumentu wystawia cookie; przekierowanie na /logowanie robi AuthGate w obwodzie (prerendering jest wyłączony).
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var cookie = SidCookie(first);
        Assert.NotNull(cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        // 32 bajty w base64url = 43 znaki; cookie jest dodatkowo chronione Data Protection (dłuższe i nieczytelne).
        var id = SessionIdOf(_live, first);
        Assert.Equal(43, id.Length);
        Assert.DoesNotContain(id, cookie);

        var login = await client.GetAsync("/logowanie");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("blazor.web.js", await login.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Visitor_without_connections_is_signed_out_so_the_gate_sends_them_to_the_login_page()
    {
        using var scope = _live.CircuitScope(AnonymousSessions.CreatePrincipal("fresh-visitor"));
        var auth = scope.ServiceProvider.GetRequiredService<IAuthSession>();

        await auth.InitializeAsync();

        Assert.False(auth.IsSignedIn);
        Assert.Null(auth.DisplayName);
        Assert.False(auth.SupportsExternalIdentityProvider); // Login pokazuje „Zaloguj przez Platon / USOS”
        var status = await scope.ServiceProvider.GetRequiredService<IConnectionManager>().GetStatusAsync();
        Assert.All(status.Values, s => Assert.Equal(ConnectionStatus.Disconnected, s.Status));
    }

    [Fact]
    public async Task Returning_visitor_keeps_the_same_user_and_other_visitors_get_other_users()
    {
        var anna = _live.NewClient();
        var jan = _live.NewClient();

        var annaId = SessionIdOf(_live, await anna.GetAsync("/logowanie"));
        var annaAgain = await anna.GetAsync("/logowanie");
        var janId = SessionIdOf(_live, await jan.GetAsync("/logowanie"));

        Assert.Equal(annaId, SessionIdOf(_live, annaAgain)); // odnowione to samo cookie, nie nowa sesja
        Assert.NotEqual(annaId, janId);
    }

    [Fact]
    public async Task Tampered_cookie_is_replaced_by_a_fresh_session()
    {
        var client = _live.NewClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/logowanie");
        request.Headers.Add("Cookie", $"{AnonymousSessions.CookieName}=to-nie-jest-podpisane");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(SidCookie(response));
    }

    [Fact]
    public async Task Static_assets_and_health_do_not_create_sessions()
    {
        var client = _live.NewClient();

        Assert.Null(SidCookie(await client.GetAsync("/favicon.ico")));
        Assert.Null(SidCookie(await client.GetAsync("/health")));
    }

    [Fact]
    public async Task Dev_login_endpoint_is_not_available_in_anonymous_mode()
    {
        var response = await _live.NewClient().PostAsync("/auth/dev-login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = Emails.Anna,
            ["returnUrl"] = "/",
        }));

        // Endpoint nie istnieje poza trybem dev (404; UseStatusCodePagesWithReExecute zamienia go dla POST na 400) i nie loguje.
        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, response.StatusCode.ToString());
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith(AuthSetup.CookieName + "=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Login_endpoint_just_returns_to_the_app()
    {
        var client = _live.NewClient();
        var response = await client.GetAsync("/auth/login?returnUrl=%2Fkursy");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/kursy", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Session_without_connections_is_signed_out_and_a_mock_connect_gives_the_mock_profile()
    {
        var sid = "test-session-1";
        var userId = AnonymousSessions.UserIdOf(sid);
        var core = _mock.Cores.GetOrCreate(userId);
        var connections = core.Get<IConnectionManager>();
        await connections.DisconnectAsync(SourceSystem.Usos); // mock łączy się sam, więc zaczynamy od rozłączonego stanu
        await connections.DisconnectAsync(SourceSystem.Platon);

        using var scope = _mock.CircuitScope(AnonymousSessions.CreatePrincipal(sid));
        var auth = scope.ServiceProvider.GetRequiredService<IAuthSession>();
        Assert.IsAssignableFrom<ConnectionAuthSession>(auth);
        Assert.False(auth.SupportsExternalIdentityProvider);
        await auth.InitializeAsync();
        Assert.False(auth.IsSignedIn);

        var signedIn = new TaskCompletionSource();
        auth.Changed += () => { if (auth.IsSignedIn) signedIn.TrySetResult(); };
        await scope.ServiceProvider.GetRequiredService<IConnectFlows>().StartUsosAsync(); // tryb mock: łączy od razu
        await signedIn.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(auth.IsSignedIn);
        Assert.Equal("Anna Nowak", auth.DisplayName);
        Assert.Equal("anna.nowak@stu.vistula.example", auth.Email);
        Assert.Equal(userId, scope.ServiceProvider.GetRequiredService<ICurrentUserCore>().UserId);
    }

    [Fact]
    public async Task Mock_mode_auto_connects_so_a_new_visitor_is_signed_in_with_the_mock_profile()
    {
        var sid = "test-session-2";
        using var scope = _mock.CircuitScope(AnonymousSessions.CreatePrincipal(sid));
        var auth = scope.ServiceProvider.GetRequiredService<IAuthSession>();

        await auth.InitializeAsync();

        Assert.True(auth.IsSignedIn);
        Assert.Equal("Anna Nowak", auth.DisplayName);
    }

    [Fact]
    public async Task Identity_is_per_visitor()
    {
        var a = AnonymousSessions.CreatePrincipal("sess-a");
        var b = AnonymousSessions.CreatePrincipal("sess-b");
        var coreB = _mock.Cores.GetOrCreate(AnonymousSessions.UserIdOf("sess-b")).Get<IConnectionManager>();
        await coreB.DisconnectAsync(SourceSystem.Usos);
        await coreB.DisconnectAsync(SourceSystem.Platon);
        using var scopeA = _mock.CircuitScope(a);
        using var scopeB = _mock.CircuitScope(b);
        var authA = scopeA.ServiceProvider.GetRequiredService<IAuthSession>();
        var authB = scopeB.ServiceProvider.GetRequiredService<IAuthSession>();

        await authA.InitializeAsync();
        await authB.InitializeAsync();

        Assert.True(authA.IsSignedIn);
        Assert.False(authB.IsSignedIn);
    }

    [Fact]
    public async Task Logout_disconnects_wipes_the_data_directory_and_deletes_the_cookie()
    {
        var client = _mock.NewClient();
        var visit = await client.GetAsync("/logowanie");
        var sid = SessionIdOf(_mock, visit);
        var userId = AnonymousSessions.UserIdOf(sid);
        var core = _mock.Cores.GetOrCreate(userId);
        var connections = core.Get<IConnectionManager>();
        await connections.ConnectUsosAsync(new UsosMockCredentials());
        var dir = core.Paths.DataDirectory;
        Assert.True(File.Exists(Path.Combine(dir, "secure.json")));
        Assert.True(core.Get<ConnectionIdentity>().IsSignedIn || await WaitSignedInAsync(core));

        var logout = await client.GetAsync("/auth/logout");

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/logowanie", logout.Headers.Location!.OriginalString);
        var deleted = SidCookie(logout);
        Assert.NotNull(deleted);
        Assert.Contains("expires=", deleted, StringComparison.OrdinalIgnoreCase); // usunięcie = cookie z datą w przeszłości
        Assert.False(Directory.Exists(dir));
        Assert.Equal(0, _mock.Cores.LoadedCount);
        Assert.Equal(ConnectionStatus.Disconnected, (await connections.GetStatusAsync())[SourceSystem.Usos].Status);

        // Kolejna wizyta to nowy, pusty użytkownik.
        var next = await client.GetAsync("/logowanie");
        Assert.NotEqual(sid, SessionIdOf(_mock, next));
    }

    [Fact]
    public async Task Cross_site_logout_request_signs_out_but_does_not_wipe_data()
    {
        var client = _mock.NewClient();
        var sid = SessionIdOf(_mock, await client.GetAsync("/logowanie"));
        var core = _mock.Cores.GetOrCreate(AnonymousSessions.UserIdOf(sid));
        var dir = core.Paths.DataDirectory;
        Assert.True(Directory.Exists(dir));

        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/logout");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        var logout = await client.SendAsync(request);

        Assert.Equal("/logowanie", logout.Headers.Location!.OriginalString);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public async Task Usos_callback_identifies_the_user_by_the_session_cookie()
    {
        var client = _live.NewClient();
        await client.GetAsync("/logowanie"); // wystawia cookie

        var response = await client.GetAsync("/connect/usos/callback?oauth_token=abc&oauth_verifier=xyz");

        // Z cookie callback dociera do flow użytkownika (brak rozpoczętego logowania = błąd), a nie na /auth/login.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/ustawienia?error=", location);
        Assert.Contains(Uri.EscapeDataString("Brak oczekującej autoryzacji USOS"), location);
    }

    [Fact]
    public async Task Settings_page_is_reachable_without_connections()
    {
        var client = _live.NewClient();
        await client.GetAsync("/logowanie");

        var response = await client.GetAsync("/ustawienia");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Anonymous_mode_is_allowed_in_production()
    {
        using var f = new WebHostFactory(auth: "anonymous").WithWebHostBuilder(b => b.UseEnvironment("Production"));
        _ = f.Services;
    }

    [Fact]
    public async Task Fake_adapters_with_anonymous_auth_serve_the_shell()
    {
        using var fake = new WebHostFactory(adapters: "fake", auth: "anonymous");
        var client = fake.NewClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(SidCookie(response));
    }

    [Fact]
    public void Unknown_auth_mode_lists_anonymous_among_the_options()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var f = new WebHostFactory(auth: "magia");
            _ = f.Services;
        });
        Assert.Contains("anonymous", ex.Message);
    }

    private static async Task<bool> WaitSignedInAsync(UserCore core)
    {
        var identity = core.Get<ConnectionIdentity>();
        for (var i = 0; i < 100 && !identity.IsSignedIn; i++)
        {
            await identity.RefreshAsync();
            await Task.Delay(20);
        }
        return identity.IsSignedIn;
    }
}
