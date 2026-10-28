using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Vusos.UI.Services;

public static class UiServiceCollectionExtensions
{
    /// <summary>
    /// Rejestruje usługi samego UI. Host musi dodatkowo zarejestrować: IAuthSession, IExternalOpener, IConnectFlows
    /// oraz kontrakty Core (IConnectionManager, ISyncStatus, IMaterialService, IQuizService, IDashboardService, IConnectivity).
    /// </summary>
    public static IServiceCollection AddVusosUi(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ToastService>();
        services.AddScoped<VusosJs>();
        return services;
    }

    /// <summary>
    /// Domyślna sesja aplikacji natywnej: tożsamość z połączeń z USOS/Platonem (<see cref="ConnectionAuthSession"/>), bez
    /// rejestracji aplikacji w Entra ID. Wymaga <c>AddVusosAdapters()</c> (ConnectionIdentity) oraz ISecureStore, IConnectionManager,
    /// IMaterialService, ICacheStore i IAppPaths z Vusos.Core.
    /// </summary>
    public static IServiceCollection AddVusosConnectionAuth(this IServiceCollection services)
    {
        services.AddSingleton<ISignOutHandler, LocalDataSignOutHandler>();
        services.AddSingleton<ConnectionAuthSession>();
        services.AddSingleton<IAuthSession>(sp => sp.GetRequiredService<ConnectionAuthSession>());
        return services;
    }
}
