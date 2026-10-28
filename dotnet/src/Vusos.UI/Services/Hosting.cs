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

/// <summary>Sesja użytkownika aplikacji (konto M365 uczelni). Host: MSAL w MAUI, dev/OIDC w Blazor Server.</summary>
public interface IAuthSession
{
    bool IsSignedIn { get; }
    string? DisplayName { get; }
    string? Email { get; }
    Task SignInAsync();
    Task SignOutAsync();
    event Action? Changed;
}
