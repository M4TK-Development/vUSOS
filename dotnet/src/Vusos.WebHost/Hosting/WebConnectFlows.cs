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

    public string UsosCallbackUrl
    {
        get
        {
            var baseUrl = string.IsNullOrWhiteSpace(server.Value.PublicUrl) ? navigation.BaseUri : server.Value.PublicUrl;
            return baseUrl.TrimEnd('/') + "/connect/usos/callback";
        }
    }

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

        // Ustawienia pokazują komunikat wyjątku pod kartą Platona – to jest cała „instrukcja” dla wersji przeglądarkowej.
        var launch = sp.GetRequiredService<PlatonConnectFlow>().BuildLaunchUrl("moodlemobile");
        throw new NotSupportedException(
            $"{PlatonPasteMessage}: otwórz {launch} w nowej karcie, zaloguj się kontem uczelni, skopiuj adres moodlemobile://token=… " +
            "(przeglądarka nie potrafi go otworzyć, ale pokaże go w pasku adresu) i wklej go w polu „Wklej token” poniżej.");
    }

    public Task SubmitPlatonTokenAsync(string pasted)
        => user.Services.GetRequiredService<PlatonConnectFlow>().ConnectWithTokenAsync(pasted);

    public Task SubmitUsosCookiesAsync(string cookies)
        => user.Services.GetRequiredService<UsosConnectFlow>().ConnectWithCookiesAsync(cookies);

    public Task SubmitPlatonCookiesAsync(string cookies, string sesskey)
        => user.Services.GetRequiredService<PlatonConnectFlow>().ConnectWithCookiesAsync(cookies, sesskey);
}
