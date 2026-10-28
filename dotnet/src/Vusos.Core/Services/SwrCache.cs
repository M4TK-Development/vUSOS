using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Offline;
using Vusos.Core.Platform;
using Vusos.Core.Services.Json;

namespace Vusos.Core.Services;

/// <summary>
/// Wspólna logika stale-while-revalidate dla dekoratorów adapterów:
/// świeże (&lt; FreshFor) → cache; inaczej sieć z LiveTimeout; błąd dostępności / timeout / brak sieci → cache
/// (jeśli młodsze niż ServeStaleUpTo) z raportem <see cref="DataOrigin.Cache"/>; błąd autoryzacji zawsze w górę.
/// </summary>
internal sealed class SwrCache(
    string userScope,
    SourceSystem system,
    ICacheStore store,
    ISyncStatus sync,
    IConnectivity connectivity,
    IClock clock,
    CacheOptions options,
    ILogger logger,
    Action<AdapterAuthException>? onAuthError = null)
{
    private readonly ICacheInvalidation _invalidation = new CacheInvalidation(store);

    public async Task<T> GetAsync<T>(string method, object?[] args, Func<CancellationToken, Task<T>> live, CancellationToken ct)
    {
        var key = CacheKey.Create(userScope, system, method, args);

        CacheEntry? entry = null;
        T? cached = default;
        var hasCached = false;
        try
        {
            entry = await store.GetAsync(key, ct).ConfigureAwait(false);
            if (entry is not null)
            {
                try
                {
                    var value = VusosJson.Deserialize<T>(entry.Json);
                    if (value is not null)
                    {
                        cached = value;
                        hasCached = true;
                    }
                }
                catch (System.Text.Json.JsonException ex)
                {
                    logger.LogWarning(ex, "Uszkodzony wpis cache {Key}", key);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Odczyt cache nie powiódł się ({Key})", key);
        }

        var now = clock.Now;
        var age = hasCached && entry is not null ? now - entry.FetchedAt : TimeSpan.MaxValue;
        if (hasCached && age < options.FreshFor)
            return cached!;

        Exception error;
        if (!connectivity.IsOnline)
        {
            error = new AdapterUnavailableException("Brak połączenia z siecią");
        }
        else
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (options.LiveTimeout > TimeSpan.Zero && options.LiveTimeout != Timeout.InfiniteTimeSpan)
                cts.CancelAfter(options.LiveTimeout);
            try
            {
                var result = await live(cts.Token).ConfigureAwait(false);
                await TryStoreAsync(key, result, ct).ConfigureAwait(false);
                sync.Report(system, DataOrigin.Live, clock.Now);
                return result;
            }
            catch (AdapterAuthException ex)
            {
                onAuthError?.Invoke(ex);
                throw;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                error = new AdapterUnavailableException(
                    $"Przekroczono limit czasu odpowiedzi ({options.LiveTimeout.TotalSeconds:0.#} s)", ex);
            }
            catch (AdapterUnavailableException ex)
            {
                error = ex;
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException)
            {
                error = new AdapterUnavailableException(ex.Message, ex);
            }
        }

        if (hasCached && entry is not null && age < options.ServeStaleUpTo)
        {
            logger.LogInformation("{System}.{Method}: serwuję cache z {FetchedAt:o} ({Reason})", system, method, entry.FetchedAt, error.Message);
            sync.Report(system, DataOrigin.Cache, entry.FetchedAt, error.Message);
            return cached!;
        }

        ExceptionDispatchInfo.Capture(error).Throw();
        throw error; // nieosiągalne
    }

    /// <summary>Operacja zapisu: bez cache; po sukcesie unieważnia wskazane metody.</summary>
    public async Task<T> WriteAsync<T>(Func<Task<T>> write, IEnumerable<string> invalidate, CancellationToken ct)
    {
        var result = await write().ConfigureAwait(false);
        await InvalidateAsync(invalidate, ct).ConfigureAwait(false);
        return result;
    }

    public async Task WriteAsync(Func<Task> write, IEnumerable<string> invalidate, CancellationToken ct)
    {
        await write().ConfigureAwait(false);
        await InvalidateAsync(invalidate, ct).ConfigureAwait(false);
    }

    private async Task InvalidateAsync(IEnumerable<string> methods, CancellationToken ct)
    {
        try
        {
            await _invalidation.InvalidateMethodsAsync(userScope, system, methods, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Unieważnienie cache nie powiodło się");
        }
    }

    private async Task TryStoreAsync<T>(string key, T value, CancellationToken ct)
    {
        try
        {
            await store.SetAsync(key, VusosJson.Serialize(value), clock.Now, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Zapis do cache nie powiódł się ({Key})", key);
        }
    }
}
