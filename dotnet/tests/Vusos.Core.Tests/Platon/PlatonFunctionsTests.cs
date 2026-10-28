using Vusos.Core.Adapters;
using Vusos.Core.Adapters.Platon;
using Vusos.Core.Domain;

namespace Vusos.Core.Tests.Platon;

public class PlatonFunctionsTests
{
    private const string Base = "https://platon.vistula.edu.pl/";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T10:00:00Z"); // 1793181600

    private static FakeTransport NewTransport() => new FakeTransport(
            rewrite: u => u + "#rewritten",
            headers: new Dictionary<string, string> { ["Cookie"] = "MoodleSession=x" })
        .OnFixture("core_webservice_get_site_info", "site_info")
        .OnFixture("core_course_get_enrolled_courses_by_timeline_classification", "enrolled_courses")
        .OnFixture("core_course_get_contents", "course_contents")
        .OnFixture("mod_assign_get_assignments", "assignments")
        .On("mod_assign_get_submission_status", args =>
            Fx.Num(args["assignid"]) == 8002 ? Fx.Fixture("submission_status_graded") : Fx.Fixture("submission_status_new"))
        .OnFixture("core_calendar_get_action_events_by_timesort", "action_events")
        .OnFixture("gradereport_user_get_grade_items", "grade_items")
        .OnFixture("mod_forum_get_forums_by_courses", "forums_by_courses")
        .On("mod_forum_get_forum_discussions", args =>
            Fx.Num(args["forumid"]) == 1 ? Fx.Fixture("forum_discussions_site") : Fx.Fixture("forum_discussions_course"));

    private static (PlatonFunctions Adapter, FakeTransport Transport) Setup(
        Action<FakeTransport>? customize = null,
        int? userId = null, bool embedFileAuth = false, IReadOnlyList<long>? newsCourseIds = null)
    {
        var t = NewTransport();
        customize?.Invoke(t);
        var options = new PlatonFunctionsOptions
        {
            BaseUrl = Base, UserId = userId, Clock = new FixedClock(Now), EmbedFileAuth = embedFileAuth,
            NewsCourseIds = newsCourseIds ?? [1]
        };
        return (new PlatonFunctions(t, options), t);
    }

    // ---------- helpers ----------

    [Fact]
    public void FromUnix_converts_timestamps()
    {
        Assert.Equal(DateTimeOffset.Parse("2026-10-28T10:00:00Z"), PlatonFunctions.FromUnix(1793181600));
        Assert.Null(PlatonFunctions.FromUnix(0));
        Assert.Null(PlatonFunctions.FromUnix(null));
    }

    [Fact]
    public void ToModuleType_maps_known_and_unknown()
    {
        Assert.Equal(LmsModuleType.Assign, PlatonFunctions.ToModuleType("assign"));
        Assert.Equal(LmsModuleType.Other, PlatonFunctions.ToModuleType("glossary"));
    }

    [Fact]
    public void StripPluginfilePlaceholders_removes_marker()
    {
        Assert.Equal("<img src=\"a.png\">", PlatonFunctions.StripPluginfilePlaceholders("<img src=\"@@PLUGINFILE@@/a.png\">"));
        Assert.Null(PlatonFunctions.StripPluginfilePlaceholders(""));
        Assert.Null(PlatonFunctions.StripPluginfilePlaceholders(null));
    }

    // ---------- site info ----------

    [Fact]
    public async Task GetSiteInfo_maps_site_info()
    {
        var (a, _) = Setup();
        Assert.Equal(new PlatonSiteInfo(48213, "Jan Kowalski", "Platon - Vistula University", "4.5.2 (Build: 20250113)"),
            await a.GetSiteInfoAsync());
    }

    [Fact]
    public async Task GetSiteInfo_falls_back_to_stored_user_id_when_unavailable()
    {
        var (a, _) = Setup(t => t.On("core_webservice_get_site_info", _ => throw new AdapterUnavailableException("nope")), userId: 5);
        Assert.Equal(5, (await a.GetSiteInfoAsync()).UserId);
    }

    [Fact]
    public async Task GetSiteInfo_does_not_hide_auth_errors_behind_stored_user_id()
    {
        var (a, _) = Setup(t => t.On("core_webservice_get_site_info", _ => throw new AdapterAuthException()), userId: 5);
        await Assert.ThrowsAsync<AdapterAuthException>(() => a.GetSiteInfoAsync());
    }

    [Fact]
    public async Task GetSiteInfo_rejects_unexpected_shape()
    {
        var (a, _) = Setup(t => t.On("core_webservice_get_site_info", """{"foo":1}"""));
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => a.GetSiteInfoAsync());
    }

    [Fact]
    public async Task GetSiteInfo_is_cached_but_failures_are_not()
    {
        var failures = 1;
        var (a, t) = Setup(tr => tr.On("core_webservice_get_site_info", _ =>
            failures-- > 0 ? throw new AdapterUnavailableException("flaky") : Fx.Fixture("site_info")));
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => a.GetSiteInfoAsync());
        await a.GetSiteInfoAsync();
        await a.GetSiteInfoAsync();
        Assert.Equal(2, t.CallsTo("core_webservice_get_site_info").Count);
    }

    // ---------- courses ----------

    [Fact]
    public async Task GetCourses_maps_courses()
    {
        var (a, t) = Setup();
        var courses = await a.GetCoursesAsync();
        Assert.Equal("all", t.Calls[0].Args["classification"]);
        Assert.Equal(2, courses.Count);
        var c = courses[0];
        Assert.Equal("3017", c.Id);
        Assert.Equal("INF-ALG-2025Z", c.ShortName);
        Assert.Equal("Informatyka 2025/26", c.Category);
        Assert.Equal(42.857142857143, c.Progress);
        Assert.Equal($"{Base}course/view.php?id=3017", c.Url);
        Assert.Equal("INF-ALG-2025Z", c.UsosCourseId);
        Assert.Equal(DateTimeOffset.Parse("2026-09-28T00:00:00Z"), c.StartDate);
        Assert.Equal(DateTimeOffset.Parse("2027-02-14T00:00:00Z"), c.EndDate);
        Assert.Null(c.LastAccess);
        Assert.Null(courses[1].Progress);
        Assert.Null(courses[1].EndDate);
        Assert.Null(courses[1].UsosCourseId);
    }

    [Fact]
    public async Task GetCourseContents_maps_sections_modules_and_files()
    {
        var (a, t) = Setup();
        var sections = await a.GetCourseContentsAsync("3017");

        Assert.Equal("core_course_get_contents", t.Calls[0].Fn);
        Assert.Equal(3017L, t.Calls[0].Args["courseid"]);
        Assert.Equal(["Wprowadzenie", "Tydzień 1", "Sekcja 2"], sections.Select(s => s.Name));
        Assert.Equal("<p><img src=\"banner.png\" alt=\"\"></p>", sections[1].SummaryHtml);

        var forum = sections[0].Modules[0];
        Assert.Equal(LmsModuleType.Forum, forum.Type);
        Assert.Equal("forum", forum.RawType);
        Assert.Null(forum.Completed);
        Assert.True(forum.Visible);
        Assert.Equal("3017", forum.CourseId);
        Assert.Equal("Wprowadzenie", forum.SectionName);
        Assert.Equal("7001", forum.InstanceId);

        var sylabus = sections[0].Modules[1];
        Assert.Equal(LmsModuleType.Resource, sylabus.Type);
        Assert.True(sylabus.Completed);
        var file = Assert.Single(sylabus.Files);
        Assert.Equal(new LmsFile(
            "Sylabus_ASD.pdf",
            "https://platon.vistula.edu.pl/webservice/pluginfile.php/520002/mod_resource/content/1/Sylabus_ASD.pdf?forcedownload=1",
            "application/pdf", 183422, DateTimeOffset.FromUnixTimeSeconds(1790561000)), file);

        var m = sections[1].Modules;
        Assert.Equal(LmsModuleType.Folder, m[0].Type);
        Assert.False(m[0].Completed);
        Assert.Equal(2, m[0].Files.Count);
        Assert.Null(m[0].Files[1].Modified); // timemodified 0
        Assert.Equal(LmsModuleType.Url, m[1].Type);
        Assert.Equal("https://visualgo.net/en", m[1].Url);
        Assert.Empty(m[1].Files);
        Assert.Equal(LmsModuleType.Assign, m[2].Type);
        Assert.Equal($"{Base}mod/assign/view.php?id=410012", m[2].Url);
        Assert.Equal("<p>Zaimplementuj <b>quicksort</b>.</p>", m[2].DescriptionHtml);
        Assert.Equal(LmsModuleType.Quiz, m[3].Type);
        Assert.Equal("9001", m[3].InstanceId);
        Assert.Equal(LmsModuleType.Other, m[4].Type);
        Assert.Equal("glossary", m[4].RawType);
        Assert.False(m[4].Visible);

        var label = sections[2].Modules[0];
        Assert.Equal(LmsModuleType.Label, label.Type);
        Assert.Equal("<p>Termin oddania: patrz kalendarz. <img src=\"x.png\"></p>", label.DescriptionHtml);
    }

    [Fact]
    public async Task GetCourseContents_does_not_embed_auth_in_file_urls_by_default_but_can()
    {
        var plain = await Setup().Adapter.GetCourseContentsAsync("3017");
        Assert.DoesNotContain("#rewritten", plain[0].Modules[1].Files[0].Url);
        var embedded = await Setup(embedFileAuth: true).Adapter.GetCourseContentsAsync("3017");
        Assert.Contains("#rewritten", embedded[0].Modules[1].Files[0].Url);
    }

    [Fact]
    public async Task GetCourseContents_rejects_a_non_numeric_course_id()
    {
        var (a, _) = Setup();
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => a.GetCourseContentsAsync("abc"));
    }

    // ---------- assignments ----------

    [Fact]
    public async Task GetAssignments_combines_with_submission_status_for_nearest_due()
    {
        var (a, t) = Setup();
        var list = await a.GetAssignmentsAsync();

        // posortowane po terminie, bez terminu na końcu
        Assert.Equal(["8002", "8001", "8101", "8003"], list.Select(x => x.Id));
        var statusCalls = t.CallsTo("mod_assign_get_submission_status").Select(c => Fx.Num(c.Args["assignid"])).Order().ToList();
        Assert.Equal([8001L, 8002L, 8101L], statusCalls); // 8003 nie ma terminu

        Assert.Equal("3017", list[0].CourseId);
        Assert.Equal("Algorytmy i struktury danych (INF-ALG-2025Z)", list[0].CourseName);
        Assert.Equal("Zadanie 0: Rozgrzewka", list[0].Name);
        Assert.True(list[0].Submitted);
        Assert.True(list[0].Graded);
        Assert.Equal("9,00 / 10,00", list[0].Grade);
        Assert.Equal($"{Base}mod/assign/view.php?id=410030", list[0].Url);

        Assert.False(list[1].Submitted);
        Assert.False(list[1].Graded);
        Assert.Null(list[1].Grade);
        Assert.Equal(DateTimeOffset.Parse("2026-11-03T22:59:00Z"), list[1].CutoffDate);
        Assert.Equal(DateTimeOffset.Parse("2026-10-30T21:59:00Z"), list[1].DueDate);
        Assert.Null(list[3].DueDate);
        Assert.False(list[3].Submitted);
    }

    [Fact]
    public async Task GetAssignments_limits_status_calls_to_the_nearest_and_survives_failures()
    {
        var assignments = string.Join(",", Enumerable.Range(0, 30)
            .Select(i => $$"""{"id":{{i + 1}},"cmid":{{100 + i}},"name":"A{{i}}","duedate":{{1793181600 + (i + 1) * 3600}}}"""));
        var (a, t) = Setup(tr => tr
            .On("mod_assign_get_assignments", $$"""{"courses":[{"id":1,"fullname":"C","assignments":[{{assignments}}]}]}""")
            .On("mod_assign_get_submission_status", _ => throw new AdapterAuthException("accessexception")));

        var list = await a.GetAssignmentsAsync();
        Assert.Equal(30, list.Count);
        Assert.Equal(20, t.CallsTo("mod_assign_get_submission_status").Count);
        Assert.All(list, x => Assert.False(x.Submitted));
    }

    [Fact]
    public async Task GetAssignments_honours_MaxSubmissionStatusCalls()
    {
        var t = NewTransport();
        var a = new PlatonFunctions(t, new PlatonFunctionsOptions { BaseUrl = Base, Clock = new FixedClock(Now), MaxSubmissionStatusCalls = 1 });
        await a.GetAssignmentsAsync();
        // najbliżej „teraz” (28.10 10:00Z) jest 8001 (30.10), a nie 8002 (20.10) ani 8101 (3.11)
        Assert.Equal(8001L, Fx.Num(Assert.Single(t.CallsTo("mod_assign_get_submission_status")).Args["assignid"]));
    }

    [Fact]
    public async Task GetAssignments_fetches_status_at_most_five_at_a_time()
    {
        var assignments = string.Join(",", Enumerable.Range(0, 20)
            .Select(i => $$"""{"id":{{i + 1}},"cmid":{{100 + i}},"name":"A{{i}}","duedate":{{1793181600 + (i + 1) * 3600}}}"""));
        var running = 0;
        var peak = 0;
        var t = NewTransport().On("mod_assign_get_assignments", $$"""{"courses":[{"id":1,"fullname":"C","assignments":[{{assignments}}]}]}""");
        var concurrent = new ConcurrencyProbeTransport(t, () =>
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
        }, () => Interlocked.Decrement(ref running));
        var a = new PlatonFunctions(concurrent, new PlatonFunctionsOptions { BaseUrl = Base, Clock = new FixedClock(Now) });
        await a.GetAssignmentsAsync();
        Assert.InRange(peak, 2, 5);
    }

    private sealed class ConcurrencyProbeTransport(IMoodleTransport inner, Action enter, Action exit) : IMoodleTransport
    {
        public Uri BaseUrl => inner.BaseUrl;
        public IReadOnlyDictionary<string, string> FileHeaders() => inner.FileHeaders();
        public string RewriteFileUrl(string url) => inner.RewriteFileUrl(url);

        public async Task<System.Text.Json.JsonElement> CallAsync(string wsfunction, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
        {
            if (wsfunction != "mod_assign_get_submission_status") return await inner.CallAsync(wsfunction, args, ct);
            enter();
            try
            {
                await Task.Delay(20, ct);
                return await inner.CallAsync(wsfunction, args, ct);
            }
            finally
            {
                exit();
            }
        }
    }

    // ---------- events ----------

    [Fact]
    public async Task GetUpcomingEvents_queries_calendar_window_and_maps_events()
    {
        var (a, t) = Setup();
        var events = await a.GetUpcomingEventsAsync(7);
        var args = t.Calls[0].Args;
        Assert.Equal(1793181600L, args["timesortfrom"]);
        Assert.Equal(1793181600L + 7 * 86400, args["timesortto"]);
        Assert.Equal(50, args["limitnum"]);
        Assert.Equal(true, args["limittononsuspendedevents"]);

        Assert.Equal(new LmsEvent(
            "70001", "3017", "Algorytmy i struktury danych (INF-ALG-2025Z)", "Zadanie 1: Sortowanie jest do oddania", "due", "assign",
            DateTimeOffset.Parse("2026-10-30T21:59:00Z"), null,
            "https://platon.vistula.edu.pl/mod/assign/view.php?id=410012&action=editsubmission", "Dodaj zgłoszenie"), events[0]);
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T12:30:00Z"), events[1].End);
        Assert.Equal("quiz", events[1].ModuleType);
        Assert.Null(events[2].CourseId);
        Assert.Null(events[2].CourseName);
        Assert.Null(events[2].ModuleType);
        Assert.Null(events[2].Url);
        Assert.Null(events[2].ActionName);
        Assert.Equal("course", events[2].Type);
    }

    [Fact]
    public async Task GetUpcomingEvents_defaults_to_14_days()
    {
        var (a, t) = Setup();
        await a.GetUpcomingEventsAsync();
        Assert.Equal(1793181600L + 14 * 86400, t.Calls[0].Args["timesortto"]);
    }

    // ---------- grades ----------

    [Fact]
    public async Task GetGrades_passes_courseid_and_user_id_from_site_info()
    {
        var (a, t) = Setup();
        var items = await a.GetGradesAsync("3017");
        var call = Assert.Single(t.CallsTo("gradereport_user_get_grade_items"));
        Assert.Equal(3017L, call.Args["courseid"]);
        Assert.Equal(48213L, call.Args["userid"]);
        Assert.Equal(3, items.Count);
        Assert.Equal(new LmsGradeItem(
            "123001", "3017", "Zadanie 0: Rozgrzewka", "mod", "9,00", 9, 10, "90,00 %", "<p>Dobra robota.</p>",
            DateTimeOffset.FromUnixTimeSeconds(1792600000)), items[0]);
        Assert.Null(items[1].GradeFormatted);
        Assert.Null(items[1].GradeRaw);
        Assert.Null(items[1].Percentage);
        Assert.Null(items[1].FeedbackHtml);
        Assert.Null(items[1].DateGraded);
        Assert.Equal("Suma końcowa", items[2].ItemName);
        Assert.Equal("course", items[2].ItemType);
    }

    [Fact]
    public async Task GetGrades_uses_stored_user_id_without_calling_site_info()
    {
        var (a, t) = Setup(userId: 77);
        await a.GetGradesAsync("3017");
        Assert.Empty(t.CallsTo("core_webservice_get_site_info"));
        Assert.Equal(77L, Fx.Num(t.Calls[0].Args["userid"]));
    }

    // ---------- announcements ----------

    [Fact]
    public async Task GetAnnouncements_reads_news_forums_newest_first()
    {
        var (a, t) = Setup(newsCourseIds: [1, 3017]);
        var items = await a.GetAnnouncementsAsync(10);

        Assert.Equal("mod_forum_get_forums_by_courses", t.Calls[0].Fn);
        Assert.Equal(new long[] { 1, 3017 }, Assert.IsAssignableFrom<IEnumerable<long>>(t.Calls[0].Args["courseids"]));
        // czytane są tylko fora "news" (1, 7001)
        Assert.Equal([1L, 7001L], t.CallsTo("mod_forum_get_forum_discussions").Select(c => Fx.Num(c.Args["forumid"])).Order());
        Assert.Equal(["platon:16001", "platon:15001", "platon:15002"], items.Select(i => i.Id));
        Assert.Equal(new Announcement(
            "platon:15001", SourceSystem.Platon, "Zmiana godzin pracy dziekanatu", "<p>Od poniedziałku dziekanat czynny 9-15.</p>",
            DateTimeOffset.FromUnixTimeSeconds(1793000000), "Dziekanat Informatyki", "Aktualności", $"{Base}mod/forum/discuss.php?d=15001"), items[1]);
        Assert.Equal("Rejestracja na lektoraty", items[2].Title); // pusty subject → name
        Assert.Null(items[2].Author);
    }

    [Fact]
    public async Task GetAnnouncements_defaults_to_site_news_course()
    {
        var (a, t) = Setup();
        await a.GetAnnouncementsAsync();
        Assert.Equal(new long[] { 1 }, Assert.IsAssignableFrom<IEnumerable<long>>(t.Calls[0].Args["courseids"]));
    }

    [Fact]
    public async Task GetAnnouncements_respects_limit_and_tolerates_one_failing_forum()
    {
        var (a, _) = Setup(t => t.On("mod_forum_get_forum_discussions", args =>
            Fx.Num(args["forumid"]) == 7001 ? throw new AdapterAuthException("x") : Fx.Fixture("forum_discussions_site")),
            newsCourseIds: [1, 3017]);
        var items = await a.GetAnnouncementsAsync(1);
        Assert.Equal("platon:15001", Assert.Single(items).Id);
    }

    // ---------- file download ----------

    [Fact]
    public async Task GetFileDownload_delegates_to_the_transport()
    {
        var (a, _) = Setup();
        var d = await a.GetFileDownloadAsync($"{Base}pluginfile.php/1/a.pdf");
        Assert.Equal($"{Base}pluginfile.php/1/a.pdf#rewritten", d.Url.ToString());
        Assert.Equal(new Dictionary<string, string> { ["Cookie"] = "MoodleSession=x" }, d.Headers);
    }

    [Fact]
    public async Task GetFileDownload_refuses_foreign_origins()
    {
        var (a, _) = Setup();
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => a.GetFileDownloadAsync("https://evil.example/pluginfile.php/1/a.pdf"));
    }

    // ---------- błędy ----------

    [Fact]
    public async Task Auth_errors_from_the_transport_propagate()
    {
        var (a, _) = Setup(t => t.On("core_course_get_contents", _ => throw new AdapterAuthException()));
        await Assert.ThrowsAsync<AdapterAuthException>(() => a.GetCourseContentsAsync("1"));
    }

    [Fact]
    public async Task Lenient_dtos_tolerate_missing_optional_fields_but_not_missing_required_ones()
    {
        var (a, _) = Setup(t => t
            .On("core_course_get_enrolled_courses_by_timeline_classification", """{"courses":[{"id":1,"fullname":"F","shortname":"S"}]}"""));
        var c = Assert.Single(await a.GetCoursesAsync());
        Assert.Null(c.Progress);
        Assert.Null(c.Category);

        var (b, _) = Setup(t => t
            .On("core_course_get_enrolled_courses_by_timeline_classification", """{"courses":[{"id":1,"shortname":"S"}]}"""));
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => b.GetCoursesAsync());

        var (d, _) = Setup(t => t
            .On("core_course_get_enrolled_courses_by_timeline_classification", """{"courses":[{"id":1,"fullname":null,"shortname":"S"}]}"""));
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => d.GetCoursesAsync());
    }

    [Fact]
    public async Task Numbers_sent_as_strings_are_accepted()
    {
        var (a, _) = Setup(t => t
            .On("core_course_get_enrolled_courses_by_timeline_classification",
                """{"courses":[{"id":"7","fullname":"F","shortname":"S","startdate":"1790553600","progress":"50.5"}]}"""));
        var c = Assert.Single(await a.GetCoursesAsync());
        Assert.Equal("7", c.Id);
        Assert.Equal(50.5, c.Progress);
        Assert.NotNull(c.StartDate);
    }
}
