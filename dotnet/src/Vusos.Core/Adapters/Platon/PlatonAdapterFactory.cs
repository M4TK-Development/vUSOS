using Vusos.Core.Platform;

namespace Vusos.Core.Adapters.Platon;

/// <summary>Tworzy adapter Platona dla poświadczeń: ws (REST + token), web (cookie + sesskey) albo mock.</summary>
public static class PlatonAdapterFactory
{
    public const string DefaultBaseUrl = "https://platon.vistula.edu.pl/";

    /// <param name="fallbackBaseUrl">Adres z konfiguracji, gdy poświadczenia nie mają własnego.</param>
    public static IPlatonAdapter Create(PlatonCredentials credentials, HttpClient http, IClock clock, string? fallbackBaseUrl = null)
    {
        string Pick(string? own) => !string.IsNullOrWhiteSpace(own) ? own : (!string.IsNullOrWhiteSpace(fallbackBaseUrl) ? fallbackBaseUrl : DefaultBaseUrl);

        switch (credentials)
        {
            case PlatonMockCredentials:
                return new PlatonMockAdapter(clock, Pick(null));
            case PlatonWsCredentials ws:
            {
                var baseUrl = Pick(ws.BaseUrl);
                return new PlatonFunctions(
                    new WsTransport(http, baseUrl, ws.WsToken),
                    new PlatonFunctionsOptions { BaseUrl = baseUrl, UserId = ws.UserId, Clock = clock });
            }
            case PlatonWebCredentials web:
            {
                var baseUrl = Pick(web.BaseUrl);
                return new PlatonFunctions(
                    new AjaxTransport(http, baseUrl, web.Cookies, web.SessKey),
                    new PlatonFunctionsOptions { BaseUrl = baseUrl, UserId = web.UserId, Clock = clock });
            }
            default:
                throw new NotSupportedException($"Nieobsługiwany typ poświadczeń Platona: {credentials.GetType().Name}");
        }
    }
}
