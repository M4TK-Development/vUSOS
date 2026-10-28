using Vusos.Core.Domain;

namespace Vusos.Core.Services;

/// <summary>Stan synchronizacji w pamięci (wątkowo bezpieczny).</summary>
public sealed class SyncStatus : ISyncStatus
{
    private readonly object _gate = new();
    private readonly Dictionary<SourceSystem, SyncState> _states = [];

    public event EventHandler<SyncState>? Changed;

    public SyncState Get(SourceSystem system)
    {
        lock (_gate)
            return _states.TryGetValue(system, out var s) ? s : Default(system);
    }

    /// <summary>
    /// <paramref name="at"/>: dla <see cref="DataOrigin.Live"/> czas pobrania; dla <see cref="DataOrigin.Cache"/> czas
    /// pobrania serwowanych danych (ich wiek). Live czyści błąd.
    /// </summary>
    public void Report(SourceSystem system, DataOrigin origin, DateTimeOffset at, string? errorMessage = null) =>
        Update(system, s => origin == DataOrigin.Live
            ? s with { LastOrigin = DataOrigin.Live, LastLiveAt = at, LastError = errorMessage }
            : s with { LastOrigin = DataOrigin.Cache, LastCacheAt = at, LastError = errorMessage });

    public void SetPending(SourceSystem system, int pending) =>
        Update(system, s => s.PendingActions == pending ? s : s with { PendingActions = pending });

    private void Update(SourceSystem system, Func<SyncState, SyncState> change)
    {
        SyncState previous, next;
        lock (_gate)
        {
            previous = _states.TryGetValue(system, out var s) ? s : Default(system);
            next = change(previous);
            _states[system] = next;
        }
        if (next == previous)
            return;
        var handlers = Changed;
        if (handlers is null)
            return;
        foreach (var h in handlers.GetInvocationList().Cast<EventHandler<SyncState>>())
        {
            try { h(this, next); }
            catch { /* subskrybent nie może zepsuć przepływu danych */ }
        }
    }

    private static SyncState Default(SourceSystem system) => new(system, DataOrigin.Live, null, null, 0, null);
}
