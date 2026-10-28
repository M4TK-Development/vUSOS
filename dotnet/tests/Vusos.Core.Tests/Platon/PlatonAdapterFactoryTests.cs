using Vusos.Core.Adapters;
using Vusos.Core.Adapters.Platon;

namespace Vusos.Core.Tests.Platon;

public class PlatonAdapterFactoryTests
{
    private const string Base = "https://platon.vistula.edu.pl/";
    private static readonly FixedClock Clock = new(DateTimeOffset.Parse("2026-10-28T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private static HttpClient Http() => new(new FakeHandler(() => FakeHandler.Json("{}")));

    [Fact]
    public async Task Ws_adapter_downloads_with_a_token_in_the_url_and_no_headers()
    {
        var ws = PlatonAdapterFactory.Create(new PlatonWsCredentials("tok", null, Base), Http(), Clock);
        var d = await ws.GetFileDownloadAsync($"{Base}pluginfile.php/1/a.pdf");
        Assert.Equal($"{Base}webservice/pluginfile.php/1/a.pdf?token=tok", d.Url.ToString());
        Assert.Empty(d.Headers);
    }

    [Fact]
    public async Task Web_adapter_downloads_with_a_cookie_header()
    {
        var web = PlatonAdapterFactory.Create(new PlatonWebCredentials("MoodleSession=z", "s", Base), Http(), Clock);
        var d = await web.GetFileDownloadAsync($"{Base}pluginfile.php/1/a.pdf");
        Assert.Equal($"{Base}pluginfile.php/1/a.pdf", d.Url.ToString());
        Assert.Equal("MoodleSession=z", d.Headers["Cookie"]);
    }

    [Fact]
    public async Task Mock_adapter_is_selected_for_mock_credentials()
    {
        var mock = PlatonAdapterFactory.Create(new PlatonMockCredentials(), Http(), Clock);
        Assert.IsType<PlatonMockAdapter>(mock);
        Assert.True((await mock.GetCoursesAsync()).Count >= 6);
    }

    [Fact]
    public async Task Falls_back_to_the_configured_base_url_when_credentials_have_none()
    {
        var ws = PlatonAdapterFactory.Create(new PlatonWsCredentials("tok", null, ""), Http(), Clock, "https://other.example/moodle");
        var d = await ws.GetFileDownloadAsync("https://other.example/moodle/pluginfile.php/1/a.pdf");
        Assert.Equal("https://other.example/moodle/webservice/pluginfile.php/1/a.pdf?token=tok", d.Url.ToString());
    }

    [Fact]
    public async Task Ws_adapter_uses_the_stored_user_id_for_grades_without_site_info()
    {
        var handler = new FakeHandler(() => FakeHandler.Json("""{"usergrades":[]}"""));
        var ws = PlatonAdapterFactory.Create(new PlatonWsCredentials("tok", null, Base, UserId: 77), new HttpClient(handler), Clock);
        await ws.GetGradesAsync("3017");
        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("userid=77", body);
        Assert.Contains("wsfunction=gradereport_user_get_grade_items", body);
    }

    [Fact]
    public void Parses_and_exposes_the_token_helper()
    {
        Assert.Equal(32, MobileLaunchToken.Parse("0123456789abcdef0123456789abcdef").WsToken.Length);
    }
}
