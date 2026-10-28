using System.Collections.Concurrent;
using System.Text.Json;
using Vusos.Core.Adapters.Platon;
using Vusos.Core.Platform;

namespace Vusos.Core.Tests.Platon;

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset Now { get; set; } = now;
}

internal sealed record RecordedCall(string Fn, IReadOnlyDictionary<string, object?> Args);

internal static class Fx
{
    public static JsonElement Fixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Platon", name + ".json");
        return Parse(File.ReadAllText(path));
    }

    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static long Num(object? value) => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Fałszywy transport: odpowiada z tabeli surowych JSON-ów Moodle (lub handlerów) i zapisuje wywołania.</summary>
internal sealed class FakeTransport : IMoodleTransport
{
    public delegate JsonElement Handler(IReadOnlyDictionary<string, object?> args);

    private readonly ConcurrentQueue<RecordedCall> _calls = new();
    private readonly Dictionary<string, Handler> _responses = [];
    private readonly Func<string, string> _rewrite;
    private readonly IReadOnlyDictionary<string, string> _headers;

    public FakeTransport(Func<string, string>? rewrite = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        _rewrite = rewrite ?? (u => u);
        _headers = headers ?? new Dictionary<string, string>();
    }

    public Uri BaseUrl { get; } = new("https://platon.vistula.edu.pl/");
    public IReadOnlyList<RecordedCall> Calls => _calls.ToList();

    public FakeTransport On(string fn, JsonElement response) { _responses[fn] = _ => response; return this; }
    public FakeTransport On(string fn, Handler handler) { _responses[fn] = handler; return this; }
    public FakeTransport On(string fn, string json) => On(fn, Fx.Parse(json));
    public FakeTransport OnFixture(string fn, string fixture) => On(fn, Fx.Fixture(fixture));

    public IReadOnlyList<RecordedCall> CallsTo(string fn) => Calls.Where(c => c.Fn == fn).ToList();

    public Task<JsonElement> CallAsync(string wsfunction, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        _calls.Enqueue(new RecordedCall(wsfunction, args));
        if (!_responses.TryGetValue(wsfunction, out var handler))
            return Task.FromException<JsonElement>(new InvalidOperationException($"unexpected call {wsfunction}"));
        try
        {
            return Task.FromResult(handler(args));
        }
        catch (Exception ex)
        {
            return Task.FromException<JsonElement>(ex);
        }
    }

    public IReadOnlyDictionary<string, string> FileHeaders() => _headers;
    public string RewriteFileUrl(string url) => _rewrite(url);
}

/// <summary>Fałszywy HttpMessageHandler zapisujący żądania (z treścią).</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public sealed record Captured(HttpRequestMessage Request, string Body);

    public List<Captured> Requests { get; } = [];

    public FakeHandler(Func<HttpResponseMessage> respond) : this(_ => Task.FromResult(respond())) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new Captured(request, body));
        return await respond(request);
    }

    public static HttpResponseMessage Json(string json, int status = 200) =>
        new((System.Net.HttpStatusCode)status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Text(string text, int status = 200) =>
        new((System.Net.HttpStatusCode)status) { Content = new StringContent(text, System.Text.Encoding.UTF8, "text/html") };
}
