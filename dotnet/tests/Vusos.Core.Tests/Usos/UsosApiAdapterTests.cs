using System.Net;
using Vusos.Core.Adapters;
using Vusos.Core.Adapters.Usos;
using Vusos.Core.Domain;

namespace Vusos.Core.Tests.Usos;

public class UsosApiAdapterTests
{
    private const string Base = "https://usosapps.vistula.example/";
    private static readonly UsosApiCredentials Creds = new("atoken", "asecret", Base);

    private sealed record Call(string Method, string Path, Dictionary<string, string> Params, Recorded Raw);

    private sealed class Harness
    {
        public required UsosApiAdapter Adapter { get; init; }
        public required FakeHandler Handler { get; init; }
        public List<Call> Calls => Handler.Requests.Select(r => new Call(r.Method.Method, r.Path, r.Params, r)).ToList();
        public List<Call> To(string path) => Calls.Where(c => c.Path == path).ToList();
    }

    /// <summary>Routes are keyed by wire path (services/...); unknown paths answer like a USOS without that module.</summary>
    private static Harness Make(Dictionary<string, Func<Call, HttpResponseMessage>> routes, string now = "2025-11-20T10:00:00Z")
    {
        var handler = new FakeHandler(r =>
        {
            var call = new Call(r.Method.Method, r.Path, r.Params, r);
            return routes.TryGetValue(r.Path, out var route)
                ? route(call)
                : Http.Json(new { message = $"Method '{r.Path}' not found" }, HttpStatusCode.BadRequest);
        });
        var signer = new LocalUsosSigner(new HttpClient(handler), "ckey", "csecret");
        return new Harness { Adapter = new UsosApiAdapter(signer, Creds, new FixedClock(now)), Handler = handler };
    }

    private static HttpResponseMessage Fixture(string name) => Http.JsonText(Fx.Text(name));

    // ---------- transport and error mapping ----------

    [Fact]
    public async Task Signed_request_is_valid_and_uses_the_documented_profile_fields()
    {
        var h = Make(new() { ["services/users/user"] = _ => Fixture("user.json") });
        var p = await h.Adapter.GetProfileAsync();

        Assert.Equal("Anna", p.FirstName);
        var call = Assert.Single(h.Calls);
        Assert.Contains("photo_urls[200x200]", call.Params["fields"]);
        Assert.Contains("student_programmes[", call.Params["fields"]);
        Assert.True(OAuthVerifier.Verify(call.Raw, $"{Base}services/users/user", "csecret", "asecret"));
    }

    [Fact]
    public async Task Http_401_is_an_auth_error_carrying_the_usos_message()
    {
        var h = Make(new() { ["services/users/user"] = _ => Http.Json(new { message = "Invalid token" }, HttpStatusCode.Unauthorized) });
        var ex = await Assert.ThrowsAsync<AdapterAuthException>(() => h.Adapter.GetProfileAsync());
        Assert.Contains("Invalid token", ex.Message);
    }

    [Fact]
    public async Task Http_5xx_is_transient_unavailable()
    {
        var h = Make(new() { ["services/users/user"] = _ => Http.Json(new { message = "Boom" }, HttpStatusCode.InternalServerError) });
        var ex = await Assert.ThrowsAsync<UsosHttpException>(() => h.Adapter.GetProfileAsync());
        Assert.True(ex.IsTransient);
        Assert.Equal(500, ex.Status);
        Assert.Contains("HTTP 500", ex.Message);
        Assert.Contains("Boom", ex.Message);
    }

    [Fact]
    public async Task Http_400_with_usos_error_json_is_permanent_and_carries_the_message()
    {
        var h = Make(new() { ["services/users/user"] = _ => Http.Json(new { message = "Invalid argument: fields" }, HttpStatusCode.BadRequest) });
        var ex = await Assert.ThrowsAsync<UsosHttpException>(() => h.Adapter.GetProfileAsync());
        Assert.False(ex.IsTransient);
        Assert.Contains("Invalid argument: fields", ex.Message);
        Assert.False(ex.IsMethodNotFound);
    }

    [Fact]
    public async Task Plain_text_error_bodies_are_reported()
    {
        var h = Make(new() { ["services/users/user"] = _ => Http.Text("plain text", HttpStatusCode.BadGateway) });
        var ex = await Assert.ThrowsAsync<UsosHttpException>(() => h.Adapter.GetProfileAsync());
        Assert.Contains("plain text", ex.Message);
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task Invalid_json_and_network_failures_are_unavailable()
    {
        var bad = Make(new() { ["services/users/user"] = _ => Http.Text("<html>") });
        var ex = await Assert.ThrowsAsync<AdapterUnavailableException>(() => bad.Adapter.GetProfileAsync());
        Assert.Contains("JSON", ex.Message);

        var handler = new FakeHandler(_ => throw new HttpRequestException("ECONNREFUSED"));
        var adapter = new UsosApiAdapter(new LocalUsosSigner(new HttpClient(handler), "k", "s"), Creds, new FixedClock("2025-11-20T10:00:00Z"));
        var net = await Assert.ThrowsAsync<AdapterUnavailableException>(() => adapter.GetProfileAsync());
        Assert.True(net.IsTransient);
        Assert.Contains("ECONNREFUSED", net.Message);
    }

    [Fact]
    public async Task Timeouts_are_transient_but_caller_cancellation_is_not_swallowed()
    {
        var timeout = new UsosApiAdapter(
            new LocalUsosSigner(new HttpClient(new FakeHandler(_ => throw new TaskCanceledException("timeout"))), "k", "s"),
            Creds, new FixedClock("2025-11-20T10:00:00Z"));
        var ex = await Assert.ThrowsAsync<AdapterUnavailableException>(() => timeout.GetProfileAsync());
        Assert.True(ex.IsTransient);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var h = Make(new() { ["services/users/user"] = _ => Fixture("user.json") });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Adapter.GetProfileAsync(cts.Token));
    }

    // ---------- terms / courses ----------

    [Fact]
    public async Task GetTerms_uses_terms_attached_to_course_editions_cached_and_flags_the_current_one()
    {
        var h = Make(new() { ["services/courses/user"] = _ => Fixture("courses-user.json") });
        var terms = await h.Adapter.GetTermsAsync();
        Assert.Equal([("2025Z", true), ("2024L", false)], terms.Select(t => (t.Id, t.IsCurrent)));
        await h.Adapter.GetTermsAsync();
        var calls = h.To("services/courses/user");
        Assert.Single(calls);
        Assert.Equal("false", calls[0].Params["active_terms_only"]);
    }

    [Fact]
    public async Task GetTerms_falls_back_to_terms_search_when_courses_carry_no_terms()
    {
        var h = Make(new()
        {
            ["services/courses/user"] = _ => Http.JsonText("""{"course_editions":{}}"""),
            ["services/terms/search"] = c => Http.JsonText(c.Params["query"] == "2025"
                ? """[{"id":"2025Z","name":{"pl":"Zimowy 2025/26"},"start_date":"2025-10-01","end_date":"2026-02-15"}]"""
                : "[]"),
        });
        var terms = await h.Adapter.GetTermsAsync();
        Assert.Equal(["2025Z"], terms.Select(t => t.Id));
        Assert.Equal(["2024", "2025"], h.To("services/terms/search").Select(c => c.Params["query"]));
    }

    [Fact]
    public async Task GetCourses_defaults_to_active_terms_and_filters_by_term_when_given()
    {
        var h = Make(new() { ["services/courses/user"] = _ => Fixture("courses-user.json") });
        await h.Adapter.GetCoursesAsync();
        var first = h.To("services/courses/user")[0];
        Assert.Equal("true", first.Params["active_terms_only"]);
        Assert.Contains("user_groups[course_unit_id|class_type|group_number|lecturers[first_name|last_name]]", first.Params["fields"]);

        Assert.Equal(["INF-AM2"], (await h.Adapter.GetCoursesAsync("2024L")).Select(c => c.CourseId));
        Assert.Equal("false", h.To("services/courses/user")[1].Params["active_terms_only"]);
    }

    // ---------- timetable ----------

    [Fact]
    public async Task GetTimetable_walks_the_range_in_7_day_chunks_and_resolves_lecturer_names_once()
    {
        var h = Make(new()
        {
            ["services/tt/user"] = c => c.Params["start"] == "2025-10-06" ? Fixture("tt-user.json") : Http.JsonText("[]"),
            ["services/users/users"] = _ => Fixture("users-users.json"),
        });
        var entries = await h.Adapter.GetTimetableAsync(new(2025, 10, 6), new(2025, 10, 20));

        var tt = h.To("services/tt/user").OrderBy(c => c.Params["start"]).ToList();
        Assert.Equal([("2025-10-06", "7"), ("2025-10-13", "7"), ("2025-10-20", "1")], tt.Select(c => (c.Params["start"], c.Params["days"])));
        Assert.Contains("lecturer_ids", tt[0].Params["fields"]);
        var users = Assert.Single(h.To("services/users/users"));
        Assert.Equal("101|102", users.Params["user_ids"]);
        Assert.Equal(2, entries.Count);
        Assert.Equal(["Marek Nowicki", "Tomasz Lis"], entries[0].Lecturers);
        Assert.True(entries[0].Start < entries[1].Start);

        await h.Adapter.GetTimetableAsync(new(2025, 10, 6), new(2025, 10, 6)); // names cached
        Assert.Single(h.To("services/users/users"));
    }

    [Fact]
    public async Task GetTimetable_caps_the_range_at_93_days()
    {
        var h = Make(new() { ["services/tt/user"] = _ => Http.JsonText("""{"activities":[]}""") });
        await h.Adapter.GetTimetableAsync(new(2025, 9, 1), new(2026, 9, 1));
        var tt = h.To("services/tt/user").OrderBy(c => c.Params["start"]).ToList();
        Assert.Equal(14, tt.Count); // 13 full weeks + 2 days = 93 days
        Assert.Equal("2", tt[^1].Params["days"]);
        Assert.Equal("2025-12-01", tt[^1].Params["start"]);
    }

    [Fact]
    public async Task GetTimetable_survives_a_failing_lecturer_lookup_but_not_an_expired_token()
    {
        var ok = Make(new()
        {
            ["services/tt/user"] = _ => Fixture("tt-user.json"),
            ["services/users/users"] = _ => Http.Json(new { message = "x" }, HttpStatusCode.InternalServerError),
        });
        Assert.Empty((await ok.Adapter.GetTimetableAsync(new(2025, 10, 6), new(2025, 10, 6)))[0].Lecturers);

        var expired = Make(new()
        {
            ["services/tt/user"] = _ => Fixture("tt-user.json"),
            ["services/users/users"] = _ => Http.Json(new { message = "expired" }, HttpStatusCode.Unauthorized),
        });
        await Assert.ThrowsAsync<AdapterAuthException>(() => expired.Adapter.GetTimetableAsync(new(2025, 10, 6), new(2025, 10, 6)));
        Assert.Empty(await ok.Adapter.GetTimetableAsync(new(2025, 10, 7), new(2025, 10, 6)));
    }

    // ---------- grades / payments ----------

    [Fact]
    public async Task GetGrades_defaults_to_every_term_of_the_user_pipe_joins_ids_and_enriches_names()
    {
        var h = Make(new()
        {
            ["services/courses/user"] = _ => Fixture("courses-user.json"),
            ["services/grades/terms2"] = _ => Fixture("grades-terms2.json"),
        });
        var grades = await h.Adapter.GetGradesAsync();
        Assert.Equal("2025Z|2024L", h.To("services/grades/terms2")[0].Params["term_ids"]);
        var unit = grades.Single(g => g.CourseId == "INF-ALG" && g.ClassType == "Ćwiczenia");
        Assert.Equal(new LangText("Algorytmy i struktury danych", "Algorithms and data structures"), unit.CourseName);

        await h.Adapter.GetGradesAsync(["2024L"]);
        Assert.Equal("2024L", h.To("services/grades/terms2")[^1].Params["term_ids"]);
    }

    [Fact]
    public async Task GetGrades_retries_without_fields_when_terms2_rejects_them()
    {
        var h = Make(new()
        {
            ["services/courses/user"] = _ => Fixture("courses-user.json"),
            ["services/grades/terms2"] = c => c.Params.ContainsKey("fields")
                ? Http.Json(new { message = "Invalid argument: fields" }, HttpStatusCode.BadRequest)
                : Fixture("grades-terms2.json"),
        });
        Assert.NotEmpty(await h.Adapter.GetGradesAsync(["2025Z"]));
        Assert.Equal(2, h.To("services/grades/terms2").Count);
    }

    [Fact]
    public async Task GetGrades_does_not_retry_when_the_method_itself_is_missing()
    {
        var h = Make(new() { ["services/courses/user"] = _ => Fixture("courses-user.json") });
        var ex = await Assert.ThrowsAsync<UsosHttpException>(() => h.Adapter.GetGradesAsync(["2025Z"]));
        Assert.True(ex.IsMethodNotFound);
        Assert.Single(h.To("services/grades/terms2"));
    }

    [Fact]
    public async Task GetPayments_uses_the_warsaw_today_for_overdue_detection()
    {
        var h = Make(new() { ["services/payments/user_payments"] = _ => Fixture("payments.json") });
        var payments = await h.Adapter.GetPaymentsAsync();
        Assert.Equal(PaymentState.Overdue, payments.Single(p => p.Id == "7002").State);
        Assert.Equal(PaymentState.Unpaid, payments.Single(p => p.Id == "7004").State);
    }

    // ---------- USOSmail ----------

    [Fact]
    public async Task Usosmail_lists_reads_and_sends()
    {
        var h = Make(new()
        {
            ["services/mailclient/messages"] = _ => Fixture("mail-messages.json"),
            ["services/mailclient/message"] = _ => Http.JsonText(Fx.Json("mail-messages.json").GetProperty("messages")[0].GetRawText()),
            ["services/mailclient/send"] = _ => Http.Json(new { id = 777 }),
        });
        var list = await h.Adapter.ListMessagesAsync(5, 10);
        Assert.Equal(["5501", "5490"], list.Select(m => m.Id));
        var listCall = h.Calls[0];
        Assert.Equal(("5", "10", "inbox"), (listCall.Params["limit"], listCall.Params["offset"], listCall.Params["folder"]));

        var msg = await h.Adapter.GetMessageAsync("5501");
        Assert.Single(msg.Attachments);
        Assert.Equal("5501", h.Calls[1].Params["message_id"]);

        Assert.Equal("777", await h.Adapter.SendMessageAsync(["a@b.pl", "c@d.pl"], "Temat", "Treść"));
        var send = h.Calls[2];
        Assert.Equal("POST", send.Method);
        Assert.Equal(("a@b.pl|c@d.pl", "Temat", "Treść"), (send.Params["recipients"], send.Params["subject"], send.Params["body"]));
        Assert.True(OAuthVerifier.Verify(send.Raw, $"{Base}services/mailclient/send", "csecret", "asecret"));
    }

    [Fact]
    public async Task Usosmail_unknown_module_becomes_a_graceful_not_supported_error()
    {
        var missing = Make([]);
        var ex1 = await Assert.ThrowsAsync<AdapterNotSupportedException>(() => missing.Adapter.ListMessagesAsync());
        await Assert.ThrowsAsync<AdapterNotSupportedException>(() => missing.Adapter.GetMessageAsync("1"));
        await Assert.ThrowsAsync<AdapterNotSupportedException>(() => missing.Adapter.SendMessageAsync(["x"], "s", "b"));
        Assert.Equal("USOSmail API niedostępne", ex1.Message);

        var unauth = Make(new() { ["services/mailclient/messages"] = _ => Http.Json(new { message = "no" }, HttpStatusCode.Unauthorized) });
        await Assert.ThrowsAsync<AdapterAuthException>(() => unauth.Adapter.ListMessagesAsync());
    }

    // ---------- contacts / news ----------

    [Fact]
    public async Task Dean_office_contacts_come_from_the_users_faculty()
    {
        var h = Make(new()
        {
            ["services/users/user"] = _ => Http.JsonText("""{"student_programmes":[{"programme":{"faculty":{"id":"WI"}}},{"programme":{"faculty":{"id":"WI"}}}]}"""),
            ["services/fac/faculty"] = _ => Fixture("faculty.json"),
        });
        Assert.Single(await h.Adapter.GetDeanOfficeContactsAsync());
        var fac = Assert.Single(h.To("services/fac/faculty"));
        Assert.Equal("WI", fac.Params["fac_id"]);
    }

    [Fact]
    public async Task Dean_office_contacts_are_empty_when_unavailable()
    {
        Assert.Empty(await Make([]).Adapter.GetDeanOfficeContactsAsync());
        Assert.Empty(await Make(new() { ["services/users/user"] = _ => Http.JsonText("{}") }).Adapter.GetDeanOfficeContactsAsync());
    }

    [Fact]
    public async Task Announcements_are_mapped_limited_and_empty_when_news_module_is_missing()
    {
        var h = Make(new() { ["services/news/search"] = _ => Fixture("news.json") });
        Assert.Single(await h.Adapter.GetAnnouncementsAsync(1));
        Assert.Equal("1", h.Calls[0].Params["limit"]);
        Assert.Empty(await Make([]).Adapter.GetAnnouncementsAsync());
    }

    [Fact]
    public async Task Announcements_still_surface_auth_errors()
    {
        var h = Make(new() { ["services/news/search"] = _ => Http.Json(new { message = "revoked" }, HttpStatusCode.Unauthorized) });
        await Assert.ThrowsAsync<AdapterAuthException>(() => h.Adapter.GetAnnouncementsAsync());
    }

    // ---------- via the remote signer ----------

    [Fact]
    public async Task Works_through_the_remote_signer_without_local_signing()
    {
        var handler = new FakeHandler(r => r.Path == "usos/proxy/users/user" ? Fixture("user.json") : Http.JsonText("{}"));
        var signer = new RemoteUsosSigner(new HttpClient(handler), new Uri("https://signing.example/"), "k");
        var adapter = new UsosApiAdapter(signer, Creds, new FixedClock("2025-11-20T10:00:00Z"));
        Assert.Equal("Nowak", (await adapter.GetProfileAsync()).LastName);
        var r = Assert.Single(handler.Requests);
        Assert.Equal("atoken", r.Headers["X-Usos-Token"]);
        Assert.Null(r.Authorization);
    }
}
