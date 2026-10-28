using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.Storage;
using Vusos.App.Services;
using Vusos.Core;
using Vusos.Core.Hosting;
using Vusos.Core.Platform;
using Vusos.Core.Services;
using Vusos.UI.Services;

namespace Vusos.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // Blazor Hybrid hostuje Vusos.UI (Razor Class Library); pliki statyczne: _content/Vusos.UI/...
        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // ---- Konfiguracja: Resources/Raw/appsettings.json (zasób aplikacji) + opcjonalne nadpisania użytkownika ----
        var configuration = BuildConfiguration();
        builder.Configuration.AddConfiguration(configuration);

        // ---- Vusos.Core + adaptery + UI ----
        builder.Services
            .AddVusosCore(configuration)
            .AddVusosAdapters()
            .AddVusosUi();

        // ---- Usługi platformowe (kontrakty Vusos.Core.Platform) ----
        var allowInsecureFallback = ReadBool(configuration["App:AllowInsecureSecureStoreFallback"], defaultValue: IsDebugBuild);
        builder.Services.AddSingleton<ISecureStore>(sp =>
            new MauiSecureStore(allowInsecureFallback, sp.GetService<ILogger<MauiSecureStore>>()));
        builder.Services.AddSingleton<Vusos.Core.Platform.IConnectivity, MauiConnectivity>();
        builder.Services.AddSingleton<IAppPaths, MauiAppPaths>();

        // ---- Usługi hosta wymagane przez Vusos.UI ----
        builder.Services.AddSingleton<IExternalOpener, MauiExternalOpener>();
        builder.Services.AddSingleton<IConnectFlows, MauiConnectFlows>();

        // ---- Sesja użytkownika ----
        // Domyślnie bez rejestracji aplikacji w Entra ID: tożsamość z połączeń z USOS/Platonem (student loguje się kontem M365
        // uczelni w przeglądarce systemowej na ich stronach). MSAL tylko gdy skonfigurowano University:EntraClientId.
        var useMsal = !string.IsNullOrWhiteSpace(configuration["University:EntraClientId"]);
        if (useMsal)
        {
            builder.Services.AddSingleton<MsalAuthSession>();
            builder.Services.AddSingleton<IAuthSession>(sp => sp.GetRequiredService<MsalAuthSession>());
        }
        else
        {
            builder.Services.AddVusosConnectionAuth(); // ConnectionAuthSession + LocalDataSignOutHandler (Vusos.UI)
        }

        var app = builder.Build();

        // Deep linki (vusos://...) – od tej chwili router ma dostęp do DeepLinkDispatcher; zakolejkowane linki są obsługiwane.
        DeepLinkRouter.Attach(app.Services);

        // Odtworzenie tożsamości z ISecureStore (aplikacja otwiera się „zalogowana” także offline); AuthGate też woła InitializeAsync.
        _ = app.Services.GetRequiredService<IAuthSession>().InitializeAsync();

        // Synchronizacja w tle po odzyskaniu sieci (kolejka testów + materiały).
        try
        {
            app.Services.GetRequiredService<BackgroundSyncCoordinator>().Start();
        }
        catch (Exception ex)
        {
            app.Services.GetService<ILoggerFactory>()?.CreateLogger("Vusos.App")
                .LogWarning(ex, "Nie udało się uruchomić BackgroundSyncCoordinator");
        }

        return app;
    }

    private static bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif

    private static bool ReadBool(string? value, bool defaultValue) =>
        bool.TryParse(value, out var parsed) ? parsed : defaultValue;

    /// <summary>
    /// appsettings.json z paczki aplikacji, potem (opcjonalnie) appsettings.user.json z katalogu danych aplikacji.
    /// Zmienne środowiskowe VUSOS_* nie są czytane (aplikacja mobilna).
    /// </summary>
    private static IConfiguration BuildConfiguration()
    {
        var cb = new ConfigurationBuilder();

        // Kopia do MemoryStream: źródło JSON czyta strumień dopiero przy Build(), a strumień paczki zamykamy od razu.
        using (var stream = FileSystem.OpenAppPackageFileAsync("appsettings.json").GetAwaiter().GetResult())
        {
            var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            cb.AddJsonStream(copy);
        }

        return AddUserOverrides(cb).Build();
    }

    private static ConfigurationBuilder AddUserOverrides(ConfigurationBuilder cb)
    {
        try
        {
            var path = Path.Combine(FileSystem.AppDataDirectory, "appsettings.user.json");
            if (File.Exists(path))
                cb.AddJsonStream(new MemoryStream(File.ReadAllBytes(path)));
        }
        catch (Exception)
        {
            // Uszkodzony / niedostępny plik nadpisań nie może zablokować startu aplikacji.
        }
        return cb;
    }
}
