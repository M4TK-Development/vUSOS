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

    [Fact]
    public void Login_page_signs_in()
    {
        var anon = new BunitContext();
        anon.Services.AddVusosUi();
        var auth = new StubAuth(signedIn: false);
        anon.Services.AddSingleton<IAuthSession>(auth);
        var cut = anon.Render<Login>();
        Assert.Contains("Zaloguj kontem uczelni (M365)", cut.Markup);
        cut.Find("button.btn-primary").Click();
        Assert.True(auth.IsSignedIn);
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
