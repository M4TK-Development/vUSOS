using Vusos.Core.Domain;

namespace Vusos.Core.Adapters;

public sealed record PlatonSiteInfo(int UserId, string FullName, string SiteName, string? Release);

public sealed record AuthenticatedDownload(Uri Url, IReadOnlyDictionary<string, string> Headers);

/// <summary>Platon (Moodle): kursy, materiały, zadania, terminy, oceny, testy.</summary>
public interface IPlatonAdapter
{
    Task<PlatonSiteInfo> GetSiteInfoAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LmsCourse>> GetCoursesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LmsSection>> GetCourseContentsAsync(string courseId, CancellationToken ct = default);
    Task<IReadOnlyList<LmsAssignment>> GetAssignmentsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LmsEvent>> GetUpcomingEventsAsync(int days = 14, CancellationToken ct = default);
    Task<IReadOnlyList<LmsGradeItem>> GetGradesAsync(string courseId, CancellationToken ct = default);
    Task<IReadOnlyList<Announcement>> GetAnnouncementsAsync(int limit = 20, CancellationToken ct = default);

    /// <summary>Adres i nagłówki do pobrania pliku z uprawnieniami użytkownika (token / cookie).</summary>
    Task<AuthenticatedDownload> GetFileDownloadAsync(string url, CancellationToken ct = default);

    // --- Testy (mod_quiz) ---
    Task<IReadOnlyList<LmsQuiz>> GetQuizzesAsync(IReadOnlyList<string>? courseIds = null, CancellationToken ct = default);
    Task<IReadOnlyList<QuizAttempt>> GetQuizAttemptsAsync(string quizId, CancellationToken ct = default);
    /// <summary>Rozpoczyna podejście (wymaga połączenia). Dla quizów z hasłem przekaż je w <paramref name="preflight"/>.</summary>
    Task<QuizAttempt> StartQuizAttemptAsync(string quizId, IReadOnlyList<QuizAnswer>? preflight = null, bool forceNew = false, CancellationToken ct = default);
    /// <summary>Pobiera wszystkie strony podejścia (do pracy offline).</summary>
    Task<QuizAttemptData> GetQuizAttemptDataAsync(string attemptId, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default);
    /// <summary>Zapisuje odpowiedzi bez kończenia podejścia (synchronizacja postępu).</summary>
    Task SaveQuizAttemptAsync(string attemptId, IReadOnlyList<QuizAnswer> answers, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default);
    /// <summary>Przetwarza odpowiedzi; <paramref name="finish"/> = true kończy podejście.</summary>
    Task<QuizAttemptState> ProcessQuizAttemptAsync(string attemptId, IReadOnlyList<QuizAnswer> answers, bool finish, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default);
    Task<QuizAttemptReview> GetQuizAttemptReviewAsync(string attemptId, CancellationToken ct = default);
}
