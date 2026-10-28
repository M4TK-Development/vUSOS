using Vusos.Core.Platform;
using Vusos.Core.Services;

namespace Vusos.Core.Adapters.Usos;

/// <summary>Dispatches on the credentials type: api (OAuth 1.0a), web (cookies + HTML) or mock.</summary>
public static class UsosAdapterFactory
{
    /// <param name="creds">Stored USOS credentials.</param>
    /// <param name="http">Used by the web adapter (prefer <see cref="UsosWebAdapter.CreateHttpClient"/>: no redirects, no cookie jar).</param>
    /// <param name="signer">Required for api credentials (<see cref="LocalUsosSigner"/> or <see cref="RemoteUsosSigner"/>).</param>
    public static IUsosAdapter Create(UsosCredentials creds, HttpClient http, IUsosRequestSigner? signer, IClock clock) => creds switch
    {
        UsosApiCredentials api => new UsosApiAdapter(
            signer ?? throw new AdapterUnavailableException(
                "Brak podpisywania OAuth USOS: ustaw SigningServerUrl albo UsosConsumerKey/UsosConsumerSecret") { IsTransient = false },
            api, clock),
        UsosWebCredentials web => new UsosWebAdapter(http, web, clock),
        UsosMockCredentials => new UsosMockAdapter(clock),
        _ => throw new AdapterUnavailableException($"Nieobsługiwany tryb USOS: {creds.GetType().Name}") { IsTransient = false },
    };

    /// <summary>
    /// Signing strategy from configuration: <c>SigningServerUrl</c> set -> <see cref="RemoteUsosSigner"/> (Vusos.Server),
    /// otherwise <see cref="LocalUsosSigner"/> with the consumer key/secret; null when neither is configured.
    /// </summary>
    public static IUsosRequestSigner? CreateSigner(UniversityOptions options, HttpClient http, string? appKey = null)
    {
        if (!string.IsNullOrWhiteSpace(options.SigningServerUrl))
            return new RemoteUsosSigner(http, new Uri(options.SigningServerUrl), appKey);
        if (!string.IsNullOrWhiteSpace(options.UsosConsumerKey) && !string.IsNullOrWhiteSpace(options.UsosConsumerSecret))
            return new LocalUsosSigner(http, options.UsosConsumerKey, options.UsosConsumerSecret);
        return null;
    }

    /// <summary>OAuth (request/access token) client matching <see cref="CreateSigner"/>; null when signing is not configured.</summary>
    public static IUsosOAuthClient? CreateOAuthClient(UniversityOptions options, HttpClient http, string? appKey = null)
    {
        if (!string.IsNullOrWhiteSpace(options.SigningServerUrl))
            return new RemoteUsosOAuthClient(http, new Uri(options.SigningServerUrl), appKey);
        if (!string.IsNullOrWhiteSpace(options.UsosConsumerKey) && !string.IsNullOrWhiteSpace(options.UsosConsumerSecret))
            return new LocalUsosOAuthClient(http, options.UsosApiBaseUrl, options.UsosConsumerKey, options.UsosConsumerSecret);
        return null;
    }
}
