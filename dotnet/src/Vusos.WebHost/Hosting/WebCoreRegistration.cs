using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vusos.Core.Hosting;
using Vusos.Core.Platform;
using Vusos.Core.Services;
using Vusos.UI.Services;

namespace Vusos.WebHost.Hosting;

public static class WebCoreRegistration
{
    /// <summary>
    /// Usługi hosta niezależne od trybu adapterów: sesja, otwieranie adresów/plików, tokeny pobierania, opcje.
    /// </summary>
    public static IServiceCollection AddVusosWebHostCommon(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<UniversityOptions>().Bind(configuration.GetSection("University"));
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection("Storage"));
        services.AddOptions<ServerOptions>().Bind(configuration.GetSection("Server"));
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<CircuitUser>();
        services.AddScoped<IAuthSession, HttpAuthSession>();
        services.AddSingleton<DownloadTokens>();
        services.AddScoped<IExternalOpener, JsExternalOpener>();
        return services;
    }

    /// <summary>
    /// Prawdziwy Vusos.Core (adaptery mock albo live): osobny kontener Core na użytkownika (<see cref="UserCoreFactory"/>)
    /// i scoped forwardery kontraktów Core do kontenera bieżącego użytkownika.
    /// </summary>
    public static IServiceCollection AddVusosPerUserCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<UserCoreFactory>();
        services.AddHostedService<UserCoreSweeper>();
        services.AddScoped<ICurrentUserCore, CurrentUserCore>();
        services.AddScoped<CircuitHandler, UserCoreCircuitHandler>();

        services.AddScoped<IConnectionManager>(Forward<IConnectionManager>);
        services.AddScoped<ISyncStatus>(Forward<ISyncStatus>);
        services.AddScoped<IMaterialService>(Forward<IMaterialService>);
        services.AddScoped<IQuizService>(Forward<IQuizService>);
        services.AddScoped<IDashboardService>(Forward<IDashboardService>);
        services.AddScoped<IConnectivity>(Forward<IConnectivity>);
        services.AddScoped<IAppPaths>(Forward<IAppPaths>);
        services.AddScoped<UsosConnectFlow>(Forward<UsosConnectFlow>);
        services.AddScoped<PlatonConnectFlow>(Forward<PlatonConnectFlow>);
        services.AddScoped<IConnectFlows, WebConnectFlows>();
        return services;
    }

    private static T Forward<T>(IServiceProvider sp) where T : notnull
        => sp.GetRequiredService<ICurrentUserCore>().Services.GetRequiredService<T>();
}
