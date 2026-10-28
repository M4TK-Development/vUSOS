using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;
using Vusos.Core.Domain;
using Vusos.Core.Hosting;
using Vusos.Core.Services;
using Vusos.UI.Services;

namespace Vusos.App.Services;

/// <summary>
/// Jedyne miejsce, do którego kod platformowy (Android Intent, iOS/Mac AppDelegate, Windows AppInstance) przekazuje
/// adresy <c>vusos://…</c>. Linki przychodzące zanim aplikacja jest gotowa (zimny start) są kolejkowane do <see cref="Attach"/>.
/// Linki MSAL (<c>msal{clientId}://auth</c>, <c>vusos://auth</c>) są pomijane – obsługuje je MSAL.
/// </summary>
public static class DeepLinkRouter
{
    private static readonly object Gate = new();
    private static readonly List<string> Pending = [];
    private static IServiceProvider? _services;
    private static string? _lastUrl;
    private static long _lastTick;

    /// <summary>Komunikat do pokazania użytkownikowi jako toast (subskrybuje go <c>Components/Routes.razor</c>).</summary>
    public static event Action<string, ToastKind>? Notification;

    /// <summary>Wołane z <c>MauiProgram</c> po zbudowaniu aplikacji.</summary>
    public static void Attach(IServiceProvider services)
    {
        string[] pending;
        lock (Gate)
        {
            if (_services is not null)
                return;
            _services = services;
            pending = [.. Pending];
            Pending.Clear();
        }

        services.GetRequiredService<DeepLinkDispatcher>().Connected += OnConnected;

        foreach (var url in pending)
            Handle(url);
    }

    public static void Handle(Uri? uri) => Handle(uri?.OriginalString);

    /// <summary>Punkt wejścia z dowolnego wątku.</summary>
    public static void Handle(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        IServiceProvider services;
        lock (Gate)
        {
            if (_services is null)
            {
                Pending.Add(url);
                return;
            }

            // Ten sam link potrafi dotrzeć dwukrotnie (OnCreate + OnNewIntent, aktywacja + przekierowanie) – USOS callback jest jednorazowy.
            var now = Environment.TickCount64;
            if (string.Equals(url, _lastUrl, StringComparison.Ordinal) && now - _lastTick < 5000)
                return;
            _lastUrl = url;
            _lastTick = now;
            services = _services;
        }

        MainThread.BeginInvokeOnMainThread(() => _ = DispatchAsync(services, url));
    }

    private static async Task DispatchAsync(IServiceProvider services, string url)
    {
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("Vusos.App.DeepLink");
        try
        {
            var scheme = services.GetRequiredService<UniversityOptions>().NativeScheme;
            if (!TryParse(url, scheme, out var uri))
                return;

            // vusos://auth… – przekierowanie MSAL, nie dotyczy przepływów USOS/Platon.
            if (string.Equals(uri.Host, "auth", StringComparison.OrdinalIgnoreCase))
                return;

            var handled = await services.GetRequiredService<DeepLinkDispatcher>().DispatchAsync(uri);
            if (!handled)
                logger?.LogInformation("Nierozpoznany deep link (pominięty)");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Obsługa deep linku nie powiodła się");
            Notification?.Invoke("Nie udało się dokończyć łączenia: " + ex.Message, ToastKind.Error);
        }
    }

    /// <summary>
    /// Zwraca true tylko dla naszego schematu. Link Platona <c>vusos://token=BASE64</c> bywa odrzucany przez <see cref="Uri"/>
    /// (znaki w „hoście”), więc w razie niepowodzenia składamy równoważny adres <c>vusos://platon/?token=…</c>
    /// – <c>PlatonConnectFlow</c> szuka <c>token=</c> w oryginalnym tekście.
    /// </summary>
    private static bool TryParse(string url, string scheme, out Uri uri)
    {
        var prefix = scheme + "://";
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            uri = null!;
            return false;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed is not null)
        {
            uri = parsed;
            return true;
        }

        var rest = url[prefix.Length..];
        if (rest.StartsWith("token=", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate($"{prefix}platon/?{rest}", UriKind.Absolute, out var rebuilt))
        {
            uri = rebuilt;
            return true;
        }

        uri = null!;
        return false;
    }

    private static void OnConnected(object? sender, SourceSystemConnected e)
    {
        var message = e.System == SourceSystem.Usos ? "Połączono z USOS." : "Połączono z Platonem.";
        Notification?.Invoke(message, ToastKind.Success);
    }
}
