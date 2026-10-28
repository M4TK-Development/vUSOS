using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vusos.Core.Adapters;
using Vusos.Core.Offline;
using Vusos.Core.Platform;
using Vusos.Core.Services;

namespace Vusos.Core.Tests.Services;

public sealed class DependencyInjectionTests : IDisposable
{
    private readonly TempAppPaths _paths = new();
    public void Dispose() => _paths.Dispose();

    private ServiceProvider Build(Dictionary<string, string?> config)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(config).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecureStore>(new InMemorySecureStore());
        services.AddSingleton<IConnectivity>(new FakeConnectivity());
        services.AddSingleton<IAppPaths>(_paths);
        services.AddVusosCore(cfg);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public async Task AddVusosCore_ResolvesEverything_AndBindsOptions()
    {
        using var sp = Build(new()
        {
            ["University:Name"] = "Testowa",
            ["Cache:FreshFor"] = "00:00:30",
            ["Cache:DownloadOnlyOnUnmetered"] = "true",
            ["Adapters:Mode"] = "mock",
        });

        Assert.Equal("Testowa", sp.GetRequiredService<UniversityOptions>().Name);
        Assert.Equal(TimeSpan.FromSeconds(30), sp.GetRequiredService<CacheOptions>().FreshFor);
        Assert.True(sp.GetRequiredService<CacheOptions>().DownloadOnlyOnUnmetered);
        Assert.True(sp.GetRequiredService<AdapterOptions>().IsMock);

        Assert.IsType<ConnectionManager>(sp.GetRequiredService<IConnectionManager>());
        Assert.IsType<MaterialService>(sp.GetRequiredService<IMaterialService>());
        Assert.IsType<QuizService>(sp.GetRequiredService<IQuizService>());
        Assert.IsType<DashboardService>(sp.GetRequiredService<IDashboardService>());
        Assert.IsType<SyncStatus>(sp.GetRequiredService<ISyncStatus>());
        Assert.IsType<SqliteCacheStore>(sp.GetRequiredService<ICacheStore>());
        Assert.NotNull(sp.GetRequiredService<BackgroundSyncCoordinator>());
        Assert.NotNull(sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("platon"));
        Assert.NotNull(sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("usos"));

        await sp.GetRequiredService<ICacheStore>().SetAsync("k", "v", DateTimeOffset.UtcNow);
        Assert.True(File.Exists(Path.Combine(_paths.DataDirectory, "vusos.db")));
    }

    [Fact]
    public async Task Placeholders_ThrowClearMessage_UntilHostOverridesFactories()
    {
        using var sp = Build(new() { ["Adapters:Mode"] = "live" });
        var cm = sp.GetRequiredService<IConnectionManager>();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => cm.ConnectUsosAsync(new UsosApiCredentials("a", "b", "https://u/")));
        Assert.Contains("IUsosAdapter", ex.Message);
    }

    [Fact]
    public async Task HostFactories_RegisteredBefore_AreRespected()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Adapters:Mode"] = "mock" }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISecureStore>(new InMemorySecureStore());
        services.AddSingleton<IConnectivity>(new FakeConnectivity());
        services.AddSingleton<IAppPaths>(_paths);
        var usos = new FakeUsosAdapter();
        var platon = new FakePlatonAdapter();
        services.AddSingleton<Func<UsosCredentials, IUsosAdapter>>(_ => usos);
        services.AddSingleton<Func<PlatonCredentials, IPlatonAdapter>>(_ => platon);
        services.AddVusosCore(cfg);
        await using var sp = services.BuildServiceProvider();

        var adapter = await sp.GetRequiredService<IConnectionManager>().GetUsosAsync();
        await adapter.GetProfileAsync();
        Assert.Equal(1, usos.CallCount("GetProfile"));
    }
}
