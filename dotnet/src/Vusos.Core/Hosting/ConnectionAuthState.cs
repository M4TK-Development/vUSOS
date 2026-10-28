using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Platform;
using Vusos.Core.Services;

namespace Vusos.Core.Hosting;

/// <summary>Ostatnia znana tożsamość studenta, zapisana w <see cref="ISecureStore"/>, żeby aplikacja otwierała się „zalogowana” także offline.</summary>
public sealed record StoredIdentity(string DisplayName, string? Email, string? StudentNumber, SourceSystem Source, DateTimeOffset UpdatedAt);

/// <summary>
/// Tożsamość wyprowadzana z połączeń z systemami uczelni, bez rejestracji aplikacji w Entra ID. Student loguje się kontem M365
/// uczelni w przeglądarce systemowej przy łączeniu USOS i/lub Platona (oba same przekierowują na login.microsoftonline.com),
/// a imię, nazwisko i e-mail odczytujemy z tych systemów: USOS (<c>users/user</c>) ma pełny profil, Platon
/// (<c>core_webservice_get_site_info</c>) tylko imię i nazwisko. Platform-neutralne: używane przez MAUI i przez WebHost.
/// Celowo bez <see cref="IDisposable"/>: żyje tyle co <see cref="IConnectionManager"/> (oba singletony jednego kontenera), a WebHost
/// wystawia go przez scoped forwardery, których kontener nie może zwolnić.
/// </summary>
public sealed class ConnectionIdentity
{
    public const string StoreKey = "vusos.identity";

    private readonly IConnectionManager _connections;
    private readonly ISecureStore _store;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile StoredIdentity? _identity;
    private volatile bool _hasLiveConnection;
    private Task? _initialization;

    public ConnectionIdentity(IConnectionManager connections, ISecureStore store, IClock? clock = null, ILogger<ConnectionIdentity>? logger = null)
    {
        _connections = connections;
        _store = store;
        _clock = clock ?? new SystemClock();
        _logger = logger ?? NullLogger<ConnectionIdentity>.Instance;
        _connections.Changed += OnConnectionsChanged;
    }

    /// <summary>Zdarzenie po każdej zmianie połączeń (po odświeżeniu tożsamości) i po <see cref="ClearAsync"/>. Może przyjść z dowolnego wątku.</summary>
    public event Action? Changed;

    /// <summary>Jest zapisana tożsamość i przynajmniej jeden system jest połączony (Connected) albo ma wygasłą sesję (Expired).</summary>
    public bool IsSignedIn => _identity is not null && _hasLiveConnection;

    public string? DisplayName => IsSignedIn ? _identity!.DisplayName : null;
    public string? Email => IsSignedIn ? _identity!.Email : null;
    public string? StudentNumber => IsSignedIn ? _identity!.StudentNumber : null;
    public SourceSystem? Source => IsSignedIn ? _identity!.Source : null;

    /// <summary>
    /// Wczytuje zapisaną tożsamość i stan połączeń (lokalnie, bez sieci). Gdy systemy są połączone, a tożsamości jeszcze nie ma
    /// (pierwsze logowanie, tryb mock), czeka na pobranie profilu; w przeciwnym razie profil jest odświeżany w tle.
    /// Bezpieczne do wielokrotnego wołania.
    /// </summary>
    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        try
        {
            var stored = await ReadStoredAsync().ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _identity ??= stored;
                _hasLiveConnection = await AnyLiveConnectionAsync(default).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            if (_identity is null && _hasLiveConnection)
                await RefreshAsync().ConfigureAwait(false);
            else if (_hasLiveConnection)
                _ = Task.Run(async () =>
                {
                    try { await RefreshAsync().ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogInformation(ex, "Odświeżenie tożsamości w tle nie powiodło się"); }
                });
            RaiseChanged();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nie udało się wczytać tożsamości z połączeń");
        }
    }

    /// <summary>
    /// Ustala tożsamość z połączeń: USOS (gdy Connected) → profil; w przeciwnym razie Platon (gdy Connected) → site info.
    /// Błąd sieci nie wylogowuje: zostaje ostatnia znana tożsamość. Gdy oba systemy są rozłączone, tożsamość jest usuwana.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (await RefreshGatedAsync(ct).ConfigureAwait(false)) RaiseChanged();
    }

    private async Task<bool> RefreshGatedAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RefreshLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Usuwa zapisaną tożsamość (wylogowanie). Połączeń nie rozłącza — robi to wołający.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _identity = null;
            _hasLiveConnection = false;
            await _store.RemoveAsync(StoreKey, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        RaiseChanged();
    }

    // ---------- implementacja ----------

    private void OnConnectionsChanged(object? sender, SourceSystem system)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshGatedAsync(default).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Odświeżenie tożsamości po zmianie połączenia {System} nie powiodło się", system);
            }
            RaiseChanged();
        });
    }

    private async Task<bool> RefreshLockedAsync(CancellationToken ct)
    {
        var before = (_identity, _hasLiveConnection);
        IReadOnlyDictionary<SourceSystem, ConnectionInfo> status;
        try
        {
            status = await _connections.GetStatusAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Nie udało się odczytać stanu połączeń");
            return false;
        }

        var usos = StatusOf(status, SourceSystem.Usos);
        var platon = StatusOf(status, SourceSystem.Platon);
        _hasLiveConnection = IsLive(usos) || IsLive(platon);

        if (usos == ConnectionStatus.Disconnected && platon == ConnectionStatus.Disconnected)
        {
            if (_identity is not null)
            {
                _identity = null;
                await _store.RemoveAsync(StoreKey, ct).ConfigureAwait(false);
            }
        }
        else
        {
            var resolved = await ResolveAsync(usos, platon, ct).ConfigureAwait(false);
            if (resolved is not null && !SameIdentity(resolved, _identity))
            {
                _identity = resolved;
                await _store.SetAsync(StoreKey, JsonSerializer.Serialize(resolved), ct).ConfigureAwait(false);
            }
        }

        return !SameIdentity(before.Item1, _identity) || before.Item2 != _hasLiveConnection;
    }

    private async Task<StoredIdentity?> ResolveAsync(ConnectionStatus usos, ConnectionStatus platon, CancellationToken ct)
    {
        if (usos == ConnectionStatus.Connected)
        {
            try
            {
                var adapter = await _connections.GetUsosAsync(ct).ConfigureAwait(false);
                var p = await adapter.GetProfileAsync(ct).ConfigureAwait(false);
                var name = $"{p.FirstName} {p.LastName}".Trim();
                if (name.Length > 0 || !string.IsNullOrWhiteSpace(p.Email))
                {
                    return new StoredIdentity(
                        name.Length > 0 ? name : p.Email!.Trim(),
                        string.IsNullOrWhiteSpace(p.Email) ? null : p.Email.Trim(),
                        string.IsNullOrWhiteSpace(p.StudentNumber) ? null : p.StudentNumber.Trim(),
                        SourceSystem.Usos,
                        _clock.Now);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogInformation(ex, "Profil USOS niedostępny, próbuję Platona");
            }
        }

        if (platon == ConnectionStatus.Connected)
        {
            try
            {
                var adapter = await _connections.GetPlatonAsync(ct).ConfigureAwait(false);
                var info = await adapter.GetSiteInfoAsync(ct).ConfigureAwait(false);
                var name = info.FullName?.Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    // E-mail i numer albumu znamy tylko z USOS: jeśli to ta sama osoba, zachowujemy je z poprzedniej tożsamości.
                    var prev = _identity;
                    var same = prev is not null && string.Equals(prev.DisplayName, name, StringComparison.OrdinalIgnoreCase);
                    return new StoredIdentity(name, same ? prev!.Email : null, same ? prev!.StudentNumber : null, SourceSystem.Platon, _clock.Now);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogInformation(ex, "Informacje o Platonie niedostępne");
            }
        }

        return null; // zostaje ostatnia znana tożsamość
    }

    private async Task<bool> AnyLiveConnectionAsync(CancellationToken ct)
    {
        var status = await _connections.GetStatusAsync(ct).ConfigureAwait(false);
        return IsLive(StatusOf(status, SourceSystem.Usos)) || IsLive(StatusOf(status, SourceSystem.Platon));
    }

    private async Task<StoredIdentity?> ReadStoredAsync()
    {
        try
        {
            var json = await _store.GetAsync(StoreKey).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json)) return null;
            var stored = JsonSerializer.Deserialize<StoredIdentity>(json);
            return stored is { DisplayName.Length: > 0 } ? stored : null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Nieczytelna zapisana tożsamość — pomijam");
            return null;
        }
    }

    private static ConnectionStatus StatusOf(IReadOnlyDictionary<SourceSystem, ConnectionInfo> status, SourceSystem system)
        => status.TryGetValue(system, out var info) ? info.Status : ConnectionStatus.Disconnected;

    private static bool SameIdentity(StoredIdentity? a, StoredIdentity? b)
        => a is null || b is null ? a is null && b is null : a with { UpdatedAt = default } == b with { UpdatedAt = default };

    private static bool IsLive(ConnectionStatus s) => s is ConnectionStatus.Connected or ConnectionStatus.Expired;

    private void RaiseChanged()
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList().Cast<Action>())
        {
            try { h(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Subskrybent ConnectionIdentity.Changed rzucił wyjątek"); }
        }
    }
}
