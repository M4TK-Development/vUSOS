using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Offline;
using Vusos.Core.Platform;
using Vusos.Core.Services;

namespace Vusos.Core.Tests.Services;

public sealed class FakeClock : IClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 28, 10, 0, 0, TimeSpan.FromHours(1));
    public void Advance(TimeSpan by) => Now += by;
}

public sealed class FakeConnectivity : IConnectivity
{
    private bool _online = true;
    public bool IsOnline => _online;
    public bool IsMetered { get; set; }
    public event EventHandler<bool>? OnlineChanged;

    public void SetOnline(bool online)
    {
        _online = online;
        OnlineChanged?.Invoke(this, online);
    }
}

public sealed class InMemorySecureStore : ISecureStore
{
    public Dictionary<string, string> Data { get; } = [];
    public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(Data.GetValueOrDefault(key));
    public Task SetAsync(string key, string value, CancellationToken ct = default) { Data[key] = value; return Task.CompletedTask; }
    public Task RemoveAsync(string key, CancellationToken ct = default) { Data.Remove(key); return Task.CompletedTask; }
}

public sealed class TempAppPaths : IAppPaths, IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "vusos-tests-" + Guid.NewGuid().ToString("N"));
    public string DataDirectory => Path.Combine(Root, "data");
    public string MaterialsDirectory => Path.Combine(Root, "materials");

    public TempAppPaths() => Directory.CreateDirectory(Root);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Baza SQLite w katalogu tymczasowym + wszystkie magazyny.</summary>
public sealed class TestDb : IDisposable
{
    public TempAppPaths Paths { get; } = new();
    public SqliteDatabase Db { get; }
    public SqliteCacheStore Cache { get; }
    public SqliteMaterialStore Materials { get; }
    public SqliteQuizOfflineStore Quizzes { get; }

    public TestDb()
    {
        Db = new SqliteDatabase(Paths);
        Cache = new SqliteCacheStore(Db);
        Materials = new SqliteMaterialStore(Db);
        Quizzes = new SqliteQuizOfflineStore(Db);
    }

    public void Dispose()
    {
        Db.Dispose();
        Paths.Dispose();
    }
}

public abstract class FakeAdapterBase
{
    public List<string> Calls { get; } = [];
    public Exception? Failure { get; set; }
    public bool Hang { get; set; }

    protected async Task EnterAsync(string name, CancellationToken ct)
    {
        lock (Calls) Calls.Add(name);
        if (Hang)
            await Task.Delay(Timeout.Infinite, ct);
        if (Failure is not null)
            throw Failure;
    }

    public int CallCount(string name) { lock (Calls) return Calls.Count(c => c == name || c.StartsWith(name + ":", StringComparison.Ordinal)); }
}

public sealed class FakeUsosAdapter : FakeAdapterBase, IUsosAdapter
{
    public StudentProfile Profile { get; set; } = new("1", "Jan", "Kowalski", "jan@x.pl", "12345", null, []);
    public List<Term> Terms { get; set; } = [];
    public List<TimetableEntry> Timetable { get; set; } = [];
    public List<Grade> Grades { get; set; } = [];
    public List<Payment> Payments { get; set; } = [];
    public List<MessageThread> Messages { get; set; } = [];
    public List<Announcement> Announcements { get; set; } = [];

    public async Task<StudentProfile> GetProfileAsync(CancellationToken ct = default) { await EnterAsync("GetProfile", ct); return Profile; }
    public async Task<IReadOnlyList<Term>> GetTermsAsync(CancellationToken ct = default) { await EnterAsync("GetTerms", ct); return Terms; }
    public async Task<IReadOnlyList<CourseUnit>> GetCoursesAsync(string? termId = null, CancellationToken ct = default) { await EnterAsync("GetCourses", ct); return []; }
    public async Task<IReadOnlyList<TimetableEntry>> GetTimetableAsync(DateOnly from, DateOnly to, CancellationToken ct = default) { await EnterAsync($"GetTimetable:{from:yyyy-MM-dd}", ct); return Timetable; }
    public async Task<IReadOnlyList<Grade>> GetGradesAsync(IReadOnlyList<string>? termIds = null, CancellationToken ct = default) { await EnterAsync("GetGrades", ct); return Grades; }
    public async Task<IReadOnlyList<Payment>> GetPaymentsAsync(CancellationToken ct = default) { await EnterAsync("GetPayments", ct); return Payments; }
    public async Task<IReadOnlyList<MessageThread>> ListMessagesAsync(int limit = 50, int offset = 0, CancellationToken ct = default) { await EnterAsync("ListMessages", ct); return Messages; }
    public async Task<Message> GetMessageAsync(string id, CancellationToken ct = default) { await EnterAsync("GetMessage", ct); return new Message(Messages.First(m => m.Id == id), "<p>x</p>", []); }
    public async Task<string> SendMessageAsync(IReadOnlyList<string> to, string subject, string body, CancellationToken ct = default) { await EnterAsync("SendMessage", ct); return "new-1"; }
    public async Task<IReadOnlyList<Contact>> GetDeanOfficeContactsAsync(CancellationToken ct = default) { await EnterAsync("GetDeanOfficeContacts", ct); return []; }
    public async Task<IReadOnlyList<Announcement>> GetAnnouncementsAsync(int limit = 20, CancellationToken ct = default) { await EnterAsync("GetAnnouncements", ct); return Announcements; }
}

public sealed class FakePlatonAdapter : FakeAdapterBase, IPlatonAdapter
{
    public PlatonSiteInfo SiteInfo { get; set; } = new(7, "Jan Kowalski", "Platon", "4.5");
    public List<LmsCourse> Courses { get; set; } = [];
    public Dictionary<string, List<LmsSection>> Contents { get; } = [];
    public List<LmsEvent> Events { get; set; } = [];
    public List<Announcement> Announcements { get; set; } = [];
    public List<LmsQuiz> Quizzes { get; set; } = [];
    public List<QuizAttempt> Attempts { get; set; } = [];
    public QuizAttemptData? AttemptData { get; set; }
    public AuthenticatedDownload Download { get; set; } = new(new Uri("https://platon.example/file"), new Dictionary<string, string>());
    /// <summary>Kolejność wywołań zapisu quizów: "save:{attempt}:{liczba odpowiedzi}", "process:{attempt}".</summary>
    public List<string> QuizLog { get; } = [];
    public Func<string, bool>? FailQuizWrite { get; set; }

    public async Task<PlatonSiteInfo> GetSiteInfoAsync(CancellationToken ct = default) { await EnterAsync("GetSiteInfo", ct); return SiteInfo; }
    public async Task<IReadOnlyList<LmsCourse>> GetCoursesAsync(CancellationToken ct = default) { await EnterAsync("GetCourses", ct); return Courses; }
    public async Task<IReadOnlyList<LmsSection>> GetCourseContentsAsync(string courseId, CancellationToken ct = default) { await EnterAsync("GetCourseContents", ct); return Contents.GetValueOrDefault(courseId) ?? []; }
    public async Task<IReadOnlyList<LmsAssignment>> GetAssignmentsAsync(CancellationToken ct = default) { await EnterAsync("GetAssignments", ct); return []; }
    public async Task<IReadOnlyList<LmsEvent>> GetUpcomingEventsAsync(int days = 14, CancellationToken ct = default) { await EnterAsync("GetUpcomingEvents", ct); return Events; }
    public async Task<IReadOnlyList<LmsGradeItem>> GetGradesAsync(string courseId, CancellationToken ct = default) { await EnterAsync("GetGrades", ct); return []; }
    public async Task<IReadOnlyList<Announcement>> GetAnnouncementsAsync(int limit = 20, CancellationToken ct = default) { await EnterAsync("GetAnnouncements", ct); return Announcements; }
    public async Task<AuthenticatedDownload> GetFileDownloadAsync(string url, CancellationToken ct = default) { await EnterAsync("GetFileDownload", ct); return Download; }
    public async Task<IReadOnlyList<LmsQuiz>> GetQuizzesAsync(IReadOnlyList<string>? courseIds = null, CancellationToken ct = default) { await EnterAsync("GetQuizzes", ct); return Quizzes; }
    public async Task<IReadOnlyList<QuizAttempt>> GetQuizAttemptsAsync(string quizId, CancellationToken ct = default) { await EnterAsync("GetQuizAttempts", ct); return Attempts.Where(a => a.QuizId == quizId).ToList(); }

    public async Task<QuizAttempt> StartQuizAttemptAsync(string quizId, IReadOnlyList<QuizAnswer>? preflight = null, bool forceNew = false, CancellationToken ct = default)
    {
        await EnterAsync("StartQuizAttempt", ct);
        var attempt = new QuizAttempt("att-" + (Attempts.Count + 1), quizId, Attempts.Count + 1, QuizAttemptState.InProgress, DateTimeOffset.UnixEpoch, null, null, 0);
        Attempts.Add(attempt);
        return attempt;
    }

    public async Task<QuizAttemptData> GetQuizAttemptDataAsync(string attemptId, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default)
    {
        await EnterAsync("GetQuizAttemptData", ct);
        var attempt = Attempts.First(a => a.Id == attemptId);
        return AttemptData is not null && AttemptData.Attempt.Id == attemptId
            ? AttemptData
            : new QuizAttemptData(attempt, [new QuizQuestion(1, 0, "shortanswer", "<input name=\"q1:1_answer\">", null, "todo", null, null, 1, ["q1:1_answer"])], preflight ?? []);
    }

    public async Task SaveQuizAttemptAsync(string attemptId, IReadOnlyList<QuizAnswer> answers, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default)
    {
        await EnterAsync("SaveQuizAttempt", ct);
        if (FailQuizWrite?.Invoke("save") == true) throw new AdapterUnavailableException("save failed");
        QuizLog.Add($"save:{attemptId}:{answers.Count}");
    }

    public async Task<QuizAttemptState> ProcessQuizAttemptAsync(string attemptId, IReadOnlyList<QuizAnswer> answers, bool finish, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default)
    {
        await EnterAsync("ProcessQuizAttempt", ct);
        if (FailQuizWrite?.Invoke("process") == true) throw new AdapterUnavailableException("process failed");
        QuizLog.Add($"process:{attemptId}:{(finish ? "finish" : "keep")}:{answers.Count}");
        return finish ? QuizAttemptState.Finished : QuizAttemptState.InProgress;
    }

    public async Task<QuizAttemptReview> GetQuizAttemptReviewAsync(string attemptId, CancellationToken ct = default)
    {
        await EnterAsync("GetQuizAttemptReview", ct);
        return new QuizAttemptReview(Attempts.First(a => a.Id == attemptId), 5, 10, []);
    }
}

/// <summary>Menedżer połączeń zwracający gotowe adaptery (bez SecureStore) — dla testów usług wyższego poziomu.</summary>
public sealed class StubConnectionManager : IConnectionManager
{
    public IUsosAdapter? Usos { get; set; }
    public IPlatonAdapter? Platon { get; set; }
    public event EventHandler<SourceSystem>? Changed { add { } remove { } }

    public Task<IReadOnlyDictionary<SourceSystem, ConnectionInfo>> GetStatusAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<SourceSystem, ConnectionInfo>>(new Dictionary<SourceSystem, ConnectionInfo>
        {
            [SourceSystem.Usos] = new(SourceSystem.Usos, Usos is null ? ConnectionStatus.Disconnected : ConnectionStatus.Connected, "api", null, null),
            [SourceSystem.Platon] = new(SourceSystem.Platon, Platon is null ? ConnectionStatus.Disconnected : ConnectionStatus.Connected, "ws", null, null),
        });

    public Task ConnectUsosAsync(UsosCredentials credentials, CancellationToken ct = default) => Task.CompletedTask;
    public Task ConnectPlatonAsync(PlatonCredentials credentials, CancellationToken ct = default) => Task.CompletedTask;
    public Task DisconnectAsync(SourceSystem system, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IUsosAdapter> GetUsosAsync(CancellationToken ct = default) => Task.FromResult(Usos ?? throw new NotConnectedException(SourceSystem.Usos));
    public Task<IPlatonAdapter> GetPlatonAsync(CancellationToken ct = default) => Task.FromResult(Platon ?? throw new NotConnectedException(SourceSystem.Platon));
}
