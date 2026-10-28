using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Vusos.Core.Domain;
using Vusos.UI.Components;
using Vusos.UI.Layout;
using Vusos.UI.Pages;
using Vusos.UI.Services;
using Vusos.WebHost.Dev;

namespace Vusos.UI.Tests;

public class LayoutTests : UiTestBase
{
    [Fact]
    public void Main_layout_has_navigation_and_status_strip()
    {
        var cut = Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddMarkupContent(0, "<h1>Treść</h1>"))));
        cut.WaitForAssertion(() => Assert.Contains("Treść", cut.Markup), Timeout);
        foreach (var label in new[] { "Dziś", "Plan", "Oceny", "Płatności", "Wiadomości", "Kursy", "Testy", "Ogłoszenia", "Ustawienia" })
            Assert.Contains(label, cut.Markup);
        Assert.Contains("USOS", cut.Find(".status-strip").TextContent);
        Assert.Contains("Platon", cut.Find(".status-strip").TextContent);
    }

    [Fact]
    public void Status_strip_reports_offline_cache_time_and_pending()
    {
        var cut = Render<StatusStrip>();
        Connectivity.SetOnline(false);
        var sync = Services.GetRequiredService<FakeSyncStatus>();
        var at = new DateTimeOffset(2026, 10, 28, 12, 34, 0, TimeSpan.FromHours(1));
        cut.InvokeAsync(() => sync.Report(SourceSystem.Usos, DataOrigin.Cache, at));
        cut.InvokeAsync(() => sync.SetPending(SourceSystem.Platon, 2));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Tryb offline", cut.Markup);
            Assert.Contains("dane z 12:34", cut.Markup);
            Assert.Contains("2 zmiany do synchronizacji", cut.Markup);
        }, Timeout);
    }

    [Fact]
    public void Connection_banners_for_expired_and_disconnected_systems()
    {
        Connections.Set(SourceSystem.Usos, ConnectionStatus.Expired, "mock", null);
        Connections.Set(SourceSystem.Platon, ConnectionStatus.Error, "mock", "timeout");
        var cut = Render<ConnectionBanners>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".banner").Count), Timeout);
        Assert.Contains("wygasła", cut.Markup);
        Assert.Contains("timeout", cut.Markup);
        Assert.All(cut.FindAll(".banner a"), a => Assert.Equal("/ustawienia", a.GetAttribute("href")));
    }

    [Fact]
    public void Auth_gate_redirects_to_login_when_signed_out()
    {
        var anon = new BunitContext();
        anon.Services.AddVusosUi();
        anon.Services.AddSingleton<IAuthSession>(new StubAuth(signedIn: false));
        var cut = anon.Render<AuthGate>(p => p.AddChildContent("<p>sekret</p>"));
        Assert.DoesNotContain("sekret", cut.Markup);
        Assert.EndsWith("/logowanie", anon.Services.GetRequiredService<BunitNavigationManager>().Uri);
    }

    private static (BunitContext Ctx, StubAuth Auth, RecordingFlows Flows) AnonContext(bool external)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddVusosUi();
        var auth = new StubAuth(signedIn: false, external: external);
        var flows = new RecordingFlows();
        ctx.Services.AddSingleton<IAuthSession>(auth);
        ctx.Services.AddSingleton<IConnectFlows>(flows);
        return (ctx, auth, flows);
    }

    [Fact]
    public void Login_page_with_identity_provider_signs_in_and_links_to_direct_connect()
    {
        var (ctx, auth, flows) = AnonContext(external: true);
        var cut = ctx.Render<Login>();
        Assert.Contains("Zaloguj kontem uczelni (M365)", cut.Markup);
        Assert.Equal("/ustawienia", cut.Find("a[href='/ustawienia']").GetAttribute("href"));
        Assert.Contains("lub połącz bezpośrednio z Platonem/USOS", cut.Markup);
        Assert.DoesNotContain("Zaloguj przez Platon", cut.Markup);
        Assert.DoesNotContain("Zaloguj przez USOS", cut.Markup);
        Assert.Empty(cut.FindAll("details"));

        cut.Find("button.btn-primary").Click();

        Assert.True(auth.IsSignedIn);
        Assert.Empty(flows.Calls);
        Assert.EndsWith("/", ctx.Services.GetRequiredService<BunitNavigationManager>().Uri);
    }

    [Fact]
    public void Login_page_without_identity_provider_offers_platon_and_usos_sign_in()
    {
        var (ctx, _, flows) = AnonContext(external: false);
        var cut = ctx.Render<Login>();

        var primary = cut.FindAll("button.btn-primary").Select(b => b.TextContent.Trim()).ToList();
        Assert.Equal(["Zaloguj przez Platon (konto M365 uczelni)", "Zaloguj przez USOS (konto M365 uczelni)"], primary);
        Assert.DoesNotContain("Zaloguj kontem uczelni (M365)", cut.Markup);
        Assert.Contains("w przeglądarce systemowej", cut.Markup);
        Assert.Contains("nie widzi hasła", cut.Markup);
        Assert.Contains("nie wymaga zgody administratora", cut.Markup);

        cut.FindAll("button.btn-primary")[0].Click();
        cut.FindAll("button.btn-primary")[1].Click();
        Assert.Equal(["platon", "usos"], flows.Calls);
    }

    [Fact]
    public void Login_page_without_identity_provider_keeps_fallbacks_collapsed()
    {
        var (ctx, _, _) = AnonContext(external: false);
        var cut = ctx.Render<Login>();

        var outer = cut.FindAll("details").First(d => d.QuerySelector("summary")!.TextContent.Contains("Inne sposoby połączenia"));
        Assert.False(outer.HasAttribute("open"));
        Assert.Contains("Zatwierdź token", cut.Markup);
        Assert.Contains("Zapisz sesję", cut.Markup);
        Assert.Contains("Zapisz cookies", cut.Markup);
    }

    [Fact]
    public void Login_page_fallback_forms_use_the_same_handlers_as_settings()
    {
        var (ctx, _, flows) = AnonContext(external: false);
        var cut = ctx.Render<Login>();

        cut.Find("textarea[aria-label='Adres z tokenem']").Input("vusos://token=abc");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Zatwierdź token").Click();
        cut.Find("textarea[aria-label='Cookies USOSweb']").Input("PHPSESSID=1");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Zapisz cookies").Click();

        Assert.Equal(["platon-token:vusos://token=abc", "usos-cookies:PHPSESSID=1"], flows.Calls);
    }

    [Fact]
    public void Login_page_navigates_home_after_a_connection_signs_the_user_in()
    {
        var (ctx, auth, flows) = AnonContext(external: false);
        var nav = ctx.Services.GetRequiredService<BunitNavigationManager>();
        nav.NavigateTo("/logowanie");
        var cut = ctx.Render<Login>();
        Assert.EndsWith("/logowanie", nav.Uri);

        // Połączenie kończy się później (deep link / callback): ConnectionAuthState.Changed → IAuthSession.Changed.
        flows.OnStart = () => { };
        cut.FindAll("button.btn-primary")[0].Click();
        cut.InvokeAsync(() => auth.SignInAsync());

        cut.WaitForAssertion(() => Assert.EndsWith("localhost/", nav.Uri), Timeout);
    }

    [Fact]
    public void Login_page_shows_flow_errors_and_opens_the_fallbacks()
    {
        var (ctx, _, flows) = AnonContext(external: false);
        flows.Failure = new NotSupportedException("W przeglądarce wklej token z launch.php: otwórz …");
        var cut = ctx.Render<Login>();

        cut.FindAll("button.btn-primary")[0].Click();

        Assert.Contains("wklej token", cut.Find("[role=alert]").TextContent);
        var outer = cut.FindAll("details").First(d => d.QuerySelector("summary")!.TextContent.Contains("Inne sposoby połączenia"));
        Assert.True(outer.HasAttribute("open"));
    }

    [Fact]
    public void Login_page_redirects_home_when_already_signed_in()
    {
        var ctx = new BunitContext();
        ctx.Services.AddVusosUi();
        ctx.Services.AddSingleton<IAuthSession>(new StubAuth(signedIn: true));
        ctx.Services.AddSingleton<IConnectFlows>(new RecordingFlows());
        var nav = ctx.Services.GetRequiredService<BunitNavigationManager>();
        nav.NavigateTo("/logowanie");

        ctx.Render<Login>();

        Assert.EndsWith("localhost/", nav.Uri);
    }

    [Fact]
    public void Auth_gate_lets_signed_out_users_reach_settings_only()
    {
        var anon = new BunitContext();
        anon.Services.AddVusosUi();
        anon.Services.AddSingleton<IAuthSession>(new StubAuth(signedIn: false));
        var nav = anon.Services.GetRequiredService<BunitNavigationManager>();

        nav.NavigateTo("/ustawienia");
        var cut = anon.Render<AuthGate>(p => p.AddChildContent("<p>ustawienia-treść</p>"));
        Assert.Contains("ustawienia-treść", cut.Markup);
        Assert.EndsWith("/ustawienia", nav.Uri);

        nav.NavigateTo("/ustawienia?connected=usos");
        Assert.Contains("ustawienia-treść", cut.Markup);

        nav.NavigateTo("/kursy");
        cut.WaitForAssertion(() => Assert.EndsWith("/logowanie", nav.Uri), Timeout);
        Assert.DoesNotContain("ustawienia-treść", cut.Markup);
    }

    [Fact]
    public void Auth_gate_sends_the_user_to_login_after_signing_out_even_from_settings()
    {
        var anon = new BunitContext();
        anon.Services.AddVusosUi();
        var auth = new StubAuth(signedIn: true);
        anon.Services.AddSingleton<IAuthSession>(auth);
        var nav = anon.Services.GetRequiredService<BunitNavigationManager>();
        nav.NavigateTo("/ustawienia");
        var cut = anon.Render<AuthGate>(p => p.AddChildContent("<p>ustawienia-treść</p>"));
        Assert.Contains("ustawienia-treść", cut.Markup);

        cut.InvokeAsync(() => auth.SignOutAsync());

        cut.WaitForAssertion(() => Assert.EndsWith("/logowanie", nav.Uri), Timeout);
    }

    [Fact]
    public void Settings_logout_in_connection_mode_explains_disconnect_and_signs_out()
    {
        var cut = Render<Settings>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Wyloguj").Click();
        Assert.Contains("rozłączy USOS i Platon", cut.Find(".dialog").TextContent);

        cut.FindAll(".dialog button").First(b => b.TextContent.Trim() == "Wyloguj").Click();

        Assert.False(Services.GetRequiredService<IAuthSession>().IsSignedIn);
    }

    [Fact]
    public void Safe_html_component_sanitizes()
    {
        var cut = Render<SafeHtml>(p => p.Add(x => x.Html, "<p>ok</p><script>alert(1)</script><a href=\"javascript:x()\">l</a>"));
        Assert.DoesNotContain("script", cut.Markup);
        Assert.DoesNotContain("javascript", cut.Markup);
        Assert.Contains("<p>ok</p>", cut.Markup);
    }

    [Fact]
    public void Error_state_maps_exceptions()
    {
        var auth = Render<ErrorState>(p => p.Add(x => x.Error, new Vusos.Core.Adapters.AdapterAuthException()).Add(x => x.Source, SourceSystem.Platon));
        Assert.Contains("Połącz ponownie", auth.Markup);
        var na = Render<ErrorState>(p => p.Add(x => x.Error, new Vusos.Core.Adapters.AdapterUnavailableException("x")).Add(x => x.HasCache, true));
        Assert.Contains("System niedostępny, pokazuję dane z cache", na.Markup);
        var nc = Render<ErrorState>(p => p.Add(x => x.Error, new Vusos.Core.Adapters.NotConnectedException(SourceSystem.Usos)));
        Assert.Contains("Połącz z systemem USOS", nc.Markup);
    }
}

public class SignedOutSettingsTests : UiTestBase
{
    public SignedOutSettingsTests() : base(signedIn: false) { }

    [Fact]
    public void Settings_is_usable_while_signed_out_so_the_user_can_connect()
    {
        var cut = Render<Settings>();

        Assert.Contains("Niezalogowano", cut.Markup);
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Trim() == "Wyloguj");
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Contains("USOS"));
        Assert.Contains("Zatwierdź token", cut.Markup); // ConnectForms także w ustawieniach
    }
}
