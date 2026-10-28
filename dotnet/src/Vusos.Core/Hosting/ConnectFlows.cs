using Microsoft.Extensions.Options;
using Vusos.Core.Adapters;
using Vusos.Core.Adapters.Platon;
using Vusos.Core.Adapters.Usos;
using Vusos.Core.Platform;
using Vusos.Core.Services;

namespace Vusos.Core.Hosting;

/// <summary>
/// Logika łączenia z USOS i Platonem wspólna dla hostów (MAUI, Blazor Server). Host dostarcza tylko:
/// otwarcie przeglądarki i odebranie deep linku / callbacku.
/// </summary>
public sealed class UsosConnectFlow(
    IConnectionManager connections,
    ISecureStore secureStore,
    IHttpClientFactory httpFactory,
    IOptions<UniversityOptions> university)
{
    private const string PendingKey = "usos:oauth:pending";

    public static readonly IReadOnlyList<string> DefaultScopes =
        ["offline_access", "studies", "grades", "payments", "email", "mailclient", "personal", "photo"];

    public bool IsConfigured => UsosAdapterFactory.CreateOAuthClient(university.Value, httpFactory.CreateClient("usos"), university.Value.SigningServerAppKey) is not null;

    /// <summary>Krok 1: pobiera request token, zapamiętuje sekret, zwraca URL autoryzacji do otwarcia w przeglądarce.</summary>
    public async Task<string> BeginAsync(string callbackUrl, IReadOnlyList<string>? scopes = null, CancellationToken ct = default)
    {
        var client = UsosAdapterFactory.CreateOAuthClient(university.Value, httpFactory.CreateClient("usos"), university.Value.SigningServerAppKey)
            ?? throw new AdapterUnavailableException("Brak konfiguracji OAuth USOS (SigningServerUrl albo consumer key/secret)") { IsTransient = false };
        var rt = await client.RequestTokenAsync(callbackUrl, scopes ?? DefaultScopes, ct).ConfigureAwait(false);
        await secureStore.SetAsync(PendingKey, $"{rt.Token}\n{rt.Secret}", ct).ConfigureAwait(false);
        return rt.AuthorizeUrl;
    }

    /// <summary>Krok 2: callback z oauth_token i oauth_verifier → access token → zapis poświadczeń.</summary>
    public async Task CompleteAsync(string oauthToken, string oauthVerifier, CancellationToken ct = default)
    {
        var pending = await secureStore.GetAsync(PendingKey, ct).ConfigureAwait(false)
            ?? throw new AdapterAuthException("Brak oczekującej autoryzacji USOS, zacznij od nowa");
        var parts = pending.Split('\n');
        if (parts.Length != 2 || !string.Equals(parts[0], oauthToken, StringComparison.Ordinal))
            throw new AdapterAuthException("Token autoryzacji USOS nie pasuje do rozpoczętego logowania");

        var client = UsosAdapterFactory.CreateOAuthClient(university.Value, httpFactory.CreateClient("usos"), university.Value.SigningServerAppKey)
            ?? throw new AdapterUnavailableException("Brak konfiguracji OAuth USOS") { IsTransient = false };
        var at = await client.AccessTokenAsync(oauthToken, parts[1], oauthVerifier, ct).ConfigureAwait(false);
        await secureStore.RemoveAsync(PendingKey, ct).ConfigureAwait(false);
        await connections.ConnectUsosAsync(new UsosApiCredentials(at.Token, at.Secret, university.Value.UsosApiBaseUrl), ct).ConfigureAwait(false);
    }

    /// <summary>Obsługa deep linku <c>vusos://usos?oauth_token=…&amp;oauth_verifier=…</c>.</summary>
    public async Task<bool> TryHandleDeepLinkAsync(Uri uri, CancellationToken ct = default)
    {
        if (!string.Equals(uri.Host, "usos", StringComparison.OrdinalIgnoreCase)) return false;
        var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var token = q["oauth_token"];
        var verifier = q["oauth_verifier"];
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(verifier)) return false;
        await CompleteAsync(token, verifier, ct).ConfigureAwait(false);
        return true;
    }

    public Task ConnectWithCookiesAsync(string cookies, CancellationToken ct = default) =>
        connections.ConnectUsosAsync(new UsosWebCredentials(cookies.Trim(), university.Value.UsoswebBaseUrl), ct);
}

public sealed class PlatonConnectFlow(IConnectionManager connections, IOptions<UniversityOptions> university)
{
    /// <summary>URL do otwarcia w przeglądarce systemowej; Moodle po SSO przekierowuje na <c>{scheme}://token=…</c>.</summary>
    public string BuildLaunchUrl(string? scheme = null)
    {
        var passport = Random.Shared.NextDouble().ToString("0.000000000000", System.Globalization.CultureInfo.InvariantCulture);
        var baseUrl = university.Value.PlatonBaseUrl.TrimEnd('/');
        var s = Uri.EscapeDataString(scheme ?? university.Value.NativeScheme);
        return $"{baseUrl}/admin/tool/mobile/launch.php?service=moodle_mobile_app&passport={passport}&urlscheme={s}";
    }

    /// <summary>Przyjmuje deep link <c>vusos://token=BASE64</c>, <c>moodlemobile://token=…</c>, goły base64 albo sam wstoken.</summary>
    public async Task ConnectWithTokenAsync(string pasted, CancellationToken ct = default)
    {
        var parsed = MobileLaunchToken.Parse(pasted.Trim());
        await connections.ConnectPlatonAsync(
            new PlatonWsCredentials(parsed.WsToken, parsed.PrivateToken, university.Value.PlatonBaseUrl), ct).ConfigureAwait(false);
    }

    public async Task<bool> TryHandleDeepLinkAsync(Uri uri, CancellationToken ct = default)
    {
        var raw = uri.OriginalString;
        if (!raw.Contains("token=", StringComparison.OrdinalIgnoreCase)) return false;
        if (!MobileLaunchToken.TryParse(raw, out _)) return false;
        await ConnectWithTokenAsync(raw, ct).ConfigureAwait(false);
        return true;
    }

    public Task ConnectWithCookiesAsync(string cookies, string sesskey, CancellationToken ct = default) =>
        connections.ConnectPlatonAsync(new PlatonWebCredentials(cookies.Trim(), sesskey.Trim(), university.Value.PlatonBaseUrl), ct);
}

/// <summary>Rozdziela deep linki <c>vusos://…</c> między przepływy. Host woła to z obsługi URL schematu.</summary>
public sealed class DeepLinkDispatcher(UsosConnectFlow usos, PlatonConnectFlow platon)
{
    public event EventHandler<SourceSystemConnected>? Connected;

    /// <summary>Zwraca true, gdy link został rozpoznany i obsłużony. Linki <c>vusos://auth…</c> (MSAL) obsługuje host.</summary>
    public async Task<bool> DispatchAsync(Uri uri, CancellationToken ct = default)
    {
        if (await usos.TryHandleDeepLinkAsync(uri, ct).ConfigureAwait(false))
        {
            Connected?.Invoke(this, new SourceSystemConnected(Domain.SourceSystem.Usos));
            return true;
        }
        if (await platon.TryHandleDeepLinkAsync(uri, ct).ConfigureAwait(false))
        {
            Connected?.Invoke(this, new SourceSystemConnected(Domain.SourceSystem.Platon));
            return true;
        }
        return false;
    }
}

public sealed record SourceSystemConnected(Domain.SourceSystem System);
