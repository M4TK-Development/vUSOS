namespace Vusos.UI.Services;

/// <summary>Otwieranie zewnętrznych adresów i lokalnych plików – implementacja zależy od hosta (MAUI / Blazor Server).</summary>
public interface IExternalOpener
{
    /// <summary>Otwiera adres w (systemowej) przeglądarce.</summary>
    Task OpenUrlAsync(string url);
    /// <summary>Otwiera pobrany plik programem systemowym (lub udostępnia go do pobrania w przeglądarce).</summary>
    Task OpenFileAsync(string localPath);
}

/// <summary>Przepływy podłączania systemów uczelni; host wie, jak otworzyć przeglądarkę / złapać deep link.</summary>
public interface IConnectFlows
{
    Task StartUsosAsync();
    Task StartPlatonAsync();
    Task SubmitPlatonTokenAsync(string pasted);
    Task SubmitUsosCookiesAsync(string cookies);
    Task SubmitPlatonCookiesAsync(string cookies, string sesskey);
}

/// <summary>
/// Sesja użytkownika aplikacji. Domyślnie tożsamość wynika z połączeń z USOS/Platonem (<see cref="ConnectionAuthSession"/>:
/// student loguje się kontem M365 uczelni na ich stronach, bez rejestracji aplikacji w Entra ID). Opcjonalnie host może
/// logować przez własnego dostawcę tożsamości: MSAL w MAUI (<c>University:EntraClientId</c>), OIDC/dev w Blazor Server.
/// </summary>
public interface IAuthSession
{
    bool IsSignedIn { get; }
    string? DisplayName { get; }
    string? Email { get; }

    /// <summary>
    /// Czy host ma własne logowanie przez dostawcę tożsamości (MSAL / OIDC) i <see cref="SignInAsync"/> je uruchamia.
    /// <c>false</c> = logowanie przez połączenie z Platonem lub USOS (strona logowania pokazuje przyciski łączenia).
    /// </summary>
    bool SupportsExternalIdentityProvider => false;

    /// <summary>Wczytuje zapisany stan (offline, z lokalnego magazynu) przed pierwszym sprawdzeniem <see cref="IsSignedIn"/>.</summary>
    Task InitializeAsync() => Task.CompletedTask;

    Task SignInAsync();

    /// <summary>
    /// Wylogowanie. W trybie opartym o połączenia: rozłącza USOS i Platon, usuwa tożsamość oraz lokalne dane
    /// (cache i pobrane materiały).
    /// </summary>
    Task SignOutAsync();

    event Action? Changed;
}
