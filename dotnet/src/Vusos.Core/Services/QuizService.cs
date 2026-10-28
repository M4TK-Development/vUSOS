using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Offline;
using Vusos.Core.Platform;

namespace Vusos.Core.Services;

/// <summary>
/// Testy Moodle z obsługą offline. Podejście musi zostać rozpoczęte online; odpowiedzi trafiają zawsze do
/// <see cref="IQuizOfflineStore"/>, a gdy brak sieci (i quiz ma <c>AllowOfflineAttempts</c>) są kolejkowane
/// i wysyłane przez <see cref="SyncPendingAsync"/> (zapisy, potem zakończenie — w kolejności utworzenia).
/// </summary>
public sealed class QuizService : IQuizService
{
    /// <summary>Po tylu nieudanych próbach akcja zostaje w kolejce (widoczny błąd), ale nie jest już ponawiana.</summary>
    public const int MaxRetries = 5;

    private readonly IConnectionManager _connections;
    private readonly IQuizOfflineStore _store;
    private readonly IConnectivity _connectivity;
    private readonly ISyncStatus _sync;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _offlineAllowed = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _attemptByQuiz = new();

    public QuizService(
        IConnectionManager connections,
        IQuizOfflineStore store,
        IConnectivity connectivity,
        ISyncStatus sync,
        IClock clock,
        ILogger<QuizService>? logger = null)
    {
        _connections = connections;
        _store = store;
        _connectivity = connectivity;
        _sync = sync;
        _clock = clock;
        _logger = logger ?? NullLogger<QuizService>.Instance;
    }

    public async Task<IReadOnlyList<LmsQuiz>> GetQuizzesAsync(string? courseId = null, CancellationToken ct = default)
    {
        var platon = await _connections.GetPlatonAsync(ct).ConfigureAwait(false);
        return await platon.GetQuizzesAsync(courseId is null ? null : [courseId], ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<QuizAttempt>> GetAttemptsAsync(string quizId, CancellationToken ct = default)
    {
        var platon = await _connections.GetPlatonAsync(ct).ConfigureAwait(false);
        return await platon.GetQuizAttemptsAsync(quizId, ct).ConfigureAwait(false);
    }

    public async Task<QuizSession> OpenAttemptAsync(LmsQuiz quiz, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default)
    {
        _offlineAllowed[quiz.Id] = quiz.AllowOfflineAttempts;
        var platon = await _connections.GetPlatonAsync(ct).ConfigureAwait(false);

        // Lista podejść (z cache, gdy offline) — czy jest już rozpoczęte?
        IReadOnlyList<QuizAttempt> attempts;
        try
        {
            attempts = await platon.GetQuizAttemptsAsync(quiz.Id, ct).ConfigureAwait(false);
        }
        catch (AdapterUnavailableException) when (!_connectivity.IsOnline)
        {
            attempts = []; // brak listy w cache — spróbujemy jeszcze pamięci podręcznej usługi
        }

        var inProgress = attempts.Where(a => a.State == QuizAttemptState.InProgress).OrderByDescending(a => a.Attempt).FirstOrDefault();

        if (!_connectivity.IsOnline)
        {
            // Zapisy online unieważniają listę podejść w cache, więc pomagamy sobie też mapą quiz → podejście.
            var attemptId = inProgress?.Id ?? (_attemptByQuiz.TryGetValue(quiz.Id, out var known) ? known : null);
            var stored = attemptId is null ? null : await _store.GetAttemptDataAsync(attemptId, ct).ConfigureAwait(false);
            if (stored is null || stored.Attempt.State != QuizAttemptState.InProgress)
                throw new AdapterUnavailableException("Rozpoczęcie testu wymaga połączenia");
            if (!quiz.AllowOfflineAttempts)
                throw new AdapterUnavailableException("Ten test nie pozwala na pracę offline");
            return await BuildSessionAsync(stored, ct).ConfigureAwait(false);
        }

        QuizAttempt attempt = inProgress ?? await platon.StartQuizAttemptAsync(quiz.Id, preflight, false, ct).ConfigureAwait(false);

        QuizAttemptData data;
        try
        {
            data = await platon.GetQuizAttemptDataAsync(attempt.Id, preflight, ct).ConfigureAwait(false);
        }
        catch (AdapterUnavailableException) when (inProgress is not null)
        {
            // Sieć padła po rozpoczęciu — wznowienie z lokalnej kopii, jeśli jest.
            var stored = await _store.GetAttemptDataAsync(attempt.Id, ct).ConfigureAwait(false);
            if (stored is null)
                throw;
            return await BuildSessionAsync(stored, ct, forceOffline: true).ConfigureAwait(false);
        }

        if (preflight is { Count: > 0 } && data.PreflightData.Count == 0)
            data = data with { PreflightData = preflight };
        await _store.SaveAttemptDataAsync(data, ct).ConfigureAwait(false);
        _attemptByQuiz[quiz.Id] = data.Attempt.Id;

        if (inProgress is null)
        {
            // Start unieważnił listę podejść w cache; odświeżamy ją, by wznowienie offline znalazło to podejście.
            try
            {
                await platon.GetQuizAttemptsAsync(quiz.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AdapterUnavailableException or AdapterAuthException)
            {
                _logger.LogDebug(ex, "Odświeżenie listy podejść po starcie nie powiodło się");
            }
        }

        return await BuildSessionAsync(data, ct).ConfigureAwait(false);
    }

    public async Task<QuizSession> ResumeAttemptAsync(string attemptId, CancellationToken ct = default)
    {
        var data = await RequireDataAsync(attemptId, ct).ConfigureAwait(false);
        return await BuildSessionAsync(data, ct).ConfigureAwait(false);
    }

    public async Task<QuizSession> SaveAnswersAsync(string attemptId, IReadOnlyList<QuizAnswer> answers, CancellationToken ct = default)
    {
        var data = await RequireDataAsync(attemptId, ct).ConfigureAwait(false);
        await _store.SaveLocalAnswersAsync(attemptId, answers, ct).ConfigureAwait(false);
        var all = await _store.GetLocalAnswersAsync(attemptId, ct).ConfigureAwait(false);

        var sent = false;
        if (_connectivity.IsOnline)
        {
            try
            {
                var platon = await _connections.GetPlatonAsync(ct).ConfigureAwait(false);
                await platon.SaveQuizAttemptAsync(attemptId, all, data.PreflightData, ct).ConfigureAwait(false);
                sent = true;
            }
            catch (AdapterUnavailableException ex)
            {
                _logger.LogInformation(ex, "Zapis odpowiedzi {Attempt} nieudany — kolejkuję", attemptId);
            }
        }

        if (!sent)
        {
            await EnsureOfflineAllowedAsync(data.Attempt, ct).ConfigureAwait(false);
            // Nowy zapis zawiera komplet odpowiedzi, więc wcześniejsze zapisy (bez zakończenia) są zbędne.
            foreach (var old in (await _store.ListPendingAsync(ct).ConfigureAwait(false)).Where(p => p.AttemptId == attemptId && !p.Finish))
                await _store.CompleteAsync(old.Id, ct).ConfigureAwait(false);
            await EnqueueAsync(data.Attempt, all, finish: false, ct).ConfigureAwait(false);
        }

        return await BuildSessionAsync(data, ct).ConfigureAwait(false);
    }

    public async Task<QuizSession> FinishAsync(string attemptId, CancellationToken ct = default)
    {
        var data = await RequireDataAsync(attemptId, ct).ConfigureAwait(false);
        var answers = await _store.GetLocalAnswersAsync(attemptId, ct).ConfigureAwait(false);

        QuizAttemptState? finalState = null;
        if (_connectivity.IsOnline)
        {
            try
            {
                var platon = await _connections.GetPlatonAsync(ct).ConfigureAwait(false);
                finalState = await platon.ProcessQuizAttemptAsync(attemptId, answers, true, data.PreflightData, ct).ConfigureAwait(false);
            }
            catch (AdapterUnavailableException ex)
            {
                _logger.LogInformation(ex, "Zakończenie {Attempt} nieudane — kolejkuję", attemptId);
            }
        }

        if (finalState is null)
        {
            await EnsureOfflineAllowedAsync(data.Attempt, ct).ConfigureAwait(false);
            await EnqueueAsync(data.Attempt, answers, finish: true, ct).ConfigureAwait(false);
            finalState = QuizAttemptState.Finished; // lokalnie: użytkownik zakończył; serwer dowie się przy synchronizacji
        }
        else
        {
            // Wysłane online — zaległe zapisy tego podejścia są nieaktualne.
            foreach (var old in (await _store.ListPendingAsync(ct).ConfigureAwait(false)).Where(p => p.AttemptId == attemptId))
                await _store.CompleteAsync(old.Id, ct).ConfigureAwait(false);
            await UpdatePendingCountAsync(ct).ConfigureAwait(false);
        }

        _attemptByQuiz.TryRemove(data.Attempt.QuizId, out _);
        data = data with { Attempt = data.Attempt with { State = finalState.Value, TimeFinish = data.Attempt.TimeFinish ?? _clock.Now } };
        await _store.SaveAttemptDataAsync(data, ct).ConfigureAwait(false);
        return await BuildSessionAsync(data, ct).ConfigureAwait(false);
    }

    public async Task<int> SyncPendingAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_connectivity.IsOnline)
                return 0;
            var pending = (await _store.ListPendingAsync(ct).ConfigureAwait(false)).Where(IsLive).ToList();
            if (pending.Count == 0)
            {
                await UpdatePendingCountAsync(ct).ConfigureAwait(false);
                return 0;
            }

            IPlatonAdapter platon;
            try
            {
                platon = await _connections.GetPlatonAsync(ct).ConfigureAwait(false);
            }
            catch (NotConnectedException)
            {
                return 0;
            }

            var done = 0;
            var blocked = new HashSet<string>(); // podejścia, których któraś akcja padła — kolejne czekają (zachowanie kolejności)
            foreach (var action in pending)
            {
                if (blocked.Contains(action.AttemptId))
                    continue;
                try
                {
                    var stored = await _store.GetAttemptDataAsync(action.AttemptId, ct).ConfigureAwait(false);
                    var preflight = stored?.PreflightData;
                    if (action.Finish)
                    {
                        var state = await platon.ProcessQuizAttemptAsync(action.AttemptId, action.Answers, true, preflight, ct).ConfigureAwait(false);
                        if (stored is not null)
                            await _store.SaveAttemptDataAsync(stored with { Attempt = stored.Attempt with { State = state } }, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await platon.SaveQuizAttemptAsync(action.AttemptId, action.Answers, preflight, ct).ConfigureAwait(false);
                    }
                    await _store.CompleteAsync(action.Id, ct).ConfigureAwait(false);
                    done++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (AdapterAuthException ex)
                {
                    await _store.MarkFailedAsync(action.Id, ex.Message, CancellationToken.None).ConfigureAwait(false);
                    break; // sesja wygasła — dalsze próby bez sensu
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Synchronizacja akcji {Action} podejścia {Attempt} nieudana", action.Id, action.AttemptId);
                    await _store.MarkFailedAsync(action.Id, ex.Message, CancellationToken.None).ConfigureAwait(false);
                    blocked.Add(action.AttemptId);
                }
            }

            await UpdatePendingCountAsync(ct).ConfigureAwait(false);
            return done;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<QuizAttemptReview?> GetReviewAsync(string attemptId, CancellationToken ct = default)
    {
        var platon = await _connections.GetPlatonAsync(ct).ConfigureAwait(false);
        try
        {
            return await platon.GetQuizAttemptReviewAsync(attemptId, ct).ConfigureAwait(false);
        }
        catch (AdapterNotSupportedException)
        {
            return null;
        }
    }

    // ---------- pomocnicze ----------

    private static bool IsLive(PendingQuizAction a) => a.RetryCount < MaxRetries;

    private async Task<QuizAttemptData> RequireDataAsync(string attemptId, CancellationToken ct) =>
        await _store.GetAttemptDataAsync(attemptId, ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Brak lokalnej kopii podejścia {attemptId} — otwórz test z połączeniem.");

    private async Task<QuizSession> BuildSessionAsync(QuizAttemptData data, CancellationToken ct, bool forceOffline = false)
    {
        var local = await _store.GetLocalAnswersAsync(data.Attempt.Id, ct).ConfigureAwait(false);
        var pending = (await _store.ListPendingAsync(ct).ConfigureAwait(false)).Any(p => p.AttemptId == data.Attempt.Id && IsLive(p));
        return new QuizSession(data, local, forceOffline || !_connectivity.IsOnline, pending);
    }

    private async Task EnqueueAsync(QuizAttempt attempt, IReadOnlyList<QuizAnswer> answers, bool finish, CancellationToken ct)
    {
        await _store.EnqueueAsync(new PendingQuizAction(
            Id: Guid.NewGuid().ToString("N"),
            AttemptId: attempt.Id,
            QuizId: attempt.QuizId,
            Answers: answers,
            Finish: finish,
            CreatedAt: _clock.Now,
            RetryCount: 0,
            LastError: null), ct).ConfigureAwait(false);
        await UpdatePendingCountAsync(ct).ConfigureAwait(false);
    }

    private async Task UpdatePendingCountAsync(CancellationToken ct)
    {
        var count = (await _store.ListPendingAsync(ct).ConfigureAwait(false)).Count(IsLive);
        _sync.SetPending(SourceSystem.Platon, count);
    }

    /// <summary>Kolejkowanie dozwolone tylko dla quizów z <c>allowofflineattempts</c> (ustawienie wykładowcy).</summary>
    private async Task EnsureOfflineAllowedAsync(QuizAttempt attempt, CancellationToken ct)
    {
        if (!_offlineAllowed.TryGetValue(attempt.QuizId, out var allowed))
        {
            allowed = false;
            try
            {
                var platon = await _connections.GetPlatonAsync(ct).ConfigureAwait(false);
                var quizzes = await platon.GetQuizzesAsync(null, ct).ConfigureAwait(false);
                var quiz = quizzes.FirstOrDefault(q => q.Id == attempt.QuizId);
                if (quiz is not null)
                {
                    allowed = quiz.AllowOfflineAttempts;
                    _offlineAllowed[attempt.QuizId] = allowed;
                }
            }
            catch (Exception ex) when (ex is AdapterUnavailableException or AdapterAuthException or NotConnectedException or AdapterNotSupportedException)
            {
                _logger.LogDebug(ex, "Nie udało się ustalić allowofflineattempts dla quizu {Quiz}", attempt.QuizId);
            }
        }

        if (!allowed)
            throw new AdapterUnavailableException("Brak połączenia — ten test nie pozwala na pracę offline. Odpowiedzi zapisano tylko lokalnie.");
    }
}
