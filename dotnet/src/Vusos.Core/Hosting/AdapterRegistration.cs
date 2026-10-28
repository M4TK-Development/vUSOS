using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vusos.Core.Adapters;
using Vusos.Core.Adapters.Platon;
using Vusos.Core.Adapters.Usos;
using Vusos.Core.Platform;
using Vusos.Core.Services;

namespace Vusos.Core.Hosting;

public static class AdapterRegistration
{
    /// <summary>
    /// Rejestruje fabryki adapterów USOS/Platon (api|web|mock) na podstawie <see cref="UniversityOptions"/>
    /// oraz przepływy łączenia. Wołać po <c>AddVusosCore</c>.
    /// </summary>
    public static IServiceCollection AddVusosAdapters(this IServiceCollection services)
    {
        services.AddSingleton<Func<UsosCredentials, IUsosAdapter>>(sp =>
        {
            var clock = sp.GetRequiredService<IClock>();
            var http = sp.GetRequiredService<IHttpClientFactory>();
            var uni = sp.GetRequiredService<IOptions<UniversityOptions>>().Value;
            return creds =>
            {
                var client = creds is UsosWebCredentials ? UsosWebAdapter.CreateHttpClient() : http.CreateClient("usos");
                var signer = creds is UsosApiCredentials
                    ? UsosAdapterFactory.CreateSigner(uni, http.CreateClient("usos"), uni.SigningServerAppKey)
                    : null;
                return UsosAdapterFactory.Create(creds, client, signer, clock);
            };
        });

        services.AddSingleton<Func<PlatonCredentials, IPlatonAdapter>>(sp =>
        {
            var clock = sp.GetRequiredService<IClock>();
            var http = sp.GetRequiredService<IHttpClientFactory>();
            var uni = sp.GetRequiredService<IOptions<UniversityOptions>>().Value;
            return creds =>
            {
                var client = creds is PlatonWebCredentials ? CreateNoRedirectClient() : http.CreateClient("platon");
                return PlatonAdapterFactory.Create(creds, client, clock, uni.PlatonBaseUrl);
            };
        });

        services.AddSingleton<UsosConnectFlow>();
        services.AddSingleton<PlatonConnectFlow>();
        services.AddSingleton<DeepLinkDispatcher>();
        services.AddSingleton<ConnectionIdentity>();
        return services;
    }

    private static HttpClient CreateNoRedirectClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("vUSOS/1.0");
        return client;
    }
}
