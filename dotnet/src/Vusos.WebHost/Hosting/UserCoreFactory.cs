using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vusos.Core;
using Vusos.Core.Hosting;
using Vusos.Core.Platform;
using Vusos.Core.Domain;
using Vusos.Core.Services;

namespace Vusos.WebHost.Hosting;

/// <summary>
/// Własny „świat” Vusos.Core jednego użytkownika: osobny kontener DI (singletony Core: ConnectionManager, SyncStatus,
/// magazyny SQLite, BackgroundSyncCoordinator), osobny katalog danych i osobny ISecureStore.
/// </summary>
public sealed class UserCore(string userId, string userHash, ServiceProvider services, IAppPaths paths) : IAsyncDisposable
{
    public string UserId { get; } = userId;
    public string UserHash { get; } = userHash;
    public IServiceProvider Services { get; } = services;
    public IAppPaths Paths { get; } = paths;

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public async ValueTask DisposeAsync()
    {
        Services.GetService<BackgroundSyncCoordinator>()?.Stop();
        await services.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Tworzy leniwie, raz na identyfikator użytkownika, <see cref="UserCore"/> i zwalnia nieużywane (bez otwartych obwodów
/// Blazora i bez dostępu przez <c>UserIdleTimeout</c>). Core nie jest zmieniany: izolacja wynika z osobnych kontenerów DI.
/// </summary>
public sealed class UserCoreFactory : IAsyncDisposable, IDisposable
{
    private sealed class Entry(Lazy<UserCore> core, long now)
    {
        public Lazy<UserCore> Core { get; } = core;
        public long LastAccessTicks = now;
        public int Pins;
    }

    private readonly ConcurrentDictionary<string, Entry> _cores = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly IConfiguration _configuration;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly StorageOptions _storage;
    private readonly TimeProvider _time;

    public UserCoreFactory(IConfiguration configuration, IDataProtectionProvider dataProtection, ILoggerFactory loggers,
        IOptions<StorageOptions> storage, TimeProvider time)
    {
        _configuration = configuration;
        _dataProtection = dataProtection;
        _loggers = loggers;
        _logger = loggers.CreateLogger<UserCoreFactory>();
        _storage = storage.Value;
        _time = time;
    }

    /// <summary>Liczba aktualnie załadowanych użytkowników.</summary>
    public int LoadedCount => _cores.Values.Count(e => e.Core.IsValueCreated);

    public UserCore GetOrCreate(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        Entry entry;
        lock (_sync)
        {
            entry = _cores.GetOrAdd(userId, id => new Entry(new Lazy<UserCore>(() => Create(id)), _time.GetTimestamp()));
            Volatile.Write(ref entry.LastAccessTicks, _time.GetTimestamp());
        }
        try { return entry.Core.Value; }
        catch
        {
            _cores.TryRemove(new KeyValuePair<string, Entry>(userId, entry)); // Lazy zapamiętałby wyjątek
            throw;
        }
    }

    /// <summary>Oznacza użytkownika jako używanego przez otwarty obwód (blokuje zwolnienie).</summary>
    public void Pin(string userId)
    {
        GetOrCreate(userId);
        lock (_sync)
        {
            if (_cores.TryGetValue(userId, out var e))
            {
                e.Pins++;
                Volatile.Write(ref e.LastAccessTicks, _time.GetTimestamp());
            }
        }
    }

    public void Unpin(string userId)
    {
        lock (_sync)
        {
            if (_cores.TryGetValue(userId, out var e))
            {
                e.Pins = Math.Max(0, e.Pins - 1);
                Volatile.Write(ref e.LastAccessTicks, _time.GetTimestamp());
            }
        }
    }

    /// <summary>Zwalnia dane użytkowników bez otwartych obwodów, nieużywanych dłużej niż <paramref name="idle"/>. Zwraca liczbę zwolnionych.</summary>
    public async Task<int> EvictIdleAsync(TimeSpan idle)
    {
        var victims = new List<(string Id, Entry Entry)>();
        lock (_sync)
        {
            var now = _time.GetTimestamp();
            foreach (var (id, e) in _cores)
            {
                if (e.Pins > 0) continue;
                var age = _time.GetElapsedTime(Volatile.Read(ref e.LastAccessTicks), now);
                if (age < idle) continue;
                if (_cores.TryRemove(new KeyValuePair<string, Entry>(id, e))) victims.Add((id, e));
            }
        }

        foreach (var (id, e) in victims)
        {
            if (!e.Core.IsValueCreated) continue;
            try
            {
                await e.Core.Value.DisposeAsync().ConfigureAwait(false);
                _logger.LogInformation("Zwolniono dane użytkownika {Hash} (bezczynność)", e.Core.Value.UserHash);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Błąd przy zwalnianiu danych użytkownika {Id}", UserIdentity.Hash(id));
            }
        }
        return victims.Count;
    }

    /// <summary>
    /// Wylogowanie z kasowaniem danych (tryb anonymous): rozłącza USOS i Platon, usuwa tożsamość, zwalnia kontener Core
    /// użytkownika (nawet przy otwartym obwodzie) i kasuje jego katalog <c>users/{hash}</c> (baza, materiały, secure.json).
    /// </summary>
    public async Task WipeAsync(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var hash = UserIdentity.Hash(userId);
        Entry? entry;
        lock (_sync) _cores.TryRemove(userId, out entry);

        if (entry is { Core.IsValueCreated: true })
        {
            try
            {
                var core = entry.Core.Value;
                var connections = core.Get<IConnectionManager>();
                foreach (var system in new[] { SourceSystem.Usos, SourceSystem.Platon })
                    await connections.DisconnectAsync(system).ConfigureAwait(false);
                await core.Get<ConnectionIdentity>().ClearAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Rozłączanie przed usunięciem danych użytkownika {Hash} nie powiodło się", hash);
            }
            try { await entry.Core.Value.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Błąd przy zwalnianiu danych użytkownika {Hash}", hash); }
        }

        var dir = Path.Combine(_storage.RootFullPath, "users", hash);
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // zwalnia uchwyty pliku bazy (Windows) przed kasowaniem
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            _logger.LogInformation("Usunięto dane użytkownika {Hash}", hash);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nie udało się usunąć katalogu danych użytkownika {Hash}", hash);
        }
    }

    private UserCore Create(string userId)
    {
        var hash = UserIdentity.Hash(userId);
        var dir = Path.Combine(_storage.RootFullPath, "users", hash);
        Directory.CreateDirectory(dir);
        var paths = new UserAppPaths(dir);

        var services = new ServiceCollection();
        // Wspólny logging hosta (instancja nie jest zwalniana razem z kontenerem użytkownika).
        services.AddSingleton(_loggers);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        services.AddVusosCore(_configuration).AddVusosAdapters();
        services.Configure<AdapterOptions>(o => o.UserScope = hash);

        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<ISecureStore>(new DataProtectionSecureStore(
            _dataProtection, userId, Path.Combine(dir, "secure.json"), _loggers.CreateLogger<DataProtectionSecureStore>()));
        services.AddSingleton<IConnectivity, ServerConnectivity>();

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        provider.GetRequiredService<BackgroundSyncCoordinator>().Start();
        _logger.LogInformation("Utworzono dane użytkownika {Hash} w {Dir}", hash, dir);
        return new UserCore(userId, hash, provider, paths);
    }

    public async ValueTask DisposeAsync()
    {
        List<Entry> all;
        lock (_sync)
        {
            all = _cores.Values.ToList();
            _cores.Clear();
        }
        foreach (var e in all)
        {
            if (!e.Core.IsValueCreated) continue;
            try { await e.Core.Value.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Błąd przy zwalnianiu danych użytkownika"); }
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}

/// <summary>Co kilka minut zwalnia bezczynne dane użytkowników.</summary>
public sealed class UserCoreSweeper(UserCoreFactory factory, IOptions<ServerOptions> options, TimeProvider time, ILogger<UserCoreSweeper> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.SweepInterval;
        if (interval <= TimeSpan.Zero) return;
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await factory.EvictIdleAsync(options.Value.UserIdleTimeout).ConfigureAwait(false); }
                catch (Exception e) { logger.LogWarning(e, "Sprzątanie bezczynnych użytkowników nie powiodło się"); }
            }
        }
        catch (OperationCanceledException) { }
    }
}
