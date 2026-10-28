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
}
