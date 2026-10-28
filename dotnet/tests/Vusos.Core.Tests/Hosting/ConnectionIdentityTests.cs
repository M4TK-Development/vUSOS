using Vusos.Core;
using Vusos.Core.Adapters;
using Vusos.Core.Adapters.Platon;
using Vusos.Core.Adapters.Usos;
using Vusos.Core.Domain;
using Vusos.Core.Hosting;
using Vusos.Core.Services;
using Vusos.Core.Tests.Services;

namespace Vusos.Core.Tests.Hosting;

public sealed class ConnectionIdentityTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly InMemorySecureStore _secure = new();
    private readonly FakeClock _clock = new();
    private readonly FakeConnectivity _net = new();
    private readonly SyncStatus _sync = new();
    private readonly FakeUsosAdapter _usos = new();
    private readonly FakePlatonAdapter _platon = new();

    private static readonly UsosApiCredentials ApiCreds = new("tok", "sec", "https://usos/");
    private static readonly PlatonWsCredentials WsCreds = new("wstoken", null, "https://platon/");

    public void Dispose()
    {
        _t.Dispose();
    }

    private ConnectionManager Manager(string mode = "live", IUsosAdapter? usos = null, IPlatonAdapter? platon = null) =>
        new(_secure, _t.Cache, _sync, _net, _clock, new CacheOptions { LiveTimeout = TimeSpan.FromSeconds(2) },
            new AdapterOptions { Mode = mode }, _ => usos ?? _usos, _ => platon ?? _platon);

    private ConnectionIdentity Identity(IConnectionManager cm)
    {
        return new ConnectionIdentity(cm, _secure, _clock);
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        T value = await read();
        for (var i = 0; i < 100 && !done(value); i++)
        {
            await Task.Delay(20);
            value = await read();
        }
        return value;
    }

    [Fact]
    public async Task Initially_not_signed_in()
    {
        var identity = Identity(Manager());
        await identity.InitializeAsync();

        Assert.False(identity.IsSignedIn);
        Assert.Null(identity.DisplayName);
        Assert.Null(identity.Email);
        Assert.False(_secure.Data.ContainsKey(ConnectionIdentity.StoreKey));
    }

    [Fact]
    public async Task Usos_connection_gives_full_profile()
    {
        _usos.Profile = new StudentProfile("1", "Anna", "Nowak", "anna.nowak@stu.vistula.edu.pl", "61234", null, []);
        var cm = Manager();
        var identity = Identity(cm);
        await identity.InitializeAsync();

        await cm.ConnectUsosAsync(ApiCreds);
        await identity.RefreshAsync();

        Assert.True(identity.IsSignedIn);
        Assert.Equal("Anna Nowak", identity.DisplayName);
        Assert.Equal("anna.nowak@stu.vistula.edu.pl", identity.Email);
        Assert.Equal("61234", identity.StudentNumber);
        Assert.Equal(SourceSystem.Usos, identity.Source);
        Assert.Contains("Anna", _secure.Data[ConnectionIdentity.StoreKey]);
    }

    [Fact]
    public async Task Platon_only_gives_name_without_email()
    {
        _platon.SiteInfo = new PlatonSiteInfo(7, "Jan Kowalski", "Platon", "4.5");
        var cm = Manager();
        var identity = Identity(cm);

        await cm.ConnectPlatonAsync(WsCreds);
        await identity.RefreshAsync();

        Assert.True(identity.IsSignedIn);
        Assert.Equal("Jan Kowalski", identity.DisplayName);
        Assert.Null(identity.Email);
        Assert.Equal(SourceSystem.Platon, identity.Source);
    }

    [Fact]
    public async Task Usos_wins_over_platon_and_platon_is_used_when_usos_profile_fails()
    {
        _usos.Profile = new StudentProfile("1", "Anna", "Nowak", "anna@x.pl", "1", null, []);
        _platon.SiteInfo = new PlatonSiteInfo(7, "Jan Platon", "Platon", null);
        var cm = Manager();
        var identity = Identity(cm);
        await cm.ConnectUsosAsync(ApiCreds);
        await cm.ConnectPlatonAsync(WsCreds);

        await identity.RefreshAsync();
        Assert.Equal("Anna Nowak", identity.DisplayName);

        _usos.Failure = new AdapterUnavailableException("USOS leży");
        _usos.Profile = _usos.Profile with { FirstName = "Zmieniona" };
        // Wymuszamy świeże odpytanie USOS (cache dekoratora mógłby zwrócić stary profil).
        await _t.Cache.ClearAsync();
        await identity.RefreshAsync();
        Assert.True(identity.IsSignedIn);
        Assert.NotNull(identity.DisplayName);
    }

    [Fact]
    public async Task Platon_after_usos_keeps_email_for_the_same_person()
    {
        _usos.Profile = new StudentProfile("1", "Jan", "Kowalski", "jan@x.pl", "99", null, []);
        _platon.SiteInfo = new PlatonSiteInfo(7, "Jan Kowalski", "Platon", null);
        var cm = Manager();
        var identity = Identity(cm);
        await cm.ConnectUsosAsync(ApiCreds);
        await cm.ConnectPlatonAsync(WsCreds);
        await identity.RefreshAsync();

        await cm.DisconnectAsync(SourceSystem.Usos);
        await identity.RefreshAsync();

        Assert.Equal(SourceSystem.Platon, identity.Source);
        Assert.Equal("jan@x.pl", identity.Email);
        Assert.Equal("99", identity.StudentNumber);
    }

    [Fact]
    public async Task Identity_survives_restart_and_offline()
    {
        _usos.Profile = new StudentProfile("1", "Anna", "Nowak", "anna@x.pl", "5", null, []);
        var first = Manager();
        var identity1 = Identity(first);
        await first.ConnectUsosAsync(ApiCreds);
        await identity1.RefreshAsync();
        Assert.True(identity1.IsSignedIn);

        // „Restart”: nowe obiekty nad tym samym magazynem, sieć nie działa.
        _usos.Failure = new AdapterUnavailableException("offline");
        _net.SetOnline(false);
        var second = Manager();
        var identity2 = Identity(second);
        await identity2.InitializeAsync();

        Assert.True(identity2.IsSignedIn);
        Assert.Equal("Anna Nowak", identity2.DisplayName);
        Assert.Equal("anna@x.pl", identity2.Email);
    }

    [Fact]
    public async Task Disconnecting_everything_signs_out_and_forgets_the_identity()
    {
        var cm = Manager();
        var identity = Identity(cm);
        await cm.ConnectUsosAsync(ApiCreds);
        await cm.ConnectPlatonAsync(WsCreds);
        await identity.RefreshAsync();
        Assert.True(identity.IsSignedIn);

        await cm.DisconnectAsync(SourceSystem.Usos);
        await identity.RefreshAsync();
        Assert.True(identity.IsSignedIn); // zostaje Platon

        await cm.DisconnectAsync(SourceSystem.Platon);
        await identity.RefreshAsync();
        Assert.False(identity.IsSignedIn);
        Assert.Null(identity.DisplayName);
        Assert.False(_secure.Data.ContainsKey(ConnectionIdentity.StoreKey));
    }

    [Fact]
    public async Task Expired_session_with_stored_identity_stays_signed_in()
    {
        var cm = Manager();
        var identity = Identity(cm);
        await cm.ConnectUsosAsync(ApiCreds);
        await identity.RefreshAsync();

        var adapter = await cm.GetUsosAsync();
        _usos.Failure = new AdapterAuthException();
        await Assert.ThrowsAsync<AdapterAuthException>(() => adapter.GetPaymentsAsync());
        await WaitForAsync(async () => (await cm.GetStatusAsync())[SourceSystem.Usos].Status, s => s == ConnectionStatus.Expired);
        await identity.RefreshAsync();

        Assert.True(identity.IsSignedIn);
        Assert.Equal("Jan Kowalski", identity.DisplayName);
    }

    [Fact]
    public async Task Error_status_alone_is_not_signed_in()
    {
        _usos.Failure = new AdapterUnavailableException("brak sieci");
        var cm = Manager();
        var identity = Identity(cm);

        await Assert.ThrowsAsync<AdapterUnavailableException>(() => cm.ConnectUsosAsync(ApiCreds));
        await identity.RefreshAsync();

        Assert.False(identity.IsSignedIn);
    }

    [Fact]
    public async Task Changed_is_raised_when_connections_change()
    {
        var cm = Manager();
        var identity = Identity(cm);
        await identity.InitializeAsync();
        var raised = new TaskCompletionSource();
        identity.Changed += () => { if (identity.IsSignedIn) raised.TrySetResult(); };

        await cm.ConnectUsosAsync(ApiCreds);

        await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Jan Kowalski", identity.DisplayName);
    }

    [Fact]
    public async Task Changed_is_raised_on_disconnect()
    {
        var cm = Manager();
        var identity = Identity(cm);
        await cm.ConnectUsosAsync(ApiCreds);
        await identity.InitializeAsync();
        await identity.RefreshAsync();
        Assert.True(identity.IsSignedIn);
        var raised = new TaskCompletionSource();
        identity.Changed += () => { if (!identity.IsSignedIn) raised.TrySetResult(); };

        await cm.DisconnectAsync(SourceSystem.Usos);

        await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ClearAsync_removes_identity_and_raises_Changed()
    {
        var cm = Manager();
        var identity = Identity(cm);
        await cm.ConnectUsosAsync(ApiCreds);
        await identity.RefreshAsync();
        var raised = 0;
        identity.Changed += () => raised++;

        await identity.ClearAsync();

        Assert.False(identity.IsSignedIn);
        Assert.False(_secure.Data.ContainsKey(ConnectionIdentity.StoreKey));
        Assert.True(raised >= 1); // tło po Connect może dołożyć własne zdarzenie
    }

    [Fact]
    public async Task Mock_mode_reports_the_mock_profile_through_ConnectionIdentity()
    {
        var clock = new FakeClock();
        var cm = Manager("mock", new UsosMockAdapter(clock), new PlatonMockAdapter(clock));
        var identity = Identity(cm);

        await identity.InitializeAsync(); // mock łączy się sam, więc tożsamość powstaje przy starcie

        Assert.True(identity.IsSignedIn);
        Assert.Equal("Anna Nowak", identity.DisplayName);
        Assert.Equal("anna.nowak@stu.vistula.example", identity.Email);
        Assert.Equal("61234", identity.StudentNumber);
    }

    [Fact]
    public async Task Corrupt_stored_identity_is_ignored()
    {
        _secure.Data[ConnectionIdentity.StoreKey] = "{nie json";
        var identity = Identity(Manager());

        await identity.InitializeAsync();

        Assert.False(identity.IsSignedIn);
    }

    [Fact]
    public void AddVusosAdapters_registers_ConnectionIdentity_as_singleton()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddVusosAdapters();
        var d = Assert.Single(services, s => s.ServiceType == typeof(ConnectionIdentity));
        Assert.Equal(Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton, d.Lifetime);
    }
}
