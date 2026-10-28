using Microsoft.Extensions.Logging;
using Vusos.Core.Domain;
using Vusos.Core.Hosting;
using Vusos.Core.Offline;
using Vusos.Core.Platform;
using Vusos.Core.Services;

namespace Vusos.UI.Services;

/// <summary>
/// Wylogowanie w trybie opartym o połączenia (rozłączenie USOS i Platona, usunięcie tożsamości i danych lokalnych); host dostarcza implementację (MAUI: <see cref="LocalDataSignOutHandler"/>,
/// WebHost: usunięcie danych użytkownika i cookie sesji).
/// </summary>
public interface ISignOutHandler
{
    Task SignOutAsync();
}

/// <summary>
/// <see cref="IAuthSession"/> bez dostawcy tożsamości: „zalogowany” znaczy „połączony z USOS i/lub Platonem”. Imię, nazwisko i e-mail
/// pochodzą z <see cref="ConnectionIdentity"/> (profil USOS albo site info Platona), zapisane lokalnie, więc aplikacja otwiera
/// się zalogowana także offline. Nie wymaga rejestracji aplikacji ani zgody administratora w Entra ID.
/// </summary>
public class ConnectionAuthSession(ConnectionIdentity identity, ISignOutHandler signOut) : IAuthSession
{
    public bool IsSignedIn => identity.IsSignedIn;
    public string? DisplayName => identity.DisplayName;
    public string? Email => identity.Email;
    public bool SupportsExternalIdentityProvider => false;

    // Zdarzenie przekazywane wprost: subskrybent (komponent) sam się wypisuje, więc sesja o krótszym życiu niż tożsamość nie przecieka.
    public event Action? Changed
    {
        add => identity.Changed += value;
        remove => identity.Changed -= value;
    }

    public Task InitializeAsync() => identity.InitializeAsync();

    /// <summary>Bez dostawcy tożsamości „logowanie” to ponowne ustalenie tożsamości z istniejących połączeń.</summary>
    public virtual Task SignInAsync() => identity.RefreshAsync();

    /// <summary>Delegowane do <see cref="ISignOutHandler"/>, który rozłącza oba systemy, czyści tożsamość i dane lokalne.</summary>
    public virtual Task SignOutAsync() => signOut.SignOutAsync();
}

/// <summary>
/// Wylogowanie lokalne (MAUI): rozłącza USOS i Platon, usuwa tożsamość, cache, rekordy i pliki pobranych materiałów
/// (ARCHITECTURE.md → Bezpieczeństwo). Błędy poszczególnych kroków są logowane i nie przerywają reszty.
/// </summary>
public sealed class LocalDataSignOutHandler(
    IConnectionManager connections,
    ConnectionIdentity identity,
    IMaterialService materials,
    ICacheStore cache,
    IAppPaths paths,
    ILogger<LocalDataSignOutHandler>? logger = null) : ISignOutHandler
{
    public async Task SignOutAsync()
    {
        foreach (var system in new[] { SourceSystem.Usos, SourceSystem.Platon })
        {
            try { await connections.DisconnectAsync(system).ConfigureAwait(false); }
            catch (Exception ex) { logger?.LogWarning(ex, "Rozłączenie {System} nie powiodło się", system); }
        }

        try { await identity.ClearAsync().ConfigureAwait(false); }
        catch (Exception ex) { logger?.LogWarning(ex, "Usuwanie tożsamości nie powiodło się"); }

        try
        {
            foreach (var material in await materials.ListAsync().ConfigureAwait(false))
                await materials.RemoveAsync(material.Id).ConfigureAwait(false);
        }
        catch (Exception ex) { logger?.LogWarning(ex, "Usuwanie materiałów nie powiodło się"); }

        try { await cache.ClearAsync().ConfigureAwait(false); }
        catch (Exception ex) { logger?.LogWarning(ex, "Czyszczenie cache nie powiodło się"); }

        try
        {
            if (Directory.Exists(paths.MaterialsDirectory))
                Directory.Delete(paths.MaterialsDirectory, recursive: true);
        }
        catch (Exception ex) { logger?.LogWarning(ex, "Usuwanie katalogu materiałów nie powiodło się"); }
    }
}
