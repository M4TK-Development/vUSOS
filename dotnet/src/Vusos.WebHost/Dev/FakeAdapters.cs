using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.UI.Util;

namespace Vusos.WebHost.Dev;

public sealed class FakeUsosAdapter(FakeBackend backend) : IUsosAdapter
{
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private async Task<T> Run<T>(Func<T> data, CancellationToken ct)
    {
        await backend.GateAsync(SourceSystem.Usos, ct);
        return data();
    }

    public Task<StudentProfile> GetProfileAsync(CancellationToken ct = default) => Run(FakeData.Profile, ct);
    public Task<IReadOnlyList<Term>> GetTermsAsync(CancellationToken ct = default) => Run(FakeData.Terms, ct);

    public Task<IReadOnlyList<CourseUnit>> GetCoursesAsync(string? termId = null, CancellationToken ct = default)
        => Run<IReadOnlyList<CourseUnit>>(() => FakeData.Timetable(new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 11))
            .GroupBy(e => (e.CourseId, e.ClassType))
            .Select(g => new CourseUnit($"{g.Key.CourseId}-{g.Key.ClassType}", g.Key.CourseId ?? "", g.First().CourseName, g.Key.ClassType ?? "", "2026Z", g.First().GroupNumber, g.First().Lecturers, 4m, null))
            .ToList(), ct);

    public Task<IReadOnlyList<TimetableEntry>> GetTimetableAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
        => Run(() => FakeData.Timetable(from, to), ct);

    public Task<IReadOnlyList<Grade>> GetGradesAsync(IReadOnlyList<string>? termIds = null, CancellationToken ct = default)
        => Run(() => (IReadOnlyList<Grade>)FakeData.Grades(Now).Where(g => termIds is null || termIds.Contains(g.TermId)).ToList(), ct);

    public Task<IReadOnlyList<Payment>> GetPaymentsAsync(CancellationToken ct = default) => Run(FakeData.Payments, ct);

    public Task<IReadOnlyList<MessageThread>> ListMessagesAsync(int limit = 50, int offset = 0, CancellationToken ct = default)
        => Run(() => (IReadOnlyList<MessageThread>)FakeData.Threads(Now).Skip(offset).Take(limit).ToList(), ct);

    public Task<Message> GetMessageAsync(string id, CancellationToken ct = default)
        => Run(() => FakeData.MessageBody(FakeData.Threads(Now).FirstOrDefault(t => t.Id == id)
                                          ?? throw new AdapterUnavailableException($"Nie znaleziono wiadomości {id}.")), ct);

    public async Task<string> SendMessageAsync(IReadOnlyList<string> to, string subject, string body, CancellationToken ct = default)
    {
        await backend.GateAsync(SourceSystem.Usos, ct);
        return "sent-" + Guid.NewGuid().ToString("N")[..8];
    }

    public Task<IReadOnlyList<Contact>> GetDeanOfficeContactsAsync(CancellationToken ct = default) => Run(FakeData.Contacts, ct);
    public Task<IReadOnlyList<Announcement>> GetAnnouncementsAsync(int limit = 20, CancellationToken ct = default)
        => Run(() => (IReadOnlyList<Announcement>)FakeData.UsosAnnouncements(Now).Take(limit).ToList(), ct);
}

public sealed class FakePlatonAdapter(FakeBackend backend) : IPlatonAdapter
{
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private async Task<T> Run<T>(Func<T> data, CancellationToken ct)
    {
        await backend.GateAsync(SourceSystem.Platon, ct);
        return data();
    }

    public Task<PlatonSiteInfo> GetSiteInfoAsync(CancellationToken ct = default)
        => Run(() => new PlatonSiteInfo(501, "Anna Kowalska", "Platon – Vistula", "4.5"), ct);

    public Task<IReadOnlyList<LmsCourse>> GetCoursesAsync(CancellationToken ct = default) => Run(() => FakeData.PlatonCourses(Now), ct);
    public Task<IReadOnlyList<LmsSection>> GetCourseContentsAsync(string courseId, CancellationToken ct = default) => Run(() => FakeData.CourseContents(courseId, Now), ct);
    public Task<IReadOnlyList<LmsAssignment>> GetAssignmentsAsync(CancellationToken ct = default) => Run(() => FakeData.Assignments(Now), ct);
    public Task<IReadOnlyList<LmsEvent>> GetUpcomingEventsAsync(int days = 14, CancellationToken ct = default) => Run(() => FakeData.Events(Now, days), ct);
    public Task<IReadOnlyList<LmsGradeItem>> GetGradesAsync(string courseId, CancellationToken ct = default) => Run(() => FakeData.PlatonGrades(courseId), ct);
    public Task<IReadOnlyList<Announcement>> GetAnnouncementsAsync(int limit = 20, CancellationToken ct = default)
        => Run(() => (IReadOnlyList<Announcement>)FakeData.PlatonAnnouncements(Now).Take(limit).ToList(), ct);

    public Task<AuthenticatedDownload> GetFileDownloadAsync(string url, CancellationToken ct = default)
        => Run(() => new AuthenticatedDownload(new Uri(url), new Dictionary<string, string>()), ct);

    public Task<IReadOnlyList<LmsQuiz>> GetQuizzesAsync(IReadOnlyList<string>? courseIds = null, CancellationToken ct = default)
        => Run(() => (IReadOnlyList<LmsQuiz>)FakeData.Quizzes(Now).Where(q => courseIds is null || courseIds.Contains(q.CourseId)).ToList(), ct);

    // Testy obsługuje FakeQuizService; adapter zwraca minimum, żeby spełnić kontrakt.
    public Task<IReadOnlyList<QuizAttempt>> GetQuizAttemptsAsync(string quizId, CancellationToken ct = default)
        => Run<IReadOnlyList<QuizAttempt>>(() => [], ct);

    public Task<QuizAttempt> StartQuizAttemptAsync(string quizId, IReadOnlyList<QuizAnswer>? preflight = null, bool forceNew = false, CancellationToken ct = default)
        => Run(() => new QuizAttempt("att-" + quizId + "-adapter", quizId, 1, QuizAttemptState.InProgress, Now, null, null, 0), ct);

    public Task<QuizAttemptData> GetQuizAttemptDataAsync(string attemptId, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default)
        => Run(() => new QuizAttemptData(new QuizAttempt(attemptId, "9001", 1, QuizAttemptState.InProgress, Now, null, null, 0), FakeData.QuizQuestions("9001"), []), ct);

    public Task SaveQuizAttemptAsync(string attemptId, IReadOnlyList<QuizAnswer> answers, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default)
        => backend.GateAsync(SourceSystem.Platon, ct);

    public Task<QuizAttemptState> ProcessQuizAttemptAsync(string attemptId, IReadOnlyList<QuizAnswer> answers, bool finish, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default)
        => Run(() => finish ? QuizAttemptState.Finished : QuizAttemptState.InProgress, ct);

    public Task<QuizAttemptReview> GetQuizAttemptReviewAsync(string attemptId, CancellationToken ct = default)
        => Run(() => new QuizAttemptReview(new QuizAttempt(attemptId, "9001", 1, QuizAttemptState.Finished, Now.AddHours(-1), Now.AddMinutes(-30), 6.5, 2), 6.5, 10,
            FakeData.QuizQuestions("9001").Select(q => q with { Html = FakeData.ReviewHtml(q), Mark = 1, MaxMark = 2, Status = "Częściowo poprawnie" }).ToList()), ct);
}
