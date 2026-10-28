using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vusos.Core.Platform;

namespace Vusos.Core.Services;

/// <summary>
/// Po odzyskaniu sieci synchronizuje kolejkę testów i wznawia pobieranie materiałów. Bez IHostedService
/// (działa też w MAUI): host wywołuje <see cref="Start"/> / <see cref="Stop"/>.
/// </summary>
public sealed class BackgroundSyncCoordinator : IDisposable
{
    private readonly IConnectivity _connectivity;
    private readonly IQuizService _quizzes;
    private readonly IMaterialService _materials;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task _running = Task.CompletedTask;

    public BackgroundSyncCoordinator(IConnectivity connectivity, IQuizService quizzes, IMaterialService materials,
        ILogger<BackgroundSyncCoordinator>? logger = null)
    {
        _connectivity = connectivity;
        _quizzes = quizzes;
        _materials = materials;
        _logger = logger ?? NullLogger<BackgroundSyncCoordinator>.Instance;
    }

    public bool IsStarted
    {
        get { lock (_gate) return _cts is not null; }
    }

    /// <summary>Zadanie bieżącego przebiegu synchronizacji (do oczekiwania w testach).</summary>
    public Task Running
    {
        get { lock (_gate) return _running; }
    }

    /// <summary>Zaczyna nasłuch; gdy jesteśmy online, od razu uruchamia przebieg.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null)
                return;
            _cts = new CancellationTokenSource();
            _connectivity.OnlineChanged += OnOnlineChanged;
        }
        if (_connectivity.IsOnline)
            Trigger();
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            _connectivity.OnlineChanged -= OnOnlineChanged;
        }
        if (cts is null)
            return;
        cts.Cancel();
        cts.Dispose();
    }

    public void Dispose() => Stop();

    /// <summary>Jeden przebieg: synchronizacja testów, potem kolejka pobrań. Błędy są logowane, nie rzucane.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        try
        {
            var synced = await _quizzes.SyncPendingAsync(ct).ConfigureAwait(false);
            if (synced > 0)
                _logger.LogInformation("Zsynchronizowano {Count} akcji testów", synced);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Synchronizacja testów nie powiodła się");
        }

        try
        {
            await _materials.ProcessQueueAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Przetwarzanie kolejki materiałów nie powiodło się");
        }
    }

    private void OnOnlineChanged(object? sender, bool online)
    {
        if (online)
            Trigger();
    }

    private void Trigger()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_cts is null)
                return;
            token = _cts.Token;
            _running = RunSafeAsync(_running, token);
        }
    }

    private async Task RunSafeAsync(Task previous, CancellationToken ct)
    {
        try
        {
            await previous.ConfigureAwait(false); // przebiegi nie nakładają się
            await RunOnceAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Przebieg synchronizacji w tle zakończony błędem");
        }
    }
}
