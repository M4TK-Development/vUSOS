using Vusos.Core.Adapters;
using Vusos.Core.Hosting;
using Vusos.Core.Services;
using Vusos.UI.Services;

namespace Vusos.App.Services;

/// <summary>
/// Przepływy łączenia USOS / Platon: logika w Vusos.Core (<see cref="UsosConnectFlow"/>, <see cref="PlatonConnectFlow"/>),
/// tu tylko otwarcie przeglądarki. Powrót następuje deep linkiem vusos://… → <see cref="DeepLinkRouter"/>.
/// </summary>
public sealed class MauiConnectFlows(
    UsosConnectFlow usos,
    PlatonConnectFlow platon,
    IConnectionManager connections,
    IExternalOpener opener,
    UniversityOptions university,
    AdapterOptions adapters) : IConnectFlows
{
    public async Task StartUsosAsync()
    {
        if (adapters.IsMock)
        {
            // Tryb mock: ConnectionManager i tak podmienia poświadczenia na demonstracyjne.
            await connections.ConnectUsosAsync(new UsosMockCredentials());
            return;
        }

        var authorizeUrl = await usos.BeginAsync($"{university.NativeScheme}://usos");
        await opener.OpenUrlAsync(authorizeUrl);
    }

    public async Task StartPlatonAsync()
    {
        if (adapters.IsMock)
        {
            await connections.ConnectPlatonAsync(new PlatonMockCredentials());
            return;
        }

        await opener.OpenUrlAsync(platon.BuildLaunchUrl(university.NativeScheme));
    }

    public Task SubmitPlatonTokenAsync(string pasted) => platon.ConnectWithTokenAsync(pasted);

    public Task SubmitUsosCookiesAsync(string cookies) => usos.ConnectWithCookiesAsync(cookies);

    public Task SubmitPlatonCookiesAsync(string cookies, string sesskey) => platon.ConnectWithCookiesAsync(cookies, sesskey);
}
