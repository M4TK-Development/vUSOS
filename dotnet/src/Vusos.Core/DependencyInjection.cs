using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vusos.Core.Adapters;
using Vusos.Core.Offline;
using Vusos.Core.Platform;
using Vusos.Core.Services;

namespace Vusos.Core;

public static class DependencyInjection
{
    /// <summary>
    /// Rejestruje Vusos.Core. Host musi zarejestrować <see cref="ISecureStore"/>, <see cref="IConnectivity"/>
    /// i <see cref="IAppPaths"/> oraz (krok integracji) fabryki adapterów
    /// <c>Func&lt;UsosCredentials, IUsosAdapter&gt;</c> i <c>Func&lt;PlatonCredentials, IPlatonAdapter&gt;</c>.
    /// </summary>
    public static IServiceCollection AddVusosCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<UniversityOptions>().Bind(configuration.GetSection("University"));
        services.AddOptions<CacheOptions>().Bind(configuration.GetSection("Cache"));
        services.AddOptions<AdapterOptions>().Bind(configuration.GetSection("Adapters"));
        return services.AddVusosCoreServices();
    }

    /// <summary>Wariant programowy (bez IConfiguration), zgodny z opisem w ARCHITECTURE.md.</summary>
    public static IServiceCollection AddVusosCore(
        this IServiceCollection services,
        Action<UniversityOptions>? configureUniversity = null,
        Action<CacheOptions>? configureCache = null,
        Action<AdapterOptions>? configureAdapters = null)
    {
        services.AddOptions<UniversityOptions>();
        services.AddOptions<CacheOptions>();
        services.AddOptions<AdapterOptions>();
        if (configureUniversity is not null) services.Configure(configureUniversity);
        if (configureCache is not null) services.Configure(configureCache);
        if (configureAdapters is not null) services.Configure(configureAdapters);
        return services.AddVusosCoreServices();
    }

    private static IServiceCollection AddVusosCoreServices(this IServiceCollection services)
    {
        // Opcje jako zwykłe singletony (wartość z IOptions) — klasy Core biorą POCO bez zależności od Options.
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<UniversityOptions>>().Value);
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<CacheOptions>>().Value);
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<AdapterOptions>>().Value);

        services.TryAddSingleton<IClock, SystemClock>();

        services.AddHttpClient(MaterialService.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromMinutes(30); // pliki bywają duże; limit pojedynczego odczytu daje serwer/transport
            c.DefaultRequestHeaders.UserAgent.ParseAdd("vUSOS/1.0");
        });
        services.AddHttpClient("usos", c => c.DefaultRequestHeaders.UserAgent.ParseAdd("vUSOS/1.0"));

        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<ICacheStore, SqliteCacheStore>();
        services.AddSingleton<IMaterialStore, SqliteMaterialStore>();
        services.AddSingleton<IQuizOfflineStore, SqliteQuizOfflineStore>();
        services.AddSingleton<ICacheInvalidation, CacheInvalidation>();
        services.AddSingleton<ISyncStatus, SyncStatus>();

        // Placeholdery — integracja podmienia je fabrykami budującymi adaptery live/mock.
        services.TryAddSingleton<Func<UsosCredentials, IUsosAdapter>>(_ => c => throw new NotSupportedException(
            $"Nie zarejestrowano fabryki Func<UsosCredentials, IUsosAdapter> (poświadczenia: {c.GetType().Name}). " +
            "Zarejestruj ją w hoście po AddVusosCore (services.AddSingleton<Func<UsosCredentials, IUsosAdapter>>(...))."));
        services.TryAddSingleton<Func<PlatonCredentials, IPlatonAdapter>>(_ => c => throw new NotSupportedException(
            $"Nie zarejestrowano fabryki Func<PlatonCredentials, IPlatonAdapter> (poświadczenia: {c.GetType().Name}). " +
            "Zarejestruj ją w hoście po AddVusosCore (services.AddSingleton<Func<PlatonCredentials, IPlatonAdapter>>(...))."));

        services.AddSingleton<IConnectionManager, ConnectionManager>();
        services.AddSingleton<IMaterialService, MaterialService>();
        services.AddSingleton<IQuizService, QuizService>();
        services.AddSingleton<IDashboardService, DashboardService>();
        services.AddSingleton<BackgroundSyncCoordinator>();
        return services;
    }
}
