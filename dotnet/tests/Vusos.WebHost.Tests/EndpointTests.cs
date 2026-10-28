using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Vusos.Core.Platform;
using Vusos.WebHost.Hosting;

namespace Vusos.WebHost.Tests;

public sealed class EndpointTests : IDisposable
{
    private readonly WebHostFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Usos_callback_without_pending_flow_redirects_with_error()
    {
        var client = await _factory.SignedInClientAsync(Emails.Anna);

        var response = await client.GetAsync("/connect/usos/callback?oauth_token=abc&oauth_verifier=xyz");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/ustawienia?error=", location);
        Assert.Contains(Uri.EscapeDataString("Brak oczekującej autoryzacji USOS"), location);
    }

    [Fact]
    public async Task Usos_callback_without_verifier_redirects_with_error()
    {
        var client = await _factory.SignedInClientAsync(Emails.Anna);

        var response = await client.GetAsync("/connect/usos/callback?oauth_token=abc");

        Assert.StartsWith("/ustawienia?error=", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Usos_callback_requires_a_session_and_returns_to_the_callback_after_login()
    {
        var response = await _factory.NewClient().GetAsync("/connect/usos/callback?oauth_token=abc&oauth_verifier=xyz");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/auth/login?returnUrl=" + Uri.EscapeDataString("/connect/usos/callback?oauth_token=abc&oauth_verifier=xyz"),
            response.Headers.Location!.OriginalString);
    }

    private (string UserId, string Hash, string FilePath) SeedFile(string email, string content)
    {
        var userId = "dev:" + email;
        var core = _factory.Cores.GetOrCreate(userId);
        var dir = Path.Combine(core.Paths.MaterialsDirectory, "c1", "m1");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "skrypt.txt");
        File.WriteAllText(file, content);
        return (userId, core.UserHash, file);
    }

    [Fact]
    public async Task Download_serves_the_owners_file()
    {
        var (_, hash, file) = SeedFile(Emails.Anna, "tajne notatki Anny");
        var token = _factory.Services.GetRequiredService<DownloadTokens>().Issue(hash, file);
        var client = await _factory.SignedInClientAsync(Emails.Anna);

        var response = await client.GetAsync("/dl/" + Uri.EscapeDataString(token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("tajne notatki Anny", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Download_rejects_a_token_issued_for_another_user()
    {
        var (_, annaHash, annaFile) = SeedFile(Emails.Anna, "tajne notatki Anny");
        var token = _factory.Services.GetRequiredService<DownloadTokens>().Issue(annaHash, annaFile);
        var jan = await _factory.SignedInClientAsync(Emails.Jan);

        var response = await jan.GetAsync("/dl/" + Uri.EscapeDataString(token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_rejects_a_foreign_path_even_with_own_hash()
    {
        var (_, _, annaFile) = SeedFile(Emails.Anna, "tajne notatki Anny");
        var janHash = _factory.Cores.GetOrCreate("dev:" + Emails.Jan).UserHash;
        var token = _factory.Services.GetRequiredService<DownloadTokens>().Issue(janHash, annaFile);
        var jan = await _factory.SignedInClientAsync(Emails.Jan);

        var response = await jan.GetAsync("/dl/" + Uri.EscapeDataString(token));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Download_rejects_garbage_tokens_and_anonymous_requests()
    {
        var (_, hash, file) = SeedFile(Emails.Anna, "x");
        var token = _factory.Services.GetRequiredService<DownloadTokens>().Issue(hash, file);
        var signedIn = await _factory.SignedInClientAsync(Emails.Anna);

        Assert.Equal(HttpStatusCode.NotFound, (await signedIn.GetAsync("/dl/not-a-token")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.NewClient().GetAsync("/dl/" + Uri.EscapeDataString(token))).StatusCode);
    }

    [Fact]
    public void Download_tokens_embed_the_user_hash()
    {
        var tokens = _factory.Services.GetRequiredService<DownloadTokens>();
        var token = tokens.Issue("abcdef0123456789", "/some/path");

        Assert.True(tokens.TryRead(token, out var hash, out var path));
        Assert.Equal("abcdef0123456789", hash);
        Assert.Equal("/some/path", path);
        Assert.False(tokens.TryRead(token + "x", out _, out _));
    }
}

public sealed class ConnectFlowTests : IDisposable
{
    private readonly WebHostFactory _mock = new();
    private readonly WebHostFactory _live = new(adapters: "live");

    public void Dispose()
    {
        _mock.Dispose();
        _live.Dispose();
    }

    [Fact]
    public async Task Mock_mode_connects_both_systems_without_leaving_the_page()
    {
        using var scope = _mock.CircuitScope(Emails.Anna);
        var flows = scope.ServiceProvider.GetRequiredService<Vusos.UI.Services.IConnectFlows>();

        await flows.StartUsosAsync();
        await flows.StartPlatonAsync();

        var status = await scope.ServiceProvider.GetRequiredService<Vusos.Core.Services.IConnectionManager>().GetStatusAsync();
        Assert.All(status.Values, s => Assert.Equal(Vusos.Core.Domain.ConnectionStatus.Connected, s.Status));
    }

    [Fact]
    public async Task Live_mode_platon_start_asks_to_paste_the_token()
    {
        using var scope = _live.CircuitScope(Emails.Anna);
        var flows = scope.ServiceProvider.GetRequiredService<Vusos.UI.Services.IConnectFlows>();

        var ex = await Assert.ThrowsAsync<NotSupportedException>(flows.StartPlatonAsync);

        Assert.StartsWith("W przeglądarce wklej token z launch.php", ex.Message);
        Assert.Contains("launch.php?service=moodle_mobile_app", ex.Message);
        Assert.Contains("urlscheme=moodlemobile", ex.Message);
    }

    [Fact]
    public async Task Live_mode_usos_start_without_signing_configuration_explains_what_is_missing()
    {
        using var scope = _live.CircuitScope(Emails.Anna);
        var flows = scope.ServiceProvider.GetRequiredService<Vusos.UI.Services.IConnectFlows>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(flows.StartUsosAsync);

        Assert.Contains("SigningServerUrl", ex.Message);
    }

    [Fact]
    public async Task Pasted_platon_token_goes_to_the_users_own_connection_manager()
    {
        using var scope = _live.CircuitScope(Emails.Anna);
        var flows = scope.ServiceProvider.GetRequiredService<Vusos.UI.Services.IConnectFlows>();

        // Token jest poprawnie sparsowany; walidacja po stronie Platona kończy się błędem sieci, ale poświadczenia trafiają do Anny.
        var token = "moodlemobile://token=" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("site:::abc123:::priv"));
        await Assert.ThrowsAnyAsync<Exception>(() => flows.SubmitPlatonTokenAsync(token));

        var anna = _live.Cores.GetOrCreate("dev:" + Emails.Anna);
        var jan = _live.Cores.GetOrCreate("dev:" + Emails.Jan);
        Assert.True(File.Exists(Path.Combine(anna.Paths.DataDirectory, "secure.json")));
        Assert.False(File.Exists(Path.Combine(jan.Paths.DataDirectory, "secure.json")));
    }
}

public sealed class FakeModeTests : IDisposable
{
    private readonly WebHostFactory _factory = new(adapters: "fake");

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Fake_mode_still_serves_the_shell_behind_cookie_login()
    {
        var client = await _factory.SignedInClientAsync(Emails.Anna);
        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("blazor.web.js", await response.Content.ReadAsStringAsync());
        Assert.Null(_factory.Services.GetService<UserCoreFactory>());
    }
}
