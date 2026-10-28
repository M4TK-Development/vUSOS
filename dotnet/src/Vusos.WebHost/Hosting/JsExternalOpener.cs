using Microsoft.AspNetCore.DataProtection;
using Microsoft.JSInterop;
using Vusos.UI.Services;

namespace Vusos.WebHost.Hosting;

/// <summary>
/// Blazor Server: adresy otwiera przeglądarka (window.open), a pobrane pliki są serwowane przez /dl/{token}
/// (token = zaszyfrowana, krótkotrwała ścieżka; endpoint dodatkowo sprawdza, że plik leży w katalogu materiałów).
/// </summary>
public sealed class JsExternalOpener(IJSRuntime js, IDataProtectionProvider dataProtection) : IExternalOpener
{
    public const string Purpose = "Vusos.WebHost.FileDownload";

    private readonly ITimeLimitedDataProtector _protector = dataProtection.CreateProtector(Purpose).ToTimeLimitedDataProtector();

    public async Task OpenUrlAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto" or "tel"))
            throw new ArgumentException("Dozwolone są tylko adresy http(s), mailto i tel.", nameof(url));
        await js.InvokeVoidAsync("vusos.openUrl", uri.ToString());
    }

    public async Task OpenFileAsync(string localPath)
    {
        var token = _protector.Protect(localPath, TimeSpan.FromMinutes(10));
        await js.InvokeVoidAsync("vusos.openUrl", "/dl/" + Uri.EscapeDataString(token));
    }
}
