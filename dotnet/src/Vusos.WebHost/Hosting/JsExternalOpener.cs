using Microsoft.JSInterop;
using Vusos.UI.Services;

namespace Vusos.WebHost.Hosting;

/// <summary>
/// Blazor Server: adresy otwiera przeglądarka (window.open), a pobrane pliki są serwowane przez /dl/{token}
/// (token = zaszyfrowana, krótkotrwała ścieżka powiązana z użytkownikiem; endpoint sprawdza użytkownika i katalog materiałów).
/// </summary>
public sealed class JsExternalOpener(IJSRuntime js, DownloadTokens tokens, CircuitUser user) : IExternalOpener
{
    public async Task OpenUrlAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto" or "tel"))
            throw new ArgumentException("Dozwolone są tylko adresy http(s), mailto i tel.", nameof(url));
        await js.InvokeVoidAsync("vusos.openUrl", uri.ToString());
    }

    public async Task OpenFileAsync(string localPath)
    {
        var hash = user.UserHash ?? throw new InvalidOperationException("Brak zalogowanego użytkownika.");
        var token = tokens.Issue(hash, localPath);
        await js.InvokeVoidAsync("vusos.openUrl", "/dl/" + Uri.EscapeDataString(token));
    }
}
