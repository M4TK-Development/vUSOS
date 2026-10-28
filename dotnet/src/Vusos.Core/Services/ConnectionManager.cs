using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Offline;
using Vusos.Core.Platform;
using Vusos.Core.Services.Json;

namespace Vusos.Core.Services;

/// <summary>
/// Poświadczenia w <see cref="ISecureStore"/> (<c>conn:usos</c>, <c>conn:platon</c>), stan w <c>conninfo:*</c>.
/// Adaptery budują wstrzyknięte fabryki; wynik jest owijany w dekoratory cache i trzymany do zmiany połączenia.
/// </summary>
public sealed class ConnectionManager : IConnectionManager
{
    private readonly ISecureStore _secure;
    private readonly ICacheStore _cache;
    private readonly ISyncStatus _sync;
    private readonly IConnectivity _connectivity;
    private readonly IClock _clock;
    private readonly CacheOptions _cacheOptions;
    private readonly AdapterOptions _adapterOptions;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Slot<UsosCredentials, IUsosAdapter> _usos;
    private readonly Slot<PlatonCredentials, IPlatonAdapter> _platon;

    public event EventHandler<SourceSystem>? Changed;

    public ConnectionManager(
        ISecureStore secure,
        ICacheStore cache,
        ISyncStatus sync,
        IConnectivity connectivity,
        IClock clock,
        CacheOptions cacheOptions,
        AdapterOptions adapterOptions,
        Func<UsosCredentials, IUsosAdapter> usosFactory,
        Func<PlatonCredentials, IPlatonAdapter> platonFactory,
        ILogger<ConnectionManager>? logger = null)
    {
        _secure = secure;
        _cache = cache;
        _sync = sync;
        _connectivity = connectivity;
        _clock = clock;
        _cacheOptions = cacheOptions;
        _adapterOptions = adapterOptions;
        _logger = logger ?? NullLogger<ConnectionManager>.Instance;

        _usos = new Slot<UsosCredentials, IUsosAdapter>
        {
            System = SourceSystem.Usos,
            CredentialsKey = "conn:usos",
            InfoKey = "conninfo:usos",
            CreateMock = () => new UsosMockCredentials(),
            ModeOf = c => c switch
            {
                UsosApiCredentials => "api",
                UsosWebCredentials => "web",
                UsosMockCredentials => "mock",
                _ => c.GetType().Name,
            },
            Create = usosFactory,
            Validate = async (a, ct) => await a.GetProfileAsync(ct).ConfigureAwait(false),
            Decorate = (raw, scope) => new CachedUsosAdapter(raw, scope, _cache, _sync, _connectivity, _clock, _cacheOptions, _logger,
                ex => MarkExpired(SourceSystem.Usos)),
        };
        _platon = new Slot<PlatonCredentials, IPlatonAdapter>
        {
            System = SourceSystem.Platon,
            CredentialsKey = "conn:platon",
            InfoKey = "conninfo:platon",
            CreateMock = () => new PlatonMockCredentials(),
            ModeOf = c => c switch
            {
                PlatonWsCredentials => "ws",
                PlatonWebCredentials => "web",
                PlatonMockCredentials => "mock",
                _ => c.GetType().Name,
            },
            Create = platonFactory,
            Validate = async (a, ct) => await a.GetSiteInfoAsync(ct).ConfigureAwait(false),
            Decorate = (raw, scope) => new CachedPlatonAdapter(raw, scope, _cache, _sync, _connectivity, _clock, _cacheOptions, _logger,
                ex => MarkExpired(SourceSystem.Platon)),
        };
    }

    public async Task<IReadOnlyDictionary<SourceSystem, ConnectionInfo>> GetStatusAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoadAsync(_usos, ct).ConfigureAwait(false);
            await LoadAsync(_platon, ct).ConfigureAwait(false);
            return new Dictionary<SourceSystem, ConnectionInfo>
            {
                [SourceSystem.Usos] = _usos.Info ?? Disconnected(SourceSystem.Usos),
                [SourceSystem.Platon] = _platon.Info ?? Disconnected(SourceSystem.Platon),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task ConnectUsosAsync(UsosCredentials credentials, CancellationToken ct = default) => ConnectAsync(_usos, credentials, ct);

    public Task ConnectPlatonAsync(PlatonCredentials credentials, CancellationToken ct = default) => ConnectAsync(_platon, credentials, ct);

    public Task DisconnectAsync(SourceSystem system, CancellationToken ct = default) => system switch
    {
        SourceSystem.Usos => DisconnectAsync(_usos, ct),
        SourceSystem.Platon => DisconnectAsync(_platon, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(system)),
    };

    public Task<IUsosAdapter> GetUsosAsync(CancellationToken ct = default) => GetAdapterAsync(_usos, ct);

    public Task<IPlatonAdapter> GetPlatonAsync(CancellationToken ct = default) => GetAdapterAsync(_platon, ct);

    // ---------- implementacja ----------

    private async Task ConnectAsync<TCreds, TAdapter>(Slot<TCreds, TAdapter> slot, TCreds requested, CancellationToken ct)
        where TCreds : class where TAdapter : class
    {
        Exception? failure = null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoadAsync(slot, ct).ConfigureAwait(false);
            var credentials = _adapterOptions.IsMock ? slot.CreateMock() : requested;
            var mode = slot.ModeOf(credentials);
            var previousMode = slot.Credentials is null ? null : slot.ModeOf(slot.Credentials);

            // Poświadczenia zostają zapisane także gdy walidacja się nie uda (np. brak sieci) — status = Error.
            await _secure.SetAsync(slot.CredentialsKey, VusosJson.Serialize(credentials), ct).ConfigureAwait(false);
            slot.Credentials = credentials;
            slot.Adapter = null;

            if (previousMode is not null && previousMode != mode)
                await _cache.RemoveByPrefixAsync(CacheKey.SystemPrefix(_adapterOptions.UserScope, slot.System), ct).ConfigureAwait(false);

            try
            {
                var raw = slot.Create(credentials);
                await slot.Validate(raw, ct).ConfigureAwait(false);
                slot.Adapter = slot.Decorate(raw, _adapterOptions.UserScope);
                await SetInfoAsync(slot, ConnectionStatus.Connected, mode, null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Połączenie z {System} nie powiodło się", slot.System);
                failure = ex;
                await SetInfoAsync(slot, ConnectionStatus.Error, mode, ex.Message, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        RaiseChanged(slot.System);
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task DisconnectAsync<TCreds, TAdapter>(Slot<TCreds, TAdapter> slot, CancellationToken ct)
        where TCreds : class where TAdapter : class
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _secure.RemoveAsync(slot.CredentialsKey, ct).ConfigureAwait(false);
            await _cache.RemoveByPrefixAsync(CacheKey.SystemPrefix(_adapterOptions.UserScope, slot.System), ct).ConfigureAwait(false);
            slot.Credentials = null;
            slot.Adapter = null;
            slot.Loaded = true;
            await SetInfoAsync(slot, ConnectionStatus.Disconnected, "", null, ct).ConfigureAwait(false);
            _sync.SetPending(slot.System, 0);
        }
        finally
        {
            _gate.Release();
        }
        RaiseChanged(slot.System);
    }

    private async Task<TAdapter> GetAdapterAsync<TCreds, TAdapter>(Slot<TCreds, TAdapter> slot, CancellationToken ct)
        where TCreds : class where TAdapter : class
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoadAsync(slot, ct).ConfigureAwait(false);
            if (slot.Credentials is null)
                throw new NotConnectedException(slot.System);
            return slot.Adapter ??= slot.Decorate(slot.Create(slot.Credentials), _adapterOptions.UserScope);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Wczytuje stan z magazynu (raz). Wywoływane pod <see cref="_gate"/>.</summary>
    private async Task LoadAsync<TCreds, TAdapter>(Slot<TCreds, TAdapter> slot, CancellationToken ct)
        where TCreds : class where TAdapter : class
    {
        if (slot.Loaded)
            return;

        slot.Info = await ReadInfoAsync(slot.InfoKey, ct).ConfigureAwait(false);

        if (_adapterOptions.IsMock)
        {
            // Tryb mock: poświadczenia demonstracyjne od razu, chyba że użytkownik świadomie się rozłączył.
            if (slot.Info?.Status == ConnectionStatus.Disconnected)
            {
                slot.Credentials = null;
            }
            else
            {
                slot.Credentials = slot.CreateMock();
                if (slot.Info is null || slot.Info.Status != ConnectionStatus.Connected)
                    await SetInfoAsync(slot, ConnectionStatus.Connected, "mock", null, ct).ConfigureAwait(false);
            }
        }
        else
        {
            var json = await _secure.GetAsync(slot.CredentialsKey, ct).ConfigureAwait(false);
            slot.Credentials = null;
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    slot.Credentials = VusosJson.Deserialize<TCreds>(json);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    _logger.LogWarning(ex, "Nieczytelne poświadczenia {System} — pomijam", slot.System);
                }
            }
            if (slot.Credentials is null && slot.Info is { Status: not ConnectionStatus.Disconnected })
                slot.Info = null;
        }

        slot.Loaded = true;
    }

    private async Task<ConnectionInfo?> ReadInfoAsync(string key, CancellationToken ct)
    {
        var json = await _secure.GetAsync(key, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return VusosJson.Deserialize<ConnectionInfo>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private async Task SetInfoAsync<TCreds, TAdapter>(Slot<TCreds, TAdapter> slot, ConnectionStatus status, string mode, string? error, CancellationToken ct)
        where TCreds : class where TAdapter : class
    {
        slot.Info = new ConnectionInfo(slot.System, status, mode, _clock.Now, error);
        await _secure.SetAsync(slot.InfoKey, VusosJson.Serialize(slot.Info), ct).ConfigureAwait(false);
    }

    /// <summary>Wywoływane przez dekoratory, gdy system odrzucił sesję (<see cref="AdapterAuthException"/>).</summary>
    private void MarkExpired(SourceSystem system)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var changed = system == SourceSystem.Usos ? await ExpireAsync(_usos).ConfigureAwait(false) : await ExpireAsync(_platon).ConfigureAwait(false);
                if (changed)
                    RaiseChanged(system);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Nie udało się oznaczyć sesji {System} jako wygasłej", system);
            }
        });
    }

    private async Task<bool> ExpireAsync<TCreds, TAdapter>(Slot<TCreds, TAdapter> slot)
        where TCreds : class where TAdapter : class
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (slot.Info is not { Status: ConnectionStatus.Connected } info)
                return false;
            await SetInfoAsync(slot, ConnectionStatus.Expired, info.Mode, "Sesja wygasła — połącz ponownie", default).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RaiseChanged(SourceSystem system)
    {
        var handlers = Changed;
        if (handlers is null)
            return;
        foreach (var h in handlers.GetInvocationList().Cast<EventHandler<SourceSystem>>())
        {
            try { h(this, system); }
            catch (Exception ex) { _logger.LogWarning(ex, "Subskrybent Changed rzucił wyjątek"); }
        }
    }

    private static ConnectionInfo Disconnected(SourceSystem system) => new(system, ConnectionStatus.Disconnected, "", null, null);

    private sealed class Slot<TCreds, TAdapter> where TCreds : class where TAdapter : class
    {
        public required SourceSystem System { get; init; }
        public required string CredentialsKey { get; init; }
        public required string InfoKey { get; init; }
        public required Func<TCreds> CreateMock { get; init; }
        public required Func<TCreds, string> ModeOf { get; init; }
        public required Func<TCreds, TAdapter> Create { get; init; }
        public required Func<TAdapter, CancellationToken, Task> Validate { get; init; }
        public required Func<TAdapter, string, TAdapter> Decorate { get; init; }

        public bool Loaded { get; set; }
        public TCreds? Credentials { get; set; }
        public TAdapter? Adapter { get; set; }
        public ConnectionInfo? Info { get; set; }
    }
}
