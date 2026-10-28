using System.Collections.Concurrent;
using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Services;

namespace Vusos.WebHost.Dev;

/// <summary>Testy w pamięci: obsługuje start online, odpowiedzi lokalne, kolejkę offline i synchronizację.</summary>
public sealed class FakeQuizService(FakeBackend backend) : IQuizService
{
    private sealed class Stored(QuizAttempt attempt, string quizId)
    {
        public QuizAttempt Attempt { get; set; } = attempt;
        public string QuizId { get; } = quizId;
        public List<QuizAnswer> Answers { get; set; } = [];
        public bool Pending { get; set; }
    }

    private readonly ConcurrentDictionary<string, Stored> _attempts = new();
    private int _seeded;

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;
    private bool Online => backend.Connectivity.IsOnline;

    private void Seed()
    {
        if (Interlocked.Exchange(ref _seeded, 1) == 1) return;
        // jedno zakończone podejście w quizie próbnym, żeby był widoczny „Przejrzyj”
        _attempts["att-9003-1"] = new Stored(new QuizAttempt("att-9003-1", "9003", 1, QuizAttemptState.Finished, Now.AddDays(-5), Now.AddDays(-5).AddMinutes(18), 7, 2), "9003");
    }

    public async Task<IReadOnlyList<LmsQuiz>> GetQuizzesAsync(string? courseId = null, CancellationToken ct = default)
    {
        await backend.GateAsync(SourceSystem.Platon, ct);
        return FakeData.Quizzes(Now).Where(q => courseId is null || q.CourseId == courseId).ToList();
    }

    public Task<IReadOnlyList<QuizAttempt>> GetAttemptsAsync(string quizId, CancellationToken ct = default)
    {
        Seed();
        return Task.FromResult<IReadOnlyList<QuizAttempt>>(_attempts.Values.Where(a => a.QuizId == quizId)
            .Select(a => a.Attempt).OrderBy(a => a.Attempt).ToList());
    }

    public async Task<QuizSession> OpenAttemptAsync(LmsQuiz quiz, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default)
    {
        Seed();
        if (!Online) throw new AdapterUnavailableException("Rozpoczęcie testu wymaga połączenia z siecią.");
        await backend.GateAsync(SourceSystem.Platon, ct);
        if (quiz.Id == "9002" && preflight?.FirstOrDefault(a => a.Name == "quizpassword")?.Value != "test")
            throw new InvalidOperationException("Nieprawidłowe hasło do testu.");

        var existing = _attempts.Values.FirstOrDefault(a => a.QuizId == quiz.Id && a.Attempt.State == QuizAttemptState.InProgress);
        if (existing is null)
        {
            var number = _attempts.Values.Count(a => a.QuizId == quiz.Id) + 1;
            var id = $"att-{quiz.Id}-{number}";
            existing = new Stored(new QuizAttempt(id, quiz.Id, number, QuizAttemptState.InProgress, Now, null, null, 0), quiz.Id);
            _attempts[id] = existing;
        }
        return ToSession(existing);
    }

    public async Task<QuizSession> ResumeAttemptAsync(string attemptId, CancellationToken ct = default)
    {
        Seed();
        await Task.Delay(80, ct);
        return ToSession(Get(attemptId));
    }

    public async Task<QuizSession> SaveAnswersAsync(string attemptId, IReadOnlyList<QuizAnswer> answers, CancellationToken ct = default)
    {
        var a = Get(attemptId);
        a.Answers = [.. answers];
        await Task.Delay(60, ct);
        if (Online)
        {
            await backend.GateAsync(SourceSystem.Platon, ct);
        }
        else
        {
            a.Pending = true;
            backend.Sync.SetPending(SourceSystem.Platon, Pending());
        }
        return ToSession(a);
    }

    public async Task<QuizSession> FinishAsync(string attemptId, CancellationToken ct = default)
    {
        var a = Get(attemptId);
        await Task.Delay(120, ct);
        if (Online)
        {
            await backend.GateAsync(SourceSystem.Platon, ct);
            a.Attempt = a.Attempt with { State = QuizAttemptState.Finished, TimeFinish = Now, SumGrades = 6.5 };
            a.Pending = false;
        }
        else
        {
            a.Attempt = a.Attempt with { State = QuizAttemptState.Finished, TimeFinish = Now };
            a.Pending = true;
        }
        backend.Sync.SetPending(SourceSystem.Platon, Pending());
        return ToSession(a);
    }

    public Task<int> SyncPendingAsync(CancellationToken ct = default)
    {
        if (!Online) return Task.FromResult(0);
        var count = 0;
        foreach (var a in _attempts.Values.Where(a => a.Pending))
        {
            a.Pending = false;
            if (a.Attempt.State == QuizAttemptState.Finished) a.Attempt = a.Attempt with { SumGrades = 6.5 };
            count++;
        }
        backend.Sync.SetPending(SourceSystem.Platon, 0);
        return Task.FromResult(count);
    }

    public async Task<QuizAttemptReview?> GetReviewAsync(string attemptId, CancellationToken ct = default)
    {
        Seed();
        var a = Get(attemptId);
        if (a.Attempt.State != QuizAttemptState.Finished || a.Pending || !Online) return null;
        await backend.GateAsync(SourceSystem.Platon, ct);
        var questions = FakeData.QuizQuestions(a.QuizId).Select(q => q with { Html = FakeData.ReviewHtml(q), Mark = 1, MaxMark = 2, Status = "Częściowo poprawnie" }).ToList();
        return new QuizAttemptReview(a.Attempt, a.Attempt.SumGrades ?? 6.5, 10, questions);
    }

    private int Pending() => _attempts.Values.Count(a => a.Pending);

    private Stored Get(string attemptId)
        => _attempts.TryGetValue(attemptId, out var a) ? a : throw new AdapterUnavailableException($"Nie znaleziono podejścia {attemptId}.");

    private QuizSession ToSession(Stored a)
    {
        var data = new QuizAttemptData(a.Attempt, FakeData.QuizQuestions(a.QuizId), []);
        return new QuizSession(data, a.Answers.ToList(), !Online, a.Pending);
    }
}
