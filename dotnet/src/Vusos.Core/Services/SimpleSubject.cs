namespace Vusos.Core.Services;

/// <summary>Minimalny, wątkowo bezpieczny odpowiednik Subject&lt;T&gt; (bez System.Reactive).</summary>
internal sealed class SimpleSubject<T> : IObservable<T>
{
    private readonly object _gate = new();
    private IObserver<T>[] _observers = [];

    public IDisposable Subscribe(IObserver<T> observer)
    {
        lock (_gate)
            _observers = [.. _observers, observer];
        return new Subscription(this, observer);
    }

    public void OnNext(T value)
    {
        IObserver<T>[] snapshot;
        lock (_gate)
            snapshot = _observers;
        foreach (var o in snapshot)
        {
            try { o.OnNext(value); }
            catch { /* błąd obserwatora nie przerywa pobierania */ }
        }
    }

    private void Unsubscribe(IObserver<T> observer)
    {
        lock (_gate)
            _observers = _observers.Where(o => !ReferenceEquals(o, observer)).ToArray();
    }

    private sealed class Subscription(SimpleSubject<T> owner, IObserver<T> observer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Unsubscribe(observer);
        }
    }
}
