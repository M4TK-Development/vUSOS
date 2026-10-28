using System.Net;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vusos.WebHost.Hosting;

namespace Vusos.WebHost.Tests;

public sealed class WebHostFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string> _settings;

    public WebHostFactory(string adapters = "mock", string auth = "dev", Dictionary<string, string>? extra = null)
    {
        StorageRoot = Path.Combine(Path.GetTempPath(), "vusos-webhost-tests-" + Guid.NewGuid().ToString("N"));
        _settings = new Dictionary<string, string>
        {
            ["Adapters:Mode"] = adapters,
            ["Auth:Mode"] = auth,
            ["Storage:Root"] = StorageRoot,
            ["Server:RedirectToHttps"] = "false",
            ["Server:SweepInterval"] = "00:00:00",
            ["Server:PublicUrl"] = "https://vusos.test",
        };
        if (extra is not null) foreach (var (k, v) in extra) _settings[k] = v;
    }

    public string StorageRoot { get; }

    public UserCoreFactory Cores => Services.GetRequiredService<UserCoreFactory>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (k, v) in _settings) builder.UseSetting(k, v);
    }

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>Loguje użytkownika dev (cookie zostaje w kliencie).</summary>
    public async Task<HttpClient> SignedInClientAsync(string email)
    {
        var client = NewClient();
        var response = await client.PostAsync("/auth/dev-login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email,
            ["returnUrl"] = "/",
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    /// <summary>Zakres jak obwód Blazora: stan uwierzytelnienia ustawiony tak, jak robi to fabryka obwodów.</summary>
    public IServiceScope CircuitScope(string email)
    {
        var scope = Services.CreateScope();
        var provider = (IHostEnvironmentAuthenticationStateProvider)scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(AuthSetup.CreateDevPrincipal(email))));
        return scope;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try { if (Directory.Exists(StorageRoot)) Directory.Delete(StorageRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public static class Emails
{
    public const string Anna = "anna.kowalska@stu.vistula.edu.pl";
    public const string Jan = "jan.nowak@vistula.edu.pl";
}
