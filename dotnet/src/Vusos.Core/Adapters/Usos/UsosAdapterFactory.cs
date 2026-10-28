using Vusos.Core.Platform;

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
}
