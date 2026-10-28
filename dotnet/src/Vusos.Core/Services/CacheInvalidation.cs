using Vusos.Core.Domain;
using Vusos.Core.Offline;

namespace Vusos.Core.Services;

/// <summary>Unieważnianie wpisów cache po operacjach zapisu.</summary>
public interface ICacheInvalidation
{
    /// <summary>Usuwa wpisy podanych metod (dla wszystkich argumentów).</summary>
    Task InvalidateMethodsAsync(string userScope, SourceSystem system, IEnumerable<string> methods, CancellationToken ct = default);

    /// <summary>Usuwa wszystkie wpisy systemu.</summary>
    Task InvalidateSystemAsync(string userScope, SourceSystem system, CancellationToken ct = default);
}

public sealed class CacheInvalidation(ICacheStore store) : ICacheInvalidation
{
    public async Task InvalidateMethodsAsync(string userScope, SourceSystem system, IEnumerable<string> methods, CancellationToken ct = default)
    {
        foreach (var method in methods)
            await store.RemoveByPrefixAsync(CacheKey.MethodPrefix(userScope, system, method), ct).ConfigureAwait(false);
    }

    public Task InvalidateSystemAsync(string userScope, SourceSystem system, CancellationToken ct = default) =>
        store.RemoveByPrefixAsync(CacheKey.SystemPrefix(userScope, system), ct);
}
