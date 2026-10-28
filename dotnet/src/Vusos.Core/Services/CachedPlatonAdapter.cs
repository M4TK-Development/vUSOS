using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Offline;
using Vusos.Core.Platform;

namespace Vusos.Core.Services;

/// <summary>
/// Dekorator <see cref="IPlatonAdapter"/>. Poza cache: <c>GetFileDownloadAsync</c> (tokeny) i
/// <c>GetQuizAttemptDataAsync</c> (stan podejścia trzyma <see cref="IQuizOfflineStore"/>) idą zawsze do adaptera.
/// Operacje zapisu quizów nie są cache'owane i unieważniają listy podejść (oraz recenzje/oceny po zakończeniu).
/// </summary>
public sealed class CachedPlatonAdapter : IPlatonAdapter
{
    private const SourceSystem Sys = SourceSystem.Platon;
    private static readonly string[] AttemptKeys = [CacheMethods.GetQuizAttempts];
    private static readonly string[] FinishKeys =
        [CacheMethods.GetQuizAttempts, CacheMethods.GetQuizAttemptReview, CacheMethods.GetQuizzes, CacheMethods.GetGrades];

    private readonly IPlatonAdapter _inner;
    private readonly SwrCache _swr;

    public CachedPlatonAdapter(
        IPlatonAdapter inner,
        string userScope,
        ICacheStore store,
        ISyncStatus sync,
        IConnectivity connectivity,
        IClock clock,
        CacheOptions options,
        ILogger? logger = null,
        Action<AdapterAuthException>? onAuthError = null)
    {
        _inner = inner;
        _swr = new SwrCache(userScope, Sys, store, sync, connectivity, clock, options, logger ?? NullLogger.Instance, onAuthError);
    }

    public Task<PlatonSiteInfo> GetSiteInfoAsync(CancellationToken ct = default) =>
        _swr.GetAsync("GetSiteInfo", [], c => _inner.GetSiteInfoAsync(c), ct);

    public Task<IReadOnlyList<LmsCourse>> GetCoursesAsync(CancellationToken ct = default) =>
        _swr.GetAsync("GetCourses", [], c => _inner.GetCoursesAsync(c), ct);

    public Task<IReadOnlyList<LmsSection>> GetCourseContentsAsync(string courseId, CancellationToken ct = default) =>
        _swr.GetAsync("GetCourseContents", [courseId], c => _inner.GetCourseContentsAsync(courseId, c), ct);

    public Task<IReadOnlyList<LmsAssignment>> GetAssignmentsAsync(CancellationToken ct = default) =>
        _swr.GetAsync("GetAssignments", [], c => _inner.GetAssignmentsAsync(c), ct);

    public Task<IReadOnlyList<LmsEvent>> GetUpcomingEventsAsync(int days = 14, CancellationToken ct = default) =>
        _swr.GetAsync("GetUpcomingEvents", [days], c => _inner.GetUpcomingEventsAsync(days, c), ct);

    public Task<IReadOnlyList<LmsGradeItem>> GetGradesAsync(string courseId, CancellationToken ct = default) =>
        _swr.GetAsync(CacheMethods.GetGrades, [courseId], c => _inner.GetGradesAsync(courseId, c), ct);

    public Task<IReadOnlyList<Announcement>> GetAnnouncementsAsync(int limit = 20, CancellationToken ct = default) =>
        _swr.GetAsync("GetAnnouncements", [limit], c => _inner.GetAnnouncementsAsync(limit, c), ct);

    public Task<AuthenticatedDownload> GetFileDownloadAsync(string url, CancellationToken ct = default) =>
        _inner.GetFileDownloadAsync(url, ct);

    public Task<IReadOnlyList<LmsQuiz>> GetQuizzesAsync(IReadOnlyList<string>? courseIds = null, CancellationToken ct = default) =>
        _swr.GetAsync(CacheMethods.GetQuizzes, [courseIds], c => _inner.GetQuizzesAsync(courseIds, c), ct);

    public Task<IReadOnlyList<QuizAttempt>> GetQuizAttemptsAsync(string quizId, CancellationToken ct = default) =>
        _swr.GetAsync(CacheMethods.GetQuizAttempts, [quizId], c => _inner.GetQuizAttemptsAsync(quizId, c), ct);

    public Task<QuizAttempt> StartQuizAttemptAsync(string quizId, IReadOnlyList<QuizAnswer>? preflight = null, bool forceNew = false, CancellationToken ct = default) =>
        _swr.WriteAsync(() => _inner.StartQuizAttemptAsync(quizId, preflight, forceNew, ct), AttemptKeys, ct);

    public Task<QuizAttemptData> GetQuizAttemptDataAsync(string attemptId, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default) =>
        _inner.GetQuizAttemptDataAsync(attemptId, preflight, ct);

    public Task SaveQuizAttemptAsync(string attemptId, IReadOnlyList<QuizAnswer> answers, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default) =>
        _swr.WriteAsync(() => _inner.SaveQuizAttemptAsync(attemptId, answers, preflight, ct), AttemptKeys, ct);

    public Task<QuizAttemptState> ProcessQuizAttemptAsync(string attemptId, IReadOnlyList<QuizAnswer> answers, bool finish, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default) =>
        _swr.WriteAsync(() => _inner.ProcessQuizAttemptAsync(attemptId, answers, finish, preflight, ct), finish ? FinishKeys : AttemptKeys, ct);

    public Task<QuizAttemptReview> GetQuizAttemptReviewAsync(string attemptId, CancellationToken ct = default) =>
        _swr.GetAsync(CacheMethods.GetQuizAttemptReview, [attemptId], c => _inner.GetQuizAttemptReviewAsync(attemptId, c), ct);
}
