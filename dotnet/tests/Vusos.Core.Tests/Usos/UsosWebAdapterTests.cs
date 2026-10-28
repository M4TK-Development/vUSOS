using System.Net;
using Vusos.Core.Adapters;
using Vusos.Core.Adapters.Usos;
using Vusos.Core.Domain;

namespace Vusos.Core.Tests.Usos;

public class UsosWebAdapterTests
{
    private const string Base = "https://usosweb.vistula.example/";

    private static (UsosWebAdapter Adapter, FakeHandler Handler) Make(Func<Recorded, HttpResponseMessage> responder, string now = "2025-11-20T10:00:00Z")
    {
        var (http, handler) = Http.Client(responder);
        return (new UsosWebAdapter(http, new UsosWebCredentials("PHPSESSID=abc; usos=1", Base), new FixedClock(now)), handler);
    }

    private static string Action(Recorded r) => r.Params.GetValueOrDefault("_action", "");

    [Fact]
    public async Task Sends_cookies_requests_the_right_action_and_parses_the_answer()
    {
        var (a, h) = Make(r => Http.Html(Fx.Text(Action(r) == "dla_stud/studia/oceny/index" ? "grades.html" : "payments.html")));
        Assert.Equal(2, (await a.GetGradesAsync(["2025Z"])).Count);
        Assert.Equal(5, (await a.GetPaymentsAsync()).Count);

        Assert.Contains("kontroler.php?_action=dla_stud%2Fstudia%2Foceny%2Findex", h.Requests[0].Url.ToString());
        Assert.Equal("PHPSESSID=abc; usos=1", h.Requests[0].Headers["Cookie"]);
        Assert.Contains("text/html", h.Requests[0].Headers["Accept"]);

        var terms = await a.GetTermsAsync();
        Assert.Equal(["2025Z", "2024L"], terms.Select(t => t.Id));
        Assert.True(terms[0].IsCurrent);
    }

    [Fact]
    public async Task Queries_each_week_of_the_requested_range_and_filters_to_it()
    {
        var starts = new List<string>();
        var (a, _) = Make(r => { lock (starts) starts.Add(r.Params["plan_start"]); return Http.Html(Fx.Text("plan-week.html")); });

        var out1 = await a.GetTimetableAsync(new(2025, 10, 7), new(2025, 10, 8));
        Assert.Equal(["2025-10-06"], starts);
        Assert.Equal(["INF-BD", "INF-SO"], out1.Select(e => e.CourseId));

        var two = await a.GetTimetableAsync(new(2025, 10, 6), new(2025, 10, 13));
        Assert.Equal(["2025-10-06", "2025-10-06", "2025-10-13"], starts);
        Assert.Equal(5, two.Count);
        Assert.Empty(await a.GetTimetableAsync(new(2025, 10, 13), new(2025, 10, 6)));
    }

    [Fact]
    public async Task Derives_courses_from_the_timetable_and_reads_messages_news_and_profile()
    {
        var (a, _) = Make(r => Action(r) switch
        {
            "home/plan" => Http.Html(Fx.Text("plan-week.html")),
            "dla_stud/studia/komunikacja" => Http.Html(Fx.Text(r.Params.ContainsKey("msg_id") ? "message.html" : "messages.html")),
            _ => Http.Html(Fx.Text("home.html")),
        }, "2025-10-08T10:00:00Z");

        var courses = await a.GetCoursesAsync();
        Assert.True(courses.Count >= 5);
        Assert.All(courses, c => Assert.Equal("2025Z", c.TermId));
        Assert.Empty(await a.GetCoursesAsync("2024L"));
        Assert.Single(await a.ListMessagesAsync(1));
        Assert.Equal(["5490"], (await a.ListMessagesAsync(5, 1)).Select(m => m.Id));
        Assert.Equal("Potwierdzenie złożenia wniosku", (await a.GetMessageAsync("5501")).Thread.Subject);
        Assert.Equal(2, (await a.GetAnnouncementsAsync(2)).Count);
        Assert.Equal("Nowak", (await a.GetProfileAsync()).LastName);
        Assert.Empty(await a.GetDeanOfficeContactsAsync());
        await Assert.ThrowsAsync<AdapterNotSupportedException>(() => a.SendMessageAsync(["x"], "s", "b"));
    }

    [Theory]
    [InlineData("/kontroler.php?_action=logowanie/index")]
    [InlineData("https://login.microsoftonline.com/x")]
    [InlineData("https://elsewhere.example/")]
    public async Task Redirects_to_login_sso_or_another_origin_mean_the_session_expired(string location)
    {
        var (a, _) = Make(_ => Http.Redirect(location));
        await Assert.ThrowsAsync<AdapterAuthException>(() => a.GetPaymentsAsync());
    }

    [Fact]
    public async Task A_login_page_with_status_200_and_401_403_responses_are_auth_errors()
    {
        await Assert.ThrowsAsync<AdapterAuthException>(() => Make(_ => Http.Html(Fx.Text("login.html"))).Adapter.GetGradesAsync());
        await Assert.ThrowsAsync<AdapterAuthException>(() => Make(_ => Http.Html("", HttpStatusCode.Forbidden)).Adapter.GetGradesAsync());
        await Assert.ThrowsAsync<AdapterAuthException>(() => Make(_ => Http.Html("", HttpStatusCode.Unauthorized)).Adapter.GetGradesAsync());
        await Assert.ThrowsAsync<AdapterAuthException>(() => Make(_ => Http.Redirect("")).Adapter.GetGradesAsync());
    }

    [Fact]
    public async Task A_handler_that_follows_redirects_itself_is_still_caught_via_the_final_url()
    {
        var http = new HttpClient(new RedirectedHandler());
        var a = new UsosWebAdapter(http, new UsosWebCredentials("a=b", Base), new FixedClock("2025-11-20T10:00:00Z"));
        await Assert.ThrowsAsync<AdapterAuthException>(() => a.GetPaymentsAsync());
    }

    private sealed class RedirectedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>Wyloguj</html>"),
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://login.microsoftonline.com/common/oauth2"),
            });
    }

    [Fact]
    public async Task Follows_same_origin_redirects()
    {
        var n = 0;
        var (a, h) = Make(_ => Interlocked.Increment(ref n) == 1
            ? Http.Redirect("kontroler.php?_action=dla_stud/platnosci/index&x=1")
            : Http.Html(Fx.Text("payments.html")));
        Assert.Equal(5, (await a.GetPaymentsAsync()).Count);
        Assert.Equal("1", h.Requests[1].Params["x"]);
    }

    [Fact]
    public async Task Server_errors_loops_and_network_failures_are_unavailable()
    {
        var ex = await Assert.ThrowsAsync<AdapterUnavailableException>(() => Make(_ => Http.Html("boom", HttpStatusCode.InternalServerError)).Adapter.GetPaymentsAsync());
        Assert.True(ex.IsTransient);

        var loop = await Assert.ThrowsAsync<AdapterUnavailableException>(() => Make(_ => Http.Redirect("/kontroler.php?_action=x")).Adapter.GetPaymentsAsync());
        Assert.Contains("przekierowa", loop.Message);

        var http = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("ECONNRESET")));
        var down = new UsosWebAdapter(http, new UsosWebCredentials("a=b", Base), new FixedClock("2025-11-20T10:00:00Z"));
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => down.GetPaymentsAsync());
    }

    [Fact]
    public async Task Unrecognised_markup_yields_empty_lists_not_errors()
    {
        var (a, _) = Make(_ => Http.Html("<html><body>Wyloguj <p>coś innego</p></body></html>"));
        Assert.Empty(await a.GetGradesAsync());
        Assert.Empty(await a.GetPaymentsAsync());
        Assert.Empty(await a.ListMessagesAsync());
        Assert.Empty(await a.GetAnnouncementsAsync());
        Assert.Empty(await a.GetTimetableAsync(new(2025, 10, 6), new(2025, 10, 12)));
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => a.GetProfileAsync());
    }

    [Fact]
    public void CreateHttpClient_does_not_follow_redirects()
    {
        using var client = UsosWebAdapter.CreateHttpClient();
        Assert.NotNull(client);
    }
}
