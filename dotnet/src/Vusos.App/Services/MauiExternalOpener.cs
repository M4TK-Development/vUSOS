using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using Vusos.UI.Services;

namespace Vusos.App.Services;

/// <summary>Otwieranie adresów w przeglądarce systemowej i plików programem systemowym.</summary>
public sealed class MauiExternalOpener : IExternalOpener
{
    public Task OpenUrlAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException("Nieprawidłowy adres: " + url, nameof(url));

        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            switch (uri.Scheme.ToLowerInvariant())
            {
                case "http":
                case "https":
                    await Browser.Default.OpenAsync(uri, BrowserLaunchMode.SystemPreferred);
                    break;
                case "mailto":
                case "tel":
                    await Launcher.Default.OpenAsync(uri);
                    break;
                default:
                    // Inne schematy (file:, javascript:, intent: …) celowo ignorujemy.
                    break;
            }
        });
    }

    public Task OpenFileAsync(string localPath)
    {
        if (!File.Exists(localPath))
            throw new FileNotFoundException("Plik nie istnieje na urządzeniu.", localPath);

        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            // TODO(compile-check): Launcher.Default.OpenAsync(OpenFileRequest) – ReadOnlyFile jest w Microsoft.Maui.Storage.
            await Launcher.Default.OpenAsync(new OpenFileRequest(Path.GetFileName(localPath), new ReadOnlyFile(localPath)));
        });
    }
}
