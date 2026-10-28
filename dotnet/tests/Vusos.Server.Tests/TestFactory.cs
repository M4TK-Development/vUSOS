using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Vusos.Server.Tests;

public sealed record SeenRequest(HttpMethod Method, Uri Uri, string? Authorization, string Body, string? ContentType, string? Accept);

public sealed class FakeUpstream : HttpMessageHandler
{
    public List<SeenRequest> Seen { get; } = [];
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? RespondAsync { get; set; }

    public static HttpResponseMessage Text(HttpStatusCode status, string body, string mediaType = "text/plain") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Seen.Add(new SeenRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(),
            body, request.Content?.Headers.ContentType?.ToString(), request.Headers.Accept.ToString()));
        return RespondAsync is null ? Respond(request) : await RespondAsync(request, ct);
    }
}

public sealed class ServerFactory : WebApplicationFactory<Program>
{
    public FakeUpstream Upstream { get; } = new();
    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["Usos:ApiBaseUrl"] = "https://usos.example.edu.pl/",
        ["Usos:ConsumerKey"] = "ckey",
        ["Usos:ConsumerSecret"] = "csecret",
    };

    public ServerFactory With(string key, string? value) { Settings[key] = value; return this; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(Settings));
        builder.ConfigureServices(s =>
            s.AddHttpClient("usos").ConfigurePrimaryHttpMessageHandler(() => Upstream));
    }

    public HttpClient NoRedirectClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
}
