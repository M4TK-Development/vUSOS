using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Services;
using Vusos.Core.Services.Json;

namespace Vusos.Core.Tests.Services;

public sealed class ConnectionManagerTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly InMemorySecureStore _secure = new();
    private readonly FakeClock _clock = new();
    private readonly FakeConnectivity _net = new();
    private readonly SyncStatus _sync = new();
    private readonly FakeUsosAdapter _usos = new();
    private readonly FakePlatonAdapter _platon = new();
    private int _usosFactoryCalls;
    private readonly List<UsosCredentials> _usosCredsSeen = [];

    public void Dispose() => _t.Dispose();

    private ConnectionManager Create(string mode = "live") =>
        new(_secure, _t.Cache, _sync, _net, _clock, new CacheOptions { LiveTimeout = TimeSpan.FromSeconds(2) },
            new AdapterOptions { Mode = mode },
            c => { _usosFactoryCalls++; _usosCredsSeen.Add(c); return _usos; },
            _ => _platon);

    private static readonly UsosApiCredentials ApiCreds = new("tok", "sec", "https://usos/");

    [Fact]
    public async Task InitialStatus_IsDisconnected_AndGetAdapterThrowsNotConnected()
    {
        var cm = Create();
        var status = await cm.GetStatusAsync();
        Assert.Equal(ConnectionStatus.Disconnected, status[SourceSystem.Usos].Status);
        Assert.Equal(ConnectionStatus.Disconnected, status[SourceSystem.Platon].Status);
        var ex = await Assert.ThrowsAsync<NotConnectedException>(() => cm.GetUsosAsync());
        Assert.Equal(SourceSystem.Usos, ex.System);
        await Assert.ThrowsAsync<NotConnectedException>(() => cm.GetPlatonAsync());
    }

    [Fact]
    public async Task Connect_ValidatesStoresCredentialsAndRaisesChanged()
    {
        var cm = Create();
        var changed = new List<SourceSystem>();
        cm.Changed += (_, s) => changed.Add(s);

        await cm.ConnectUsosAsync(ApiCreds);

        Assert.Contains(SourceSystem.Usos, changed);
        Assert.Equal(1, _usos.CallCount("GetProfile"));
        var info = (await cm.GetStatusAsync())[SourceSystem.Usos];
        Assert.Equal(ConnectionStatus.Connected, info.Status);
        Assert.Equal("api", info.Mode);
        Assert.Null(info.Error);
        Assert.Equal(ApiCreds, VusosJson.Deserialize<UsosCredentials>(_secure.Data["conn:usos"]));
        Assert.True(_secure.Data.ContainsKey("conninfo:usos"));
        Assert.False(_secure.Data.ContainsKey("conn:platon"));
    }

    [Fact]
    public async Task GetAdapter_ReturnsCachedDecorator_AndReusesInstance()
    {
        var cm = Create();
        await cm.ConnectUsosAsync(ApiCreds);
        var a1 = await cm.GetUsosAsync();
        var a2 = await cm.GetUsosAsync();
        Assert.Same(a1, a2);
        Assert.IsType<CachedUsosAdapter>(a1);
        Assert.Equal(1, _usosFactoryCalls); // zwalidowany adapter został użyty ponownie

        // dekorator faktycznie cache'uje
        _usos.Payments = [];
        await a1.GetPaymentsAsync();
        await a1.GetPaymentsAsync();
        Assert.Equal(1, _usos.CallCount("GetPayments"));
    }

    [Fact]
    public async Task NewManager_LoadsStoredCredentials()
    {
        await Create().ConnectPlatonAsync(new PlatonWsCredentials("t", null, "https://p/", 3));

        var fresh = Create();
        var status = await fresh.GetStatusAsync();
        Assert.Equal(ConnectionStatus.Connected, status[SourceSystem.Platon].Status);
        Assert.IsType<CachedPlatonAdapter>(await fresh.GetPlatonAsync());
        Assert.Equal(1, _platon.CallCount("GetSiteInfo")); // tylko walidacja przy łączeniu
    }

    [Fact]
    public async Task Connect_Failure_SetsErrorStatus_KeepsCredentials_AndRethrows()
    {
        var cm = Create();
        _platon.Failure = new AdapterAuthException("Zły token");
        var changed = 0;
        cm.Changed += (_, _) => changed++;

        await Assert.ThrowsAsync<AdapterAuthException>(() => cm.ConnectPlatonAsync(new PlatonWsCredentials("bad", null, "https://p/")));

        var info = (await cm.GetStatusAsync())[SourceSystem.Platon];
        Assert.Equal(ConnectionStatus.Error, info.Status);
        Assert.Equal("Zły token", info.Error);
        Assert.True(_secure.Data.ContainsKey("conn:platon"));
        Assert.Equal(1, changed);

        // ponowna próba z poprawnym tokenem
        _platon.Failure = null;
        await cm.ConnectPlatonAsync(new PlatonWsCredentials("good", null, "https://p/"));
        Assert.Equal(ConnectionStatus.Connected, (await cm.GetStatusAsync())[SourceSystem.Platon].Status);
    }

    [Fact]
    public async Task Disconnect_RemovesCredentials_AndOnlyThatSystemsCache()
    {
        var cm = Create();
        await cm.ConnectUsosAsync(ApiCreds);
        await cm.ConnectPlatonAsync(new PlatonWsCredentials("t", null, "https://p/"));
        await (await cm.GetUsosAsync()).GetPaymentsAsync();
        await (await cm.GetPlatonAsync()).GetCoursesAsync();
        var changed = new List<SourceSystem>();
        cm.Changed += (_, s) => changed.Add(s);

        await cm.DisconnectAsync(SourceSystem.Usos);

        Assert.Equal([SourceSystem.Usos], changed);
        Assert.False(_secure.Data.ContainsKey("conn:usos"));
        Assert.True(_secure.Data.ContainsKey("conn:platon"));
        var status = await cm.GetStatusAsync();
        Assert.Equal(ConnectionStatus.Disconnected, status[SourceSystem.Usos].Status);
        Assert.Equal(ConnectionStatus.Connected, status[SourceSystem.Platon].Status);
        await Assert.ThrowsAsync<NotConnectedException>(() => cm.GetUsosAsync());

        var scope = new AdapterOptions().UserScope;
        Assert.Null(await _t.Cache.GetAsync(CacheKey.Create(scope, SourceSystem.Usos, "GetPayments")));
        Assert.NotNull(await _t.Cache.GetAsync(CacheKey.Create(scope, SourceSystem.Platon, "GetCourses")));

        // po restarcie nadal rozłączony
        Assert.Equal(ConnectionStatus.Disconnected, (await Create().GetStatusAsync())[SourceSystem.Usos].Status);
    }

    [Fact]
    public async Task SwitchingCredentialKind_PurgesOldCache()
    {
        var cm = Create();
        await cm.ConnectUsosAsync(new UsosMockCredentials());
        await (await cm.GetUsosAsync()).GetPaymentsAsync();
        var key = CacheKey.Create(new AdapterOptions().UserScope, SourceSystem.Usos, "GetPayments");
        Assert.NotNull(await _t.Cache.GetAsync(key));

        await cm.ConnectUsosAsync(ApiCreds);
        Assert.Null(await _t.Cache.GetAsync(key));
    }

    [Fact]
    public async Task AuthFailureInDecorator_MarksConnectionExpired()
    {
        var cm = Create();
        await cm.ConnectUsosAsync(ApiCreds);
        var adapter = await cm.GetUsosAsync();
        _usos.Failure = new AdapterAuthException();
        await Assert.ThrowsAsync<AdapterAuthException>(() => adapter.GetPaymentsAsync());

        for (var i = 0; i < 50; i++)
        {
            if ((await cm.GetStatusAsync())[SourceSystem.Usos].Status == ConnectionStatus.Expired) break;
            await Task.Delay(20);
        }
        Assert.Equal(ConnectionStatus.Expired, (await cm.GetStatusAsync())[SourceSystem.Usos].Status);
    }

    [Fact]
    public async Task MockMode_AutoConnects_WithMockCredentials()
    {
        var cm = Create("mock");

        var status = await cm.GetStatusAsync();
        Assert.Equal(ConnectionStatus.Connected, status[SourceSystem.Usos].Status);
        Assert.Equal(ConnectionStatus.Connected, status[SourceSystem.Platon].Status);
        Assert.Equal("mock", status[SourceSystem.Usos].Mode);

        var adapter = await cm.GetUsosAsync();
        Assert.IsType<CachedUsosAdapter>(adapter);
        Assert.IsType<UsosMockCredentials>(Assert.Single(_usosCredsSeen));

        // jawne rozłączenie zostaje respektowane, a Connect w trybie mock ignoruje przekazane poświadczenia
        await cm.DisconnectAsync(SourceSystem.Usos);
        Assert.Equal(ConnectionStatus.Disconnected, (await cm.GetStatusAsync())[SourceSystem.Usos].Status);
        await cm.ConnectUsosAsync(ApiCreds);
        Assert.IsType<UsosMockCredentials>(_usosCredsSeen[^1]);
        Assert.Equal(ConnectionStatus.Connected, (await cm.GetStatusAsync())[SourceSystem.Usos].Status);
    }

    [Fact]
    public async Task MissingFactory_SurfacesAsErrorStatus()
    {
        var cm = new ConnectionManager(_secure, _t.Cache, _sync, _net, _clock, new CacheOptions(), new AdapterOptions(),
            _ => throw new NotSupportedException("brak fabryki USOS"), _ => _platon);
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => cm.ConnectUsosAsync(ApiCreds));
        Assert.Contains("brak fabryki", ex.Message);
        var info = (await cm.GetStatusAsync())[SourceSystem.Usos];
        Assert.Equal(ConnectionStatus.Error, info.Status);
    }
}
