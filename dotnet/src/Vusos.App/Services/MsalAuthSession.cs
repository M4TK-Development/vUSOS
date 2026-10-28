using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.Maui.ApplicationModel;
using Vusos.Core.Domain;
using Vusos.Core.Offline;
using Vusos.Core.Platform;
using Vusos.Core.Services;
using Vusos.UI.Services;

namespace Vusos.App.Services;

/// <summary>
/// Logowanie kontem M365 uczelni (MSAL.NET, publiczny klient, przeglądarka systemowa). Aplikacja nie woła żadnego API
/// z tokenem – potrzebuje tylko tożsamości (e-mail/nazwa z ID tokenu), więc profil zapisujemy w <see cref="ISecureStore"/>
/// i aplikacja po restarcie (także offline) jest od razu „zalogowana”. Bez <c>University:EntraClientId</c> działa lokalne
/// logowanie demonstracyjne (tryb mock).
/// </summary>
public sealed class MsalAuthSession : IAuthSession
{
    private const string NameKey = "vusos.auth.name";
    private const string EmailKey = "vusos.auth.email";
    private const string ModeKey = "vusos.auth.mode";
    private const string ModeMsal = "msal";
    private const string ModeDemo = "demo";

    private static readonly string[] Scopes = ["openid", "profile", "email", "User.Read"];

    private readonly UniversityOptions _university;
    private readonly ISecureStore _store;
    private readonly IConnectionManager _connections;
    private readonly IMaterialService _materials;
    private readonly ICacheStore _cache;
    private readonly IAppPaths _paths;
    private readonly ILogger<MsalAuthSession>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPublicClientApplication? _pca;
    private Task? _initialization;

    public MsalAuthSession(
        UniversityOptions university,
        ISecureStore store,
        IConnectionManager connections,
        IMaterialService materials,
        ICacheStore cache,
        IAppPaths paths,
        ILogger<MsalAuthSession>? logger = null)
    {
        _university = university;
        _store = store;
        _connections = connections;
        _materials = materials;
        _cache = cache;
        _paths = paths;
        _logger = logger;
    }

    public bool IsSignedIn { get; private set; }
    public string? DisplayName { get; private set; }
    public string? Email { get; private set; }
    public event Action? Changed;

    private bool UseMsal => !string.IsNullOrWhiteSpace(_university.EntraClientId);

    /// <summary>Odtwarza profil z magazynu. Bezpieczne do wielokrotnego wołania.</summary>
    public Task InitializeAsync() => _initialization ??= LoadProfileAsync();

    private async Task LoadProfileAsync()
    {
        try
        {
            var mode = await _store.GetAsync(ModeKey).ConfigureAwait(false);
            var email = await _store.GetAsync(EmailKey).ConfigureAwait(false);
            var name = await _store.GetAsync(NameKey).ConfigureAwait(false);

            var expectedMode = UseMsal ? ModeMsal : ModeDemo;
            if (string.IsNullOrEmpty(email) || mode != expectedMode)
                return;
            if (mode == ModeMsal && !IsAllowedDomain(email))
                return; // konfiguracja domen zmieniła się od ostatniego logowania

            DisplayName = string.IsNullOrWhiteSpace(name) ? FormatName(email) : name;
            Email = email;
            IsSignedIn = true;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Nie udało się odczytać zapisanego profilu użytkownika");
        }
    }

    public async Task SignInAsync()
    {
        await InitializeAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!UseMsal)
            {
                var domain = _university.AllowedEmailDomains.FirstOrDefault() ?? "stu.vistula.edu.pl";
                await CompleteSignInAsync("Student Demo", "demo@" + domain, ModeDemo).ConfigureAwait(false);
                return;
            }

            AuthenticationResult result;
            try
            {
                result = await AcquireAsync().ConfigureAwait(false);
            }
            catch (MsalClientException ex) when (ex.ErrorCode == MsalError.AuthenticationCanceledError) // TODO(compile-check): nazwa stałej
            {
                throw new InvalidOperationException("Logowanie zostało przerwane.", ex);
            }

            var email = ExtractEmail(result);
            if (string.IsNullOrEmpty(email) || !IsAllowedDomain(email))
            {
                await RemoveMsalAccountsAsync().ConfigureAwait(false);
                var allowed = string.Join(", ", _university.AllowedEmailDomains.Select(d => "@" + d));
                throw new InvalidOperationException(
                    $"Konto {(string.IsNullOrEmpty(email) ? "bez adresu e-mail" : email)} nie należy do uczelni. Zaloguj się kontem {allowed}.");
            }

            await CompleteSignInAsync(ExtractName(result) ?? FormatName(email), email, ModeMsal).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignOutAsync()
    {
        await InitializeAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await RemoveMsalAccountsAsync().ConfigureAwait(false);
            await WipeLocalDataAsync().ConfigureAwait(false);

            foreach (var key in new[] { NameKey, EmailKey, ModeKey })
            {
                try { await _store.RemoveAsync(key).ConfigureAwait(false); }
                catch (Exception ex) { _logger?.LogWarning(ex, "Nie udało się usunąć klucza {Key}", key); }
            }

            IsSignedIn = false;
            DisplayName = null;
            Email = null;
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
    }

    // ---------- MSAL ----------

    private async Task<AuthenticationResult> AcquireAsync()
    {
        var pca = GetClient();

        try
        {
            var account = (await pca.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
            if (account is not null)
                return await pca.AcquireTokenSilent(Scopes, account).ExecuteAsync().ConfigureAwait(false);
        }
        catch (MsalUiRequiredException)
        {
            // wymagane logowanie interaktywne
        }
        catch (MsalException ex)
        {
            _logger?.LogInformation(ex, "Ciche logowanie nie powiodło się, przechodzę do interaktywnego");
        }

        // MSAL na Androidzie/iOS wymaga wołania z wątku UI.
        return await MainThread.InvokeOnMainThreadAsync(() => AcquireInteractiveAsync(pca)).ConfigureAwait(false);
    }

    private static Task<AuthenticationResult> AcquireInteractiveAsync(IPublicClientApplication pca)
    {
        var builder = pca.AcquireTokenInteractive(Scopes)
            .WithPrompt(Prompt.SelectAccount)
            .WithUseEmbeddedWebView(false); // przeglądarka systemowa

#if ANDROID
        // TODO(compile-check): Platform.CurrentActivity (Microsoft.Maui.ApplicationModel) + WithParentActivityOrWindow(object).
        builder = builder.WithParentActivityOrWindow(Microsoft.Maui.ApplicationModel.Platform.CurrentActivity);
#elif IOS
        // TODO(compile-check): Platform.GetCurrentUIViewController() + WithParentActivityOrWindow(object).
        builder = builder.WithParentActivityOrWindow(Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController());
#else
        // Windows / Mac Catalyst: MSAL korzysta z zasobu net6.0 (brak zasobu maccatalyst/windows w pakiecie) – przeglądarka
        // systemowa + nasłuch http://localhost. Link otwieramy przez MAUI, bo Process.Start bywa niedostępny w sandboxie.
        // TODO(compile-check): w tej gałęzi nie jest potrzebny uchwyt okna (brak embedded WebView / brokera WAM).
        builder = builder.WithSystemWebViewOptions(new SystemWebViewOptions
        {
            OpenBrowserAsync = async uri => { await Browser.Default.OpenAsync(uri, BrowserLaunchMode.SystemPreferred); },
        });
#endif

        return builder.ExecuteAsync();
    }

    private IPublicClientApplication GetClient()
    {
        if (_pca is not null)
            return _pca;

        var clientId = _university.EntraClientId!.Trim();
        var builder = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(_university.EntraAuthority);

#if ANDROID || IOS
        // Mobile: własny schemat msal{clientId}://auth (BrowserTabActivity / CFBundleURLTypes).
        builder = builder.WithRedirectUri($"msal{clientId}://auth");
#else
        // Desktop (Windows, Mac Catalyst): pętla zwrotna http://localhost – bez portu, MSAL wybiera wolny port.
        // NuGet dla net10.0-maccatalyst/windows wybiera zasób net6.0 pakietu MSAL (sprawdzone dla 4.66.2).
        builder = builder.WithRedirectUri("http://localhost");
#endif

#if IOS
        // TODO(compile-check): WithIosKeychainSecurityGroup istnieje tylko w zasobie iOS MSAL; grupa musi być w Entitlements.plist.
        builder = builder.WithIosKeychainSecurityGroup(Foundation.NSBundle.MainBundle.BundleIdentifier ?? "pl.m4tk.vusos");
#endif

        _pca = builder.Build();
        return _pca;
    }

    private async Task RemoveMsalAccountsAsync()
    {
        if (!UseMsal)
            return;
        try
        {
            var pca = GetClient();
            foreach (var account in await pca.GetAccountsAsync().ConfigureAwait(false))
                await pca.RemoveAsync(account).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Nie udało się usunąć kont z pamięci podręcznej MSAL");
        }
    }

    // ---------- profil ----------

    private async Task CompleteSignInAsync(string name, string email, string mode)
    {
        await _store.SetAsync(NameKey, name).ConfigureAwait(false);
        await _store.SetAsync(EmailKey, email).ConfigureAwait(false);
        await _store.SetAsync(ModeKey, mode).ConfigureAwait(false);

        DisplayName = name;
        Email = email;
        IsSignedIn = true;
        Changed?.Invoke();
    }

    private static string? ExtractEmail(AuthenticationResult result)
    {
        var principal = result.ClaimsPrincipal; // TODO(compile-check): AuthenticationResult.ClaimsPrincipal
        var email = principal?.FindFirst("email")?.Value
                    ?? principal?.FindFirst("preferred_username")?.Value
                    ?? principal?.FindFirst("upn")?.Value
                    ?? result.Account?.Username;
        return string.IsNullOrWhiteSpace(email) ? null : email.Trim();
    }

    private static string? ExtractName(AuthenticationResult result)
    {
        var name = result.ClaimsPrincipal?.FindFirst("name")?.Value;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>Dokładne dopasowanie domeny e-mail do <see cref="UniversityOptions.AllowedEmailDomains"/> (pusta lista = bez ograniczeń).</summary>
    private bool IsAllowedDomain(string email)
    {
        var allowed = _university.AllowedEmailDomains;
        if (allowed.Length == 0)
            return true;

        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1)
            return false;

        var domain = email[(at + 1)..].Trim();
        return allowed.Any(d => string.Equals(d.Trim(), domain, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatName(string email)
    {
        var local = email.Split('@')[0];
        var parts = local.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..]);
        var name = string.Join(' ', parts);
        return name.Length == 0 ? email : name;
    }

    // ---------- wylogowanie: czyszczenie danych lokalnych (ARCHITECTURE.md → Bezpieczeństwo) ----------

    private async Task WipeLocalDataAsync()
    {
        foreach (var system in new[] { SourceSystem.Usos, SourceSystem.Platon })
        {
            try { await _connections.DisconnectAsync(system).ConfigureAwait(false); }
            catch (Exception ex) { _logger?.LogWarning(ex, "Rozłączenie {System} nie powiodło się", system); }
        }

        try
        {
            foreach (var material in await _materials.ListAsync().ConfigureAwait(false))
                await _materials.RemoveAsync(material.Id).ConfigureAwait(false);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "Usuwanie materiałów nie powiodło się"); }

        try { await _cache.ClearAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger?.LogWarning(ex, "Czyszczenie cache nie powiodło się"); }

        try
        {
            if (Directory.Exists(_paths.MaterialsDirectory))
                Directory.Delete(_paths.MaterialsDirectory, recursive: true);
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "Usuwanie katalogu materiałów nie powiodło się"); }
    }
}
