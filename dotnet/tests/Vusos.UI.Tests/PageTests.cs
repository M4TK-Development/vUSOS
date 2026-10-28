using Bunit;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Services;
using Vusos.UI.Components;
using Vusos.UI.Pages;
using Vusos.WebHost.Dev;

namespace Vusos.UI.Tests;

public class PageTests : UiTestBase
{
    [Fact]
    public void Dashboard_renders_cards()
    {
        var cut = Render<Dashboard>();
        cut.WaitForAssertion(() => Assert.Contains("Dzisiejsze zajęcia", cut.Markup), Timeout);
        Assert.Contains("Terminy w ciągu 7 dni", cut.Markup);
        Assert.Contains("Ostatnie oceny", cut.Markup);
        Assert.Contains("Płatności", cut.Markup);
        Assert.Contains("Po terminie", cut.Markup); // zaległa opłata jest widoczna
        Assert.Contains("Ogłoszenia", cut.Markup);
        Assert.Contains("Cześć, Anna!", cut.Markup);
        Assert.DoesNotContain("z cache", cut.Markup);
    }

    [Fact]
    public void Dashboard_marks_cache_origin_when_offline()
    {
        Connectivity.SetOnline(false);
        var cut = Render<Dashboard>();
        cut.WaitForAssertion(() => Assert.Contains("z cache", cut.Markup), Timeout);
    }

    [Fact]
    public void Plan_shows_week_and_navigates()
    {
        var cut = Render<Plan>();
        cut.WaitForAssertion(() => Assert.Equal(7, cut.FindAll("section.day").Count), Timeout);
        var before = cut.Find(".page-header-text p").TextContent;
        cut.Find("button[aria-label='Następny tydzień']").Click();
        cut.WaitForAssertion(() => Assert.NotEqual(before, cut.Find(".page-header-text p").TextContent), Timeout);
        cut.WaitForAssertion(() => Assert.Equal(7, cut.FindAll("section.day").Count), Timeout);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.slot")), Timeout);
    }

    [Fact]
    public void Plan_opens_details_sheet()
    {
        var cut = Render<Plan>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.slot")), Timeout);
        cut.Find("button.slot").Click();
        Assert.NotEmpty(cut.FindAll("[role=dialog]"));
        Assert.Contains("Prowadzący", cut.Markup);
    }

    [Fact]
    public void Grades_group_by_course_with_unweighted_average_note()
    {
        var cut = Render<Grades>();
        cut.WaitForAssertion(() => Assert.Contains("Mikroekonomia", cut.Markup), Timeout);
        Assert.Contains("nieważona", cut.Markup);
        // semestr bieżący: 4,5 / 5 / 4 -> 4,50
        Assert.Contains("4,50", cut.Markup);
    }

    [Fact]
    public void Grades_platon_tab_lists_items()
    {
        var cut = Render<Grades>();
        cut.WaitForAssertion(() => Assert.Contains("Mikroekonomia", cut.Markup), Timeout);
        cut.FindAll("button[role=tab]").First(b => b.TextContent.Contains("Platona")).Click();
        cut.WaitForAssertion(() => Assert.Contains("Suma w kursie", cut.Markup), Timeout);
    }

    [Fact]
    public void Payments_show_totals_and_copy_account_number()
    {
        JSInterop.Setup<bool>("vusos.copyText", _ => true).SetResult(true);
        var cut = Render<Payments>();
        cut.WaitForAssertion(() => Assert.Contains("Do zapłaty", cut.Markup), Timeout);
        Assert.Contains("Po terminie", cut.Markup);
        Assert.Contains("odsetki", cut.Markup);
        cut.Find("button[aria-label^='Kopiuj numer']").Click();
        JSInterop.VerifyInvoke("vusos.copyText");
        Assert.Equal(FakeData.AccountNumber, JSInterop.Invocations["vusos.copyText"].Single().Arguments[0]);
    }

    [Fact]
    public void Messages_master_detail_and_compose()
    {
        var cut = Render<Messages>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("button.thread")), Timeout);
        Assert.Contains("Kontakt z dziekanatem", cut.Markup);
        cut.WaitForAssertion(() => Assert.Contains("dziekanat.ekonomia@vistula.edu.pl", cut.Markup), Timeout);

        cut.FindAll("button.thread")[0].Click();
        cut.WaitForAssertion(() => Assert.Contains("sali 118", cut.Markup), Timeout);
        Assert.Contains("has-selected", cut.Find(".msg-layout").ClassName);

        cut.Find("button.btn-primary").Click(); // „Nowa wiadomość”
        Assert.NotEmpty(cut.FindAll("form.form"));
    }

    [Fact]
    public void Courses_grid_with_progress_and_offline_badge()
    {
        var materials = Services.GetRequiredService<FakeMaterialService>();
        var cut = Render<Courses>();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("a.course-card").Count), Timeout);
        Assert.NotEmpty(cut.FindAll("[role=progressbar]"));
        Assert.DoesNotContain("offline:", cut.Markup);

        materials.EnqueueCourseAsync("101").GetAwaiter().GetResult();
        materials.ProcessQueueAsync().GetAwaiter().GetResult();
        var again = Render<Courses>();
        again.WaitForAssertion(() => Assert.Contains("offline:", again.Markup), Timeout);
    }

    [Fact]
    public void Course_detail_sections_download_and_open_local_file()
    {
        var cut = Render<CourseDetail>(p => p.Add(x => x.Id, "101"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("details.section")), Timeout);
        Assert.Contains("Wykład 1 – slajdy", cut.Markup);
        Assert.Contains("Dokumentacja Microsoft Learn", cut.Markup); // link wykładowcy z cache treści

        // plik zdalny: „Otwórz” używa adresu zdalnego
        cut.FindAll("button").First(b => b.TextContent.Contains("Otwórz")).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(Opener.Urls), Timeout);

        // pobranie offline
        cut.FindAll("button").First(b => b.TextContent.Contains("Pobierz offline")).Click();
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll(".file-row .badge-ok"), b => b.TextContent.Contains("offline")), TimeSpan.FromSeconds(15));

        // pobrany plik otwiera się lokalnie
        cut.FindAll("button").First(b => b.TextContent.Contains("Otwórz") && b.GetAttribute("disabled") is null).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(Opener.Files), Timeout);
    }

    [Fact]
    public void Course_detail_downloaded_files_open_locally_even_when_offline()
    {
        var materials = Services.GetRequiredService<FakeMaterialService>();
        materials.EnqueueCourseAsync("101").GetAwaiter().GetResult();
        materials.ProcessQueueAsync().GetAwaiter().GetResult();
        Connectivity.SetOnline(false);

        var cut = Render<CourseDetail>(p => p.Add(x => x.Id, "101"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("details.section")), Timeout);
        var open = cut.FindAll("button").First(b => b.TextContent.Contains("Otwórz") && b.GetAttribute("disabled") is null);
        open.Click();
        Assert.Single(Opener.Files.Take(1));
        Assert.True(File.Exists(Opener.Files[0]));
    }

    [Fact]
    public void Course_detail_download_whole_course()
    {
        var cut = Render<CourseDetail>(p => p.Add(x => x.Id, "101"));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("details.section")), Timeout);
        cut.FindAll("button").First(b => b.TextContent.Contains("Pobierz cały kurs")).Click();
        var materials = Services.GetRequiredService<FakeMaterialService>();
        cut.WaitForAssertion(() => Assert.True(materials.ListAsync("101").GetAwaiter().GetResult().Count >= 5), Timeout);
    }

    [Fact]
    public void Announcements_merge_both_sources_with_badges()
    {
        var cut = Render<Announcements>();
        cut.WaitForAssertion(() => Assert.Contains("Harmonogram sesji zimowej", cut.Markup), Timeout);
        Assert.Contains("Programowanie obiektowe: termin kolokwium", cut.Markup);
        Assert.Contains(">USOS<", cut.Markup);
        Assert.Contains(">Platon<", cut.Markup);
    }

    [Fact]
    public void Settings_connect_and_disconnect()
    {
        var cut = Render<Settings>();
        cut.WaitForAssertion(() => Assert.Contains("Połączono", cut.Markup), Timeout);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Rozłącz").Click();
        cut.FindAll(".dialog button").First(b => b.TextContent.Trim() == "Rozłącz").Click();
        cut.WaitForAssertion(() => Assert.Contains("Niepołączono", cut.Markup), Timeout);
        cut.FindAll("button").First(b => b.TextContent.Contains("Połącz z USOS")).Click();
        cut.WaitForAssertion(() => Assert.Equal(ConnectionStatus.Connected, Connections.GetStatusAsync().GetAwaiter().GetResult()[SourceSystem.Usos].Status), Timeout);
    }

    [Fact]
    public void Unconnected_system_shows_connect_prompt()
    {
        Connections.DisconnectAsync(SourceSystem.Platon).GetAwaiter().GetResult();
        var cut = Render<Courses>();
        cut.WaitForAssertion(() => Assert.Contains("Połącz z systemem Platon", cut.Markup), Timeout);
        Assert.NotEmpty(cut.FindAll("a[href='/ustawienia']"));
    }

    [Fact]
    public void Expired_session_shows_reconnect_link()
    {
        Backend.SetMode(SourceSystem.Usos, FakeSystemMode.AuthExpired);
        var cut = Render<Payments>();
        cut.WaitForAssertion(() => Assert.Contains("Połącz ponownie", cut.Markup), Timeout);
    }

    [Fact]
    public void Unavailable_system_without_cache_shows_unavailable()
    {
        Backend.SetMode(SourceSystem.Usos, FakeSystemMode.Unavailable);
        var cut = Render<Payments>();
        cut.WaitForAssertion(() => Assert.Contains("System niedostępny", cut.Markup), Timeout);
    }

    [Fact]
    public void Unavailable_system_with_cached_data_keeps_showing_data()
    {
        var cut = Render<Payments>();
        cut.WaitForAssertion(() => Assert.Contains("Do zapłaty", cut.Markup), Timeout);
        Backend.SetMode(SourceSystem.Usos, FakeSystemMode.Unavailable);
        cut.FindComponent<AsyncView<PaymentsSummary>>().InvokeAsync(() => cut.FindComponent<AsyncView<PaymentsSummary>>().Instance.ReloadAsync());
        cut.WaitForAssertion(() => Assert.Contains("System niedostępny, pokazuję dane z cache", cut.Markup), Timeout);
        Assert.Contains("Czesne", cut.Markup);
    }
}
