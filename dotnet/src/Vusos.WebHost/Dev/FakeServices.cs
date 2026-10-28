using ConnectionInfo = Vusos.Core.Services.ConnectionInfo;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Platform;
using Vusos.Core.Services;
using Vusos.UI.Services;

namespace Vusos.WebHost.Dev;

/// <summary>
/// Rejestracja w pamięci fałszywych implementacji wszystkich kontraktów Core używanych przez UI
/// (tryb <c>Adapters:Mode=fake</c>). Singletony – wspólne dla wszystkich obwodów (tylko do developmentu).
/// </summary>
public static class FakeServiceRegistration
{
    public static IServiceCollection AddFakeVusosServices(this IServiceCollection services)
    {
        services.AddSingleton<FakeBackend>();
        services.AddSingleton<FakeSyncStatus>();
        services.AddSingleton<ISyncStatus>(sp => sp.GetRequiredService<FakeSyncStatus>());
        services.AddSingleton<FakeConnectivity>();
        services.AddSingleton<IConnectivity>(sp => sp.GetRequiredService<FakeConnectivity>());
        services.AddSingleton<ISecureStore, FakeSecureStore>();
        services.AddSingleton<IAppPaths, FakeAppPaths>();
        services.AddSingleton<FakeConnectionManager>();
        services.AddSingleton<IConnectionManager>(sp => sp.GetRequiredService<FakeConnectionManager>());
        services.AddSingleton<FakeMaterialService>();
        services.AddSingleton<IMaterialService>(sp => sp.GetRequiredService<FakeMaterialService>());
        services.AddSingleton<IQuizService, FakeQuizService>();
        services.AddSingleton<IDashboardService, FakeDashboardService>();
        services.AddScoped<IConnectFlows, FakeConnectFlows>();
        services.Configure<UniversityOptions>(_ => { });
        return services;
    }
}

/// <summary>Jak zachowuje się „system” w trybie fake – do ręcznego testowania stanów błędów (strona /dev).</summary>
public enum FakeSystemMode
{
    /// <summary>Dane na żywo (albo z cache, gdy brak sieci).</summary>
    Normal,
    /// <summary>Wymusza dane z cache (jak przeciążony system z zapisanymi danymi).</summary>
    ServeCache,
    /// <summary>System niedostępny i brak cache: AdapterUnavailableException.</summary>
    Unavailable,
    /// <summary>Sesja wygasła: AdapterAuthException.</summary>
    AuthExpired,
}

public sealed class FakeBackend(FakeSyncStatus sync, FakeConnectivity connectivity)
{
    private readonly ConcurrentDictionary<SourceSystem, FakeSystemMode> _modes = new();
    private readonly ConcurrentDictionary<SourceSystem, DateTimeOffset> _lastLive = new();

    public FakeSyncStatus Sync { get; } = sync;
    public FakeConnectivity Connectivity { get; } = connectivity;
    public TimeSpan Latency { get; set; } = TimeSpan.FromMilliseconds(120);

    public FakeSystemMode ModeOf(SourceSystem s) => _modes.GetValueOrDefault(s, FakeSystemMode.Normal);
    public void SetMode(SourceSystem s, FakeSystemMode mode) => _modes[s] = mode;

    /// <summary>Wywoływane przez każdą metodę fałszywych adapterów: opóźnienie, błędy i raport do ISyncStatus (jak dekoratory cache w Core).</summary>
    public async Task GateAsync(SourceSystem system, CancellationToken ct)
    {
        if (Latency > TimeSpan.Zero) await Task.Delay(Latency, ct);
        var now = DateTimeOffset.UtcNow;
        var mode = ModeOf(system);
        switch (mode)
        {
            case FakeSystemMode.AuthExpired:
                throw new AdapterAuthException($"Sesja w systemie {system} wygasła (symulacja).");
            case FakeSystemMode.Unavailable:
                Sync.Report(system, DataOrigin.Cache, _lastLive.GetValueOrDefault(system, now.AddMinutes(-35)), $"{system} niedostępny (symulacja).");
                throw new AdapterUnavailableException($"{system} nie odpowiada (symulacja).");
        }

        if (mode == FakeSystemMode.ServeCache || !Connectivity.IsOnline)
        {
            var at = _lastLive.GetValueOrDefault(system, now.AddMinutes(-35));
            Sync.Report(system, DataOrigin.Cache, at, Connectivity.IsOnline ? $"{system} niedostępny (symulacja)." : "Brak połączenia z siecią.");
            return;
        }

        _lastLive[system] = now;
        Sync.Report(system, DataOrigin.Live, now);
    }
}

public sealed class FakeSyncStatus : ISyncStatus
{
    private readonly ConcurrentDictionary<SourceSystem, SyncState> _states = new();

    public event EventHandler<SyncState>? Changed;

    public SyncState Get(SourceSystem system)
        => _states.GetValueOrDefault(system, new SyncState(system, DataOrigin.Live, null, null, 0, null));

    public void Report(SourceSystem system, DataOrigin origin, DateTimeOffset at, string? errorMessage = null)
    {
        var next = _states.AddOrUpdate(system,
            _ => Make(new SyncState(system, origin, null, null, 0, null)),
            (_, cur) => Make(cur));
        Changed?.Invoke(this, next);

        SyncState Make(SyncState cur) => origin == DataOrigin.Live
            ? cur with { LastOrigin = DataOrigin.Live, LastLiveAt = at, LastError = errorMessage }
            : cur with { LastOrigin = DataOrigin.Cache, LastCacheAt = at, LastError = errorMessage };
    }

    public void SetPending(SourceSystem system, int pending)
    {
        var next = _states.AddOrUpdate(system,
            _ => new SyncState(system, DataOrigin.Live, null, null, pending, null),
            (_, cur) => cur with { PendingActions = pending });
        Changed?.Invoke(this, next);
    }
}

public sealed class FakeConnectivity : IConnectivity
{
    private volatile bool _online = true;
    public bool IsOnline => _online;
    public bool IsMetered => false;
    public event EventHandler<bool>? OnlineChanged;

    public void SetOnline(bool online)
    {
        if (_online == online) return;
        _online = online;
        OnlineChanged?.Invoke(this, online);
    }
}

public sealed class FakeSecureStore : ISecureStore
{
    private readonly ConcurrentDictionary<string, string> _items = new();
    public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(_items.TryGetValue(key, out var v) ? v : null);
    public Task SetAsync(string key, string value, CancellationToken ct = default) { _items[key] = value; return Task.CompletedTask; }
    public Task RemoveAsync(string key, CancellationToken ct = default) { _items.TryRemove(key, out _); return Task.CompletedTask; }
}

public sealed class FakeAppPaths : IAppPaths
{
    public FakeAppPaths()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "vusos-fake");
        MaterialsDirectory = Path.Combine(DataDirectory, "materials");
        Directory.CreateDirectory(MaterialsDirectory);
    }

    public string DataDirectory { get; }
    public string MaterialsDirectory { get; }
}

public sealed class FakeConnectionManager : IConnectionManager
{
    private readonly object _lock = new();
    private readonly Dictionary<SourceSystem, ConnectionInfo> _status;
    private readonly FakeBackend _backend;

    public FakeConnectionManager(FakeBackend backend)
    {
        _backend = backend;
        var now = DateTimeOffset.UtcNow;
        _status = new()
        {
            [SourceSystem.Usos] = new ConnectionInfo(SourceSystem.Usos, ConnectionStatus.Connected, "mock", now, null),
            [SourceSystem.Platon] = new ConnectionInfo(SourceSystem.Platon, ConnectionStatus.Connected, "mock", now, null),
        };
    }

    public event EventHandler<SourceSystem>? Changed;

    public Task<IReadOnlyDictionary<SourceSystem, ConnectionInfo>> GetStatusAsync(CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult<IReadOnlyDictionary<SourceSystem, ConnectionInfo>>(new Dictionary<SourceSystem, ConnectionInfo>(_status));
    }

    public Task ConnectUsosAsync(UsosCredentials credentials, CancellationToken ct = default)
    {
        Set(SourceSystem.Usos, ConnectionStatus.Connected, credentials is UsosApiCredentials ? "api" : credentials is UsosWebCredentials ? "web" : "mock", null);
        return Task.CompletedTask;
    }

    public Task ConnectPlatonAsync(PlatonCredentials credentials, CancellationToken ct = default)
    {
        Set(SourceSystem.Platon, ConnectionStatus.Connected, credentials is PlatonWsCredentials ? "ws" : credentials is PlatonWebCredentials ? "web" : "mock", null);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(SourceSystem system, CancellationToken ct = default)
    {
        Set(system, ConnectionStatus.Disconnected, "mock", null);
        return Task.CompletedTask;
    }

    /// <summary>Do strony /dev: ręczna zmiana stanu (np. Expired), żeby zobaczyć banery.</summary>
    public void Set(SourceSystem system, ConnectionStatus status, string mode, string? error)
    {
        lock (_lock) _status[system] = new ConnectionInfo(system, status, mode, DateTimeOffset.UtcNow, error);
        Changed?.Invoke(this, system);
    }

    private ConnectionStatus StatusOf(SourceSystem s)
    {
        lock (_lock) return _status[s].Status;
    }

    public Task<IUsosAdapter> GetUsosAsync(CancellationToken ct = default)
        => StatusOf(SourceSystem.Usos) == ConnectionStatus.Disconnected
            ? throw new NotConnectedException(SourceSystem.Usos)
            : Task.FromResult<IUsosAdapter>(new FakeUsosAdapter(_backend));

    public Task<IPlatonAdapter> GetPlatonAsync(CancellationToken ct = default)
        => StatusOf(SourceSystem.Platon) == ConnectionStatus.Disconnected
            ? throw new NotConnectedException(SourceSystem.Platon)
            : Task.FromResult<IPlatonAdapter>(new FakePlatonAdapter(_backend));
}

/// <summary>Fałszywe przepływy podłączania (w prawdziwym hoście: przeglądarka systemowa + deep link).</summary>
public sealed class FakeConnectFlows(IConnectionManager connections) : IConnectFlows
{
    public async Task StartUsosAsync()
    {
        await Task.Delay(500);
        await connections.ConnectUsosAsync(new UsosMockCredentials());
    }

    public async Task StartPlatonAsync()
    {
        await Task.Delay(500);
        await connections.ConnectPlatonAsync(new PlatonMockCredentials());
    }

    public async Task SubmitPlatonTokenAsync(string pasted)
    {
        if (!pasted.Contains("token=", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("To nie wygląda na adres z tokenem (powinien zawierać „token=”).");
        await connections.ConnectPlatonAsync(new PlatonMockCredentials());
    }

    public async Task SubmitUsosCookiesAsync(string cookies)
    {
        if (string.IsNullOrWhiteSpace(cookies)) throw new ArgumentException("Wklej zawartość nagłówka Cookie.");
        await connections.ConnectUsosAsync(new UsosWebCredentials(cookies, "https://usosweb.vistula.edu.pl/"));
    }

    public async Task SubmitPlatonCookiesAsync(string cookies, string sesskey)
    {
        if (string.IsNullOrWhiteSpace(cookies) || string.IsNullOrWhiteSpace(sesskey))
            throw new ArgumentException("Podaj cookies oraz sesskey.");
        await connections.ConnectPlatonAsync(new PlatonWebCredentials(cookies, sesskey, "https://platon.vistula.edu.pl/"));
    }
}
