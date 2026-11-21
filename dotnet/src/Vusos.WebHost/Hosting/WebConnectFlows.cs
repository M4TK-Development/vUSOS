using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vusos.Core.Adapters;
using Vusos.Core.Hosting;
using Vusos.Core.Services;
using Vusos.UI.Services;

namespace Vusos.WebHost.Hosting;

/// <summary>
/// Przepływy łączenia dla przeglądarki. USOS: OAuth 1.0a z callbackiem na ten serwer (<c>/connect/usos/callback</c>).
/// Platon: przeglądarka nie złapie schematu <c>moodlemobile://</c>, więc użytkownik wkleja token (komunikat w wyjątku).
/// W trybie <c>Adapters:Mode=mock</c> „połącz” od razu podłącza dane demonstracyjne.
/// </summary>
public sealed class WebConnectFlows(ICurrentUserCore user, NavigationManager navigation, IOptions<ServerOptions> server) : IConnectFlows
{
    public const string PlatonPasteMessage = "W przeglądarce wklej token z launch.php";

    private string BaseUrl => (string.IsNullOrWhiteSpace(server.Value.PublicUrl) ? navigation.BaseUri : server.Value.PublicUrl).TrimEnd('/');

    public string UsosCallbackUrl => BaseUrl + "/connect/usos/callback";

    /// <summary>
    /// Moodle składa adres powrotu jako <c>{urlscheme}://token=BASE64</c>. Podając jako „schemat” pełny adres naszego
    /// callbacku z otwartym query, dostajemy <c>https://host/connect/platon/callback?s://token=BASE64</c>, czyli zwykłe
    /// przekierowanie HTTP do nas. TODO(verify): działa, o ile administrator nie ustawił <c>forcedurlscheme</c>.
    /// </summary>
    public string PlatonCallbackScheme => BaseUrl + "/connect/platon/callback?s";

    public async Task StartUsosAsync()
    {
        var sp = user.Services;
        if (sp.GetRequiredService<AdapterOptions>().IsMock)
        {
            await sp.GetRequiredService<IConnectionManager>().ConnectUsosAsync(new UsosMockCredentials());
            return;
        }

        var flow = sp.GetRequiredService<UsosConnectFlow>();
        if (!flow.IsConfigured)
            throw new InvalidOperationException("Połączenie z USOS nie jest skonfigurowane na tym serwerze (University:SigningServerUrl albo UsosConsumerKey i UsosConsumerSecret).");
        var authorizeUrl = await flow.BeginAsync(UsosCallbackUrl);
        if (!Uri.TryCreate(authorizeUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("USOS zwrócił nieprawidłowy adres autoryzacji.");
        navigation.NavigateTo(uri.ToString(), forceLoad: true);
    }

    public async Task StartPlatonAsync()
    {
        var sp = user.Services;
        if (sp.GetRequiredService<AdapterOptions>().IsMock)
        {
            await sp.GetRequiredService<IConnectionManager>().ConnectPlatonAsync(new PlatonMockCredentials());
            return;
        }

        // Przeglądarka nie złapie schematu moodlemobile://, więc powrót z launch.php kierujemy na nasz endpoint HTTP.
        var launch = sp.GetRequiredService<PlatonConnectFlow>().BuildLaunchUrl(PlatonCallbackScheme);
        navigation.NavigateTo(launch, forceLoad: true);
    }

    public Task SubmitPlatonTokenAsync(string pasted)
        => user.Services.GetRequiredService<PlatonConnectFlow>().ConnectWithTokenAsync(pasted);

    public Task SubmitUsosCookiesAsync(string cookies)
        => user.Services.GetRequiredService<UsosConnectFlow>().ConnectWithCookiesAsync(cookies);

    public Task SubmitPlatonCookiesAsync(string cookies, string sesskey)
        => user.Services.GetRequiredService<PlatonConnectFlow>().ConnectWithCookiesAsync(cookies, sesskey);
}
