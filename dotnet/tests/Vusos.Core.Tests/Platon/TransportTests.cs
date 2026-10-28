using System.Text.Json;
using Vusos.Core.Adapters;
using Vusos.Core.Adapters.Platon;

namespace Vusos.Core.Tests.Platon;

public class MoodleArgsEncoderTests
{
    private static Dictionary<string, object?> Args(params (string, object?)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public void Flatten_encodes_arrays_nested_objects_booleans_and_skips_null()
    {
        var flat = MoodleArgsEncoder.Flatten(new Dictionary<string, object?>
        {
            ["classification"] = "inprogress",
            ["courseids"] = new[] { 12, 13 },
            ["events"] = new Dictionary<string, object?> { ["timesortfrom"] = 100, ["nested"] = new Dictionary<string, object?> { ["a"] = true } },
            ["flag"] = false,
            ["nothing"] = null,
            ["list"] = new List<object?> { new Dictionary<string, object?> { ["id"] = 1, ["v"] = "x" } }
        });
        Assert.Equal(
        [
            ("classification", "inprogress"),
            ("courseids[0]", "12"),
            ("courseids[1]", "13"),
            ("events[timesortfrom]", "100"),
            ("events[nested][a]", "1"),
            ("flag", "0"),
            ("list[0][id]", "1"),
            ("list[0][v]", "x")
        ], flat.Select(kv => (kv.Key, kv.Value)));
    }

    [Fact]
    public void Flatten_formats_numbers_with_the_invariant_culture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("pl-PL");
            var flat = MoodleArgsEncoder.Flatten(Args(("x", 1.5), ("y", 42L)));
            Assert.Equal([("x", "1.5"), ("y", "42")], flat.Select(kv => (kv.Key, kv.Value)));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Flatten_handles_empty_input_and_empty_collections()
    {
        Assert.Empty(MoodleArgsEncoder.Flatten(new Dictionary<string, object?>()));
        Assert.Empty(MoodleArgsEncoder.Flatten(Args(("ids", new List<int>()))));
    }
}

public class WsTransportTests
{
    private const string Base = "https://platon.vistula.edu.pl/";
    private static readonly IReadOnlyDictionary<string, object?> NoArgs = new Dictionary<string, object?>();

    private static (WsTransport T, FakeHandler H) Make(Func<HttpResponseMessage> respond, string baseUrl = Base, TimeSpan? timeout = null)
    {
        var h = new FakeHandler(respond);
        return (new WsTransport(new HttpClient(h), baseUrl, "tok123", timeout), h);
    }

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));

    [Fact]
    public async Task Posts_form_encoded_args_to_server_php()
    {
        var (t, h) = Make(() => FakeHandler.Json("""{"ok":true}"""), "https://platon.vistula.edu.pl");
        var res = await t.CallAsync("core_calendar_get_action_events_by_timesort", new Dictionary<string, object?>
        {
            ["timesortfrom"] = 5,
            ["courseids"] = new[] { 12, 13 },
            ["events"] = new Dictionary<string, object?> { ["x"] = "a b&c" }
        });
        Assert.True(res.GetProperty("ok").GetBoolean());

        var c = Assert.Single(h.Requests);
        Assert.Equal($"{Base}webservice/rest/server.php", c.Request.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, c.Request.Method);
        Assert.Equal("application/x-www-form-urlencoded", c.Request.Content?.Headers.ContentType?.MediaType);
        var form = ParseForm(c.Body);
        Assert.Equal("tok123", form["wstoken"]);
        Assert.Equal("core_calendar_get_action_events_by_timesort", form["wsfunction"]);
        Assert.Equal("json", form["moodlewsrestformat"]);
        Assert.Equal("5", form["timesortfrom"]);
        Assert.Equal("12", form["courseids[0]"]);
        Assert.Equal("13", form["courseids[1]"]);
        Assert.Equal("a b&c", form["events[x]"]);
    }

    [Theory]
    [InlineData("invalidtoken", true)]
    [InlineData("accessexception", true)]
    [InlineData("requireloginerror", true)]
    [InlineData("servicenotavailable", false)]
    [InlineData("invalidparameter", false)]
    public async Task Maps_errorcodes(string errorCode, bool isAuth)
    {
        var (t, _) = Make(() => FakeHandler.Json($$"""{"exception":"moodle_exception","errorcode":"{{errorCode}}","message":"boom"}"""));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => t.CallAsync("x", NoArgs));
        Assert.Equal(isAuth, ex is AdapterAuthException);
        Assert.Equal(!isAuth, ex is AdapterUnavailableException);
        Assert.Contains("boom", ex.Message);
    }

    [Theory]
    [InlineData("invalidparameter", false)]
    [InlineData("invalidrecord", false)]
    [InlineData("servicenotavailable", true)]
    [InlineData("somethingelse", true)]
    public async Task Marks_invalid_record_and_parameter_errors_as_permanent(string errorCode, bool transient)
    {
        var (t, _) = Make(() => FakeHandler.Json($$"""{"exception":"x","errorcode":"{{errorCode}}","message":"m"}"""));
        var ex = await Assert.ThrowsAsync<AdapterUnavailableException>(() => t.CallAsync("x", NoArgs));
        Assert.Equal(transient, ex.IsTransient);
    }

    [Fact]
    public async Task Maps_http_status_codes()
    {
        var (t502, _) = Make(() => FakeHandler.Text("x", 502));
        var ex = await Assert.ThrowsAsync<AdapterUnavailableException>(() => t502.CallAsync("x", NoArgs));
        Assert.True(ex.IsTransient);

        var (t503, _) = Make(() => FakeHandler.Text("x", 503));
        Assert.True((await Assert.ThrowsAsync<AdapterUnavailableException>(() => t503.CallAsync("x", NoArgs))).IsTransient);

        var (t404, _) = Make(() => FakeHandler.Text("x", 404));
        Assert.False((await Assert.ThrowsAsync<AdapterUnavailableException>(() => t404.CallAsync("x", NoArgs))).IsTransient);

        var (t403, _) = Make(() => FakeHandler.Text("x", 403));
        await Assert.ThrowsAsync<AdapterAuthException>(() => t403.CallAsync("x", NoArgs));
    }

    [Fact]
    public async Task Non_json_body_and_network_failures_are_unavailable()
    {
        var (html, _) = Make(() => FakeHandler.Text("<html>"));
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => html.CallAsync("x", NoArgs));

        var net = new WsTransport(new HttpClient(new FakeHandler(_ => throw new HttpRequestException("fetch failed"))), Base, "t");
        var ex = await Assert.ThrowsAsync<AdapterUnavailableException>(() => net.CallAsync("x", NoArgs));
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task Timeout_is_a_transient_unavailable_but_caller_cancellation_is_not_swallowed()
    {
        var honouring = new CancellableHandler();
        var t = new WsTransport(new HttpClient(honouring), Base, "t", TimeSpan.FromMilliseconds(50));
        var ex = await Assert.ThrowsAsync<AdapterUnavailableException>(() => t.CallAsync("x", NoArgs));
        Assert.True(ex.IsTransient);

        using var cts = new CancellationTokenSource();
        var pending = t.CallAsync("x", NoArgs, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private sealed class CancellableHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        }
    }

    [Fact]
    public async Task Does_not_mistake_array_results_for_errors()
    {
        var (t, _) = Make(() => FakeHandler.Json("""[{"id":1}]"""));
        var res = await t.CallAsync("x", NoArgs);
        Assert.Equal(JsonValueKind.Array, res.ValueKind);
        Assert.Equal(1, res[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public void Exposes_a_normalised_base_url()
    {
        var (t, _) = Make(() => FakeHandler.Json("{}"), "  https://platon.vistula.edu.pl  ");
        Assert.Equal(Base, t.BaseUrl.ToString());
    }

    public class RewriteFileUrl
    {
        private readonly WsTransport _t = new(new HttpClient(new FakeHandler(() => FakeHandler.Json("{}"))), Base, "tok");

        [Fact]
        public void Turns_pluginfile_into_webservice_pluginfile_with_token() =>
            Assert.Equal($"{Base}webservice/pluginfile.php/1/mod_resource/content/1/a.pdf?token=tok",
                _t.RewriteFileUrl($"{Base}pluginfile.php/1/mod_resource/content/1/a.pdf"));

        [Fact]
        public void Keeps_existing_query_and_does_not_duplicate_the_token()
        {
            var once = _t.RewriteFileUrl($"{Base}webservice/pluginfile.php/1/a.pdf?forcedownload=1");
            Assert.Equal($"{Base}webservice/pluginfile.php/1/a.pdf?forcedownload=1&token=tok", once);
            Assert.Equal(once, _t.RewriteFileUrl(once));
        }

        [Fact]
        public void Leaves_non_file_and_foreign_urls_untouched()
        {
            Assert.Equal($"{Base}mod/resource/view.php?id=1", _t.RewriteFileUrl($"{Base}mod/resource/view.php?id=1"));
            Assert.Equal("https://evil.example/pluginfile.php/1/a.pdf", _t.RewriteFileUrl("https://evil.example/pluginfile.php/1/a.pdf"));
            Assert.Equal("not a url", _t.RewriteFileUrl("not a url"));
        }

        [Fact]
        public void Preserves_fragments_and_sends_no_extra_headers()
        {
            Assert.Equal($"{Base}webservice/pluginfile.php/1/a.pdf?token=tok#p2", _t.RewriteFileUrl($"{Base}pluginfile.php/1/a.pdf#p2"));
            Assert.Empty(_t.FileHeaders());
        }
    }
}

public class AjaxTransportTests
{
    private const string Base = "https://platon.vistula.edu.pl/";
    private static readonly IReadOnlyDictionary<string, object?> NoArgs = new Dictionary<string, object?>();

    private static (AjaxTransport T, FakeHandler H) Make(Func<HttpResponseMessage> respond)
    {
        var h = new FakeHandler(respond);
        return (new AjaxTransport(new HttpClient(h), Base, "MoodleSession=abc", "sk 1"), h);
    }

    [Fact]
    public async Task Posts_a_json_array_with_cookie_and_returns_data()
    {
        var (t, h) = Make(() => FakeHandler.Json("""[{"error":false,"data":{"courses":[]}}]"""));
        var fn = "core_course_get_enrolled_courses_by_timeline_classification";
        var res = await t.CallAsync(fn, new Dictionary<string, object?> { ["classification"] = "all" });
        Assert.Equal(JsonValueKind.Array, res.GetProperty("courses").ValueKind);

        var c = Assert.Single(h.Requests);
        Assert.Equal($"{Base}lib/ajax/service.php?sesskey=sk%201&info={fn}", c.Request.RequestUri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Post, c.Request.Method);
        Assert.Equal("MoodleSession=abc", Assert.Single(c.Request.Headers.GetValues("Cookie")));
        using var body = JsonDocument.Parse(c.Body);
        var item = Assert.Single(body.RootElement.EnumerateArray());
        Assert.Equal(0, item.GetProperty("index").GetInt32());
        Assert.Equal(fn, item.GetProperty("methodname").GetString());
        Assert.Equal("all", item.GetProperty("args").GetProperty("classification").GetString());
    }

    [Fact]
    public async Task Serialises_nested_args_natively_as_json()
    {
        var (t, h) = Make(() => FakeHandler.Json("""[{"error":false,"data":null}]"""));
        var res = await t.CallAsync("f", new Dictionary<string, object?>
        {
            ["courseids"] = new List<long> { 1, 2 },
            ["finish"] = true,
            ["data"] = new List<object?> { new Dictionary<string, object?> { ["name"] = "n", ["value"] = "v" } }
        });
        Assert.Equal(JsonValueKind.Null, res.ValueKind);
        using var body = JsonDocument.Parse(h.Requests[0].Body);
        var args = body.RootElement[0].GetProperty("args");
        Assert.Equal(2, args.GetProperty("courseids").GetArrayLength());
        Assert.True(args.GetProperty("finish").GetBoolean());
        Assert.Equal("v", args.GetProperty("data")[0].GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("servicerequireslogin", true)]
    [InlineData("invalidsesskey", true)]
    [InlineData("invalidparameter", false)]
    public async Task Maps_exception_errorcodes(string errorCode, bool isAuth)
    {
        var (t, _) = Make(() => FakeHandler.Json($$$"""[{"error":true,"exception":{"message":"nope","errorcode":"{{{errorCode}}}","link":""}}]"""));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => t.CallAsync("x", NoArgs));
        Assert.Equal(isAuth, ex is AdapterAuthException);
        Assert.Equal(!isAuth, ex is AdapterUnavailableException);
    }

    [Fact]
    public async Task Treats_redirects_and_non_json_bodies_as_an_expired_session()
    {
        var (redirect, _) = Make(() =>
        {
            var r = new HttpResponseMessage(System.Net.HttpStatusCode.Found);
            r.Headers.Location = new Uri($"{Base}login/index.php");
            return r;
        });
        await Assert.ThrowsAsync<AdapterAuthException>(() => redirect.CallAsync("x", NoArgs));

        var (html, _) = Make(() => FakeHandler.Text("<html>login</html>"));
        await Assert.ThrowsAsync<AdapterAuthException>(() => html.CallAsync("x", NoArgs));
    }

    [Fact]
    public async Task Detects_an_auto_followed_redirect_to_the_login_page()
    {
        var handler = new FakeHandler(req =>
        {
            var response = FakeHandler.Json("""[{"error":false,"data":1}]""");
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, $"{Base}login/index.php");
            return Task.FromResult(response);
        });
        var t = new AjaxTransport(new HttpClient(handler), Base, "c", "s");
        await Assert.ThrowsAsync<AdapterAuthException>(() => t.CallAsync("x", NoArgs));
    }

    [Fact]
    public async Task Handles_a_bare_error_object_and_server_errors()
    {
        var (bare, _) = Make(() => FakeHandler.Json("""{"error":"Session key is invalid"}"""));
        await Assert.ThrowsAsync<AdapterAuthException>(() => bare.CallAsync("x", NoArgs));

        var (s500, _) = Make(() => FakeHandler.Text("oops", 500));
        var ex = await Assert.ThrowsAsync<AdapterUnavailableException>(() => s500.CallAsync("x", NoArgs));
        Assert.True(ex.IsTransient);

        var (empty, _) = Make(() => FakeHandler.Json("[]"));
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => empty.CallAsync("x", NoArgs));

        var (odd, _) = Make(() => FakeHandler.Json("""{"something":1}"""));
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => odd.CallAsync("x", NoArgs));
    }

    [Fact]
    public void Uses_cookie_for_files_and_unrewrites_ws_urls()
    {
        var (t, _) = Make(() => FakeHandler.Json("[]"));
        Assert.Equal(new Dictionary<string, string> { ["Cookie"] = "MoodleSession=abc" }, t.FileHeaders());
        Assert.Equal($"{Base}pluginfile.php/1/a.pdf", t.RewriteFileUrl($"{Base}webservice/pluginfile.php/1/a.pdf"));
        Assert.Equal($"{Base}pluginfile.php/1/a.pdf", t.RewriteFileUrl($"{Base}pluginfile.php/1/a.pdf"));
        Assert.Equal("https://evil.example/webservice/pluginfile.php/1/a.pdf", t.RewriteFileUrl("https://evil.example/webservice/pluginfile.php/1/a.pdf"));
    }
}
