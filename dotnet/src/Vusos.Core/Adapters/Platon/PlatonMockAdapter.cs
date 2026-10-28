using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Vusos.Core.Domain;
using Vusos.Core.Platform;

namespace Vusos.Core.Adapters.Platon;

/// <summary>
/// Deterministyczny adapter demo Platona (dev/testy/demo bez dostępu do uczelni). Wszystkie daty zbioru są względne
/// do zegara, więc demo nigdy się nie starzeje. Quizy działają w pamięci od początku do końca:
/// start → dane podejścia → zapis → zakończenie → przegląd z obliczoną oceną.
/// </summary>
public sealed partial class PlatonMockAdapter : IPlatonAdapter
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private const long FirstAttemptId = 60_001;

    private readonly IClock _clock;
    private readonly string _base;
    private readonly DateTimeOffset _t0;
    private readonly DateTimeOffset _day0;

    private readonly IReadOnlyList<LmsCourse> _courses;
    private readonly IReadOnlyList<LmsAssignment> _assignments;
    private readonly Dictionary<string, IReadOnlyList<LmsSection>> _contents;
    private readonly IReadOnlyList<LmsEvent> _events;
    private readonly Dictionary<string, IReadOnlyList<LmsGradeItem>> _grades = [];
    private readonly IReadOnlyList<Announcement> _announcements;
    private readonly IReadOnlyList<LmsQuiz> _quizList;

    private readonly object _quizLock = new();
    private readonly List<AttemptRecord> _attempts = [];
    private long _nextAttemptId = FirstAttemptId;

    public PlatonMockAdapter(IClock? clock = null, string? baseUrl = null)
    {
        _clock = clock ?? new SystemClock();
        _base = MoodleUrls.Normalize(string.IsNullOrWhiteSpace(baseUrl) ? "https://platon.vistula.edu.pl/" : baseUrl).ToString();
        _t0 = _clock.Now.ToUniversalTime();
        _day0 = new DateTimeOffset(DateTime.SpecifyKind(_t0.UtcDateTime.Date, DateTimeKind.Utc));

        _courses = BuildCourses();
        _assignments = BuildAssignments();
        _contents = MockData.Courses.Select((c, i) => (c, i))
            .ToDictionary(x => Str(x.c.Id), x => (IReadOnlyList<LmsSection>)BuildContents(x.i));
        _quizList = BuildQuizList();
        _events = BuildEvents();
        BuildGrades();
        _announcements = BuildAnnouncements();
    }

    // ---------- pomocnicze ----------

    private static string Str(long v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Stabilny, mały identyfikator z klucza definicji.</summary>
    private static int KeyId(int prefix, string key)
    {
        var h = 0;
        foreach (var ch in key) h = (h * 31 + ch) % 90_000;
        return prefix + h;
    }

    private static string F2(double v) => v.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', ',');

    /// <summary>Dzień <paramref name="days"/> po dzisiejszej północy UTC, godz. 21:59Z (= 22:59/23:59 w Warszawie).</summary>
    private DateTimeOffset Deadline(int days) => _day0 + days * Day + TimeSpan.FromHours(21) + TimeSpan.FromMinutes(59);

    private DateTimeOffset Ago(TimeSpan span) => _t0 - span;

    private static MockCourse CourseOf(int index) => MockData.Courses[index];
    private static string CourseName(MockCourse c) => $"{c.Full} ({c.Short})";
    private static int AssignId(MockAssignment a) => KeyId(8000, a.Key);
    private static int AssignCmid(MockAssignment a) => KeyId(412000, a.Key);
    private static int QuizId(MockQuiz q) => KeyId(9000, q.Key);
    private static int QuizCmid(MockQuiz q) => KeyId(413000, q.Key);

    // ---------- kursy ----------

    private List<LmsCourse> BuildCourses() =>
        MockData.Courses.Select((c, i) => new LmsCourse(
            Id: Str(c.Id),
            ShortName: c.Short,
            FullName: CourseName(c),
            Category: "Informatyka 2025/26",
            Progress: c.Progress,
            StartDate: new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero),
            EndDate: new DateTimeOffset(2026, 2, 14, 0, 0, 0, TimeSpan.Zero),
            ImageUrl: "data:image/svg+xml;utf8," + Uri.EscapeDataString(
                $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"400\" height=\"200\"><rect width=\"400\" height=\"200\" fill=\"{c.Color}\"/></svg>"),
            Url: $"{_base}course/view.php?id={c.Id}",
            UsosCourseId: $"{string.Join("-", c.Short.Split('-').Take(2))}-{i + 1}",
            LastAccess: Ago(c.LastAccessHours * Hour))).ToList();

    private List<LmsAssignment> BuildAssignments() =>
        MockData.Assignments.Select(a =>
        {
            var c = CourseOf(a.Course);
            return new LmsAssignment(
                Id: Str(AssignId(a)),
                CourseId: Str(c.Id),
                CourseName: CourseName(c),
                Name: a.Name,
                DueDate: Deadline(a.DueDays),
                CutoffDate: null,
                Submitted: a.Submitted,
                Graded: a.Grade is not null,
                Grade: a.Grade is { } g ? $"{F2(g.Value)} / {F2(g.Max)}" : null,
                Url: $"{_base}mod/assign/view.php?id={AssignCmid(a)}");
        }).OrderBy(a => a.DueDate).ToList();

    // ---------- treść kursów ----------

    private List<LmsSection> BuildContents(int courseIndex)
    {
        var c = CourseOf(courseIndex);
        var cid = Str(c.Id);
        var seq = 0;
        var sections = new List<LmsSection>();

        int NextId() => c.Id * 100 + ++seq; // stabilne w obrębie kursu

        LmsFile FileOf(int ctx, MockFile f, int modifiedDaysAgo) => new(
            f.Name,
            $"{_base}pluginfile.php/{ctx}/mod_resource/content/1/{Uri.EscapeDataString(f.Name)}",
            f.Mime, f.Size, Ago(modifiedDaysAgo * Day));

        LmsModule Mod(string sid, string sn, string name, LmsModuleType type, string? url = null, string? description = null,
            bool? completed = null, Func<int, IReadOnlyList<LmsFile>>? files = null)
        {
            var id = NextId();
            var raw = type.ToString().ToLowerInvariant();
            return new LmsModule(
                Id: Str(id), CourseId: cid, SectionId: sid, SectionName: sn, Name: name, Type: type, RawType: raw,
                Url: url ?? $"{_base}mod/{raw}/view.php?id={id}",
                DescriptionHtml: description,
                Files: files?.Invoke(id) ?? [],
                Completed: completed, Visible: true);
        }

        void AddSection(string name, string? summary, Func<string, string, List<LmsModule>> build)
        {
            var id = Str(c.Id * 10 + sections.Count);
            sections.Add(new LmsSection(id, name, summary, build(id, name)));
        }

        AddSection("Wprowadzenie", $"<p>Witamy na kursie <b>{c.Full}</b>. Prowadzący: {c.Teacher}.</p>", (sid, sn) =>
        [
            Mod(sid, sn, "Ogłoszenia", LmsModuleType.Forum, description: "<p>Ogłoszenia prowadzącego</p>"),
            Mod(sid, sn, "Sylabus", LmsModuleType.Resource, completed: true,
                files: ctx => [FileOf(ctx, new MockFile($"Sylabus_{c.Short}.pdf", MockData.PdfMime, 183_422), 40)])
        ]);

        for (var w = 1; w <= c.Weeks; w++)
        {
            var week = w;
            AddSection($"Tydzień {week}", null, (sid, sn) =>
            {
                var folder = Mod(sid, sn, $"Slajdy z wykładu {week}", LmsModuleType.Folder, completed: week == 1,
                    files: ctx =>
                    [
                        FileOf(ctx, new MockFile($"Wyklad_0{week}.pptx", MockData.PptxMime, 2_411_023 + week * 1000), 30 - week * 7),
                        FileOf(ctx, new MockFile($"Cwiczenia_0{week}.pdf", MockData.PdfMime, 311_902 + week * 100), 28 - week * 7)
                    ]);
                var link = Mod(sid, sn, c.Link.Name, LmsModuleType.Url, url: c.Link.Url);
                return [folder, link];
            });
        }

        AddSection("Materiały", null, (sid, sn) =>
            c.Material.Select(f => Mod(sid, sn,
                Regex.Replace(Path.GetFileNameWithoutExtension(f.Name), "_", " "),
                LmsModuleType.Resource, completed: false,
                files: ctx => [FileOf(ctx, f, 10)])).ToList());

        AddSection("Zadania", null, (sid, sn) =>
        {
            var output = new List<LmsModule>();
            foreach (var a in MockData.Assignments.Where(x => x.Course == courseIndex))
            {
                output.Add(new LmsModule(
                    Id: Str(AssignCmid(a)), CourseId: cid, SectionId: sid, SectionName: sn, Name: a.Name,
                    Type: LmsModuleType.Assign, RawType: "assign",
                    Url: $"{_base}mod/assign/view.php?id={AssignCmid(a)}",
                    DescriptionHtml: $"<p>{a.Description}</p>",
                    Files: [], Completed: a.Submitted, Visible: true,
                    InstanceId: Str(AssignId(a))));
            }
            foreach (var q in MockData.Quizzes.Where(x => x.Course == courseIndex))
            {
                output.Add(new LmsModule(
                    Id: Str(QuizCmid(q)), CourseId: cid, SectionId: sid, SectionName: sn, Name: q.Name,
                    Type: LmsModuleType.Quiz, RawType: "quiz",
                    Url: $"{_base}mod/quiz/view.php?id={QuizCmid(q)}",
                    DescriptionHtml: null, Files: [], Completed: false, Visible: true,
                    InstanceId: Str(QuizId(q))));
            }
            if (courseIndex == 5) output.Add(Mod(sid, sn, "Discussion forum: Study abroad", LmsModuleType.Forum));
            return output;
        });

        return sections;
    }

    // ---------- quizy (lista) ----------

    private List<LmsQuiz> BuildQuizList() =>
        MockData.Quizzes.Select(q => new LmsQuiz(
            Id: Str(QuizId(q)),
            CourseModuleId: Str(QuizCmid(q)),
            CourseId: Str(CourseOf(q.Course).Id),
            Name: q.Name,
            IntroHtml: q.Intro,
            TimeOpen: Ago(7 * Day),
            TimeClose: Deadline(q.CloseDays),
            TimeLimitSeconds: q.TimeLimitSeconds,
            Attempts: q.Attempts,
            AllowOfflineAttempts: q.AllowOffline,
            HasQuestions: true,
            Url: $"{_base}mod/quiz/view.php?id={QuizCmid(q)}")).ToList();

    // ---------- kalendarz ----------

    private List<LmsEvent> BuildEvents()
    {
        var all = new List<LmsEvent>();
        foreach (var a in MockData.Assignments)
        {
            if (a.Submitted) continue;
            var c = CourseOf(a.Course);
            all.Add(new LmsEvent(
                Id: Str(7_000_000 + AssignId(a)), CourseId: Str(c.Id), CourseName: CourseName(c),
                Name: $"{a.Name} - termin oddania", Type: "due", ModuleType: "assign",
                Start: Deadline(a.DueDays), End: null,
                Url: $"{_base}mod/assign/view.php?id={AssignCmid(a)}", ActionName: "Dodaj zgłoszenie"));
        }
        foreach (var q in MockData.Quizzes)
        {
            var c = CourseOf(q.Course);
            all.Add(new LmsEvent(
                Id: Str(7_500_000 + QuizId(q)), CourseId: Str(c.Id), CourseName: CourseName(c),
                Name: $"{q.Name} - zamknięcie", Type: "close", ModuleType: "quiz",
                Start: Deadline(q.CloseDays), End: null,
                Url: $"{_base}mod/quiz/view.php?id={QuizCmid(q)}", ActionName: "Podejdź do quizu"));
        }
        var bd = CourseOf(1);
        var so = CourseOf(3);
        all.Add(new LmsEvent(
            "7900001", Str(bd.Id), CourseName(bd), "Konsultacje online (MS Teams)", "course", null,
            _day0 + 8 * Day + 14 * Hour, _day0 + 8 * Day + 15 * Hour, $"{_base}calendar/view.php?view=day", null));
        all.Add(new LmsEvent(
            "7900002", Str(so.Id), CourseName(so), "Kolokwium zaliczeniowe - sala 204", "course", null,
            _day0 + 13 * Day + 9 * Hour, _day0 + 13 * Day + TimeSpan.FromHours(10.5), $"{_base}calendar/view.php?view=day", null));
        return all.OrderBy(e => e.Start).ToList();
    }

    // ---------- oceny ----------

    private void BuildGrades()
    {
        for (var ci = 0; ci < MockData.Courses.Count; ci++)
        {
            var c = MockData.Courses[ci];
            var cid = Str(c.Id);
            var items = new List<LmsGradeItem>();
            double total = 0, totalMax = 0;
            DateTimeOffset? latest = null;

            void Add(int id, string name, string type, double? value, double max, int gradedDaysAgo, string? feedback)
            {
                DateTimeOffset? dateGraded = value is not null ? Ago(gradedDaysAgo * Day) : null;
                if (value is { } v)
                {
                    total += v;
                    totalMax += max;
                    if (latest is null || (dateGraded is { } d && d > latest)) latest = dateGraded;
                }
                items.Add(new LmsGradeItem(
                    Id: Str(id), CourseId: cid, ItemName: name, ItemType: type,
                    GradeFormatted: value is { } gv ? F2(gv) : null,
                    GradeRaw: value, GradeMax: max,
                    Percentage: value is { } pv ? $"{F2(pv / max * 100)} %" : null,
                    FeedbackHtml: feedback, DateGraded: dateGraded));
            }

            foreach (var a in MockData.Assignments.Where(x => x.Course == ci))
                Add(KeyId(123_000, a.Key), a.Name, "mod", a.Grade?.Value, a.Grade?.Max ?? 10, 6, a.Grade is not null ? "<p>Dobra robota.</p>" : null);
            foreach (var q in MockData.Quizzes.Where(x => x.Course == ci))
                Add(KeyId(124_000, q.Key), q.Name, "mod", null, 10, 0, null);
            if (ci == 1) Add(125_001, "Kolokwium 1", "manual", 17, 20, 12, "<p>Zabrakło normalizacji w zad. 3.</p>");
            if (ci == 4) Add(125_002, "Aktywność na ćwiczeniach", "manual", 4, 5, 3, null);

            var maxForTotal = items.Sum(i => i.GradeMax ?? 0);
            items.Add(new LmsGradeItem(
                Id: Str(126_000 + c.Id), CourseId: cid, ItemName: "Suma końcowa", ItemType: "course",
                GradeFormatted: totalMax > 0 ? F2(total) : null,
                GradeRaw: totalMax > 0 ? total : null,
                GradeMax: maxForTotal,
                Percentage: totalMax > 0 ? $"{F2(total / maxForTotal * 100)} %" : null,
                FeedbackHtml: null, DateGraded: latest));
            _grades[cid] = items;
        }
    }

    // ---------- ogłoszenia ----------

    private List<Announcement> BuildAnnouncements()
    {
        Announcement A(int id, string title, string body, TimeSpan ago, string author, string category) => new(
            $"platon:{id}", SourceSystem.Platon, title, body, Ago(ago), author, category, $"{_base}mod/forum/discuss.php?d={id}");
        return
        [
            A(15001, "Zmiana godzin pracy dziekanatu",
                "<p>Od poniedziałku dziekanat Wydziału Informatyki jest czynny w godzinach 9:00-15:00.</p>",
                1 * Day + 2 * Hour, "Dziekanat Informatyki", "Aktualności"),
            A(16001, "Przesunięcie wykładu z algorytmów",
                "<p>Wykład w czwartek odbędzie się o 12:00 zamiast 10:00 (sala 101).</p>",
                2 * Day + 5 * Hour, "dr Anna Nowak", $"Ogłoszenia ({CourseOf(0).Short})"),
            A(15002, "Rejestracja na lektoraty",
                "<p>Rejestracja na lektoraty językowe trwa do piątku do godziny 23:59.</p>",
                5 * Day, "Studium Języków Obcych", "Aktualności"),
            A(15003, "Planowana przerwa serwisowa Platona",
                "<p>W sobotę w godzinach 2:00-4:00 platforma będzie niedostępna.</p>",
                9 * Day, "Centrum Informatyczne", "Aktualności")
        ];
    }

    // ---------- IPlatonAdapter: dane ----------

    public Task<PlatonSiteInfo> GetSiteInfoAsync(CancellationToken ct = default) =>
        Task.FromResult(new PlatonSiteInfo(48213, "Jan Kowalski", "Platon (demo)", "4.5.2 (demo)"));

    public Task<IReadOnlyList<LmsCourse>> GetCoursesAsync(CancellationToken ct = default) => Task.FromResult(_courses);

    public Task<IReadOnlyList<LmsSection>> GetCourseContentsAsync(string courseId, CancellationToken ct = default) =>
        _contents.TryGetValue(courseId, out var c)
            ? Task.FromResult(c)
            : throw new AdapterUnavailableException($"Nieznany kurs {courseId}") { IsTransient = false };

    public Task<IReadOnlyList<LmsAssignment>> GetAssignmentsAsync(CancellationToken ct = default) => Task.FromResult(_assignments);

    public Task<IReadOnlyList<LmsEvent>> GetUpcomingEventsAsync(int days = 14, CancellationToken ct = default)
    {
        var from = _t0;
        var to = from + days * Day;
        return Task.FromResult<IReadOnlyList<LmsEvent>>(_events.Where(e => e.Start >= from && e.Start <= to).ToList());
    }

    public Task<IReadOnlyList<LmsGradeItem>> GetGradesAsync(string courseId, CancellationToken ct = default) =>
        Task.FromResult(_grades.TryGetValue(courseId, out var g) ? g : []);

    public Task<IReadOnlyList<Announcement>> GetAnnouncementsAsync(int limit = 20, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Announcement>>(_announcements.Take(limit).ToList());

    public Task<AuthenticatedDownload> GetFileDownloadAsync(string url, CancellationToken ct = default) =>
        Task.FromResult(new AuthenticatedDownload(new Uri(url, UriKind.Absolute), new Dictionary<string, string>()));

    // ---------- IPlatonAdapter: quizy ----------

    private sealed class AttemptRecord(long id, MockQuiz quiz, int number, DateTimeOffset start)
    {
        public long Id { get; } = id;
        public MockQuiz Quiz { get; } = quiz;
        public int Number { get; } = number;
        public DateTimeOffset Start { get; } = start;
        public DateTimeOffset? Finish { get; set; }
        public QuizAttemptState State { get; set; } = QuizAttemptState.InProgress;
        public double? SumGrades { get; set; }
        public int CurrentPage { get; set; }
        public Dictionary<int, string> Answers { get; } = [];
        public Dictionary<int, int> SequenceChecks { get; } = [];
    }

    private static MockQuiz FindQuiz(string quizId) =>
        MockData.Quizzes.FirstOrDefault(q => Str(QuizId(q)) == quizId)
        ?? throw new AdapterUnavailableException($"Nieznany quiz {quizId}") { IsTransient = false };

    private AttemptRecord FindAttempt(string attemptId) =>
        _attempts.FirstOrDefault(a => Str(a.Id) == attemptId)
        ?? throw new AdapterUnavailableException($"Nieznane podejście {attemptId}") { IsTransient = false };

    /// <summary>Stan z uwzględnieniem limitu czasu (po jego upływie podejście jest „overdue”, jak w Moodle po cronie).</summary>
    private QuizAttemptState EffectiveState(AttemptRecord a)
    {
        if (a.State == QuizAttemptState.InProgress && a.Quiz.TimeLimitSeconds is { } limit
            && _clock.Now > a.Start.AddSeconds(limit))
            return QuizAttemptState.Overdue;
        return a.State;
    }

    private static bool IsOpen(QuizAttemptState s) => s is QuizAttemptState.InProgress or QuizAttemptState.Overdue;

    private QuizAttempt ToAttempt(AttemptRecord a) => new(
        Id: Str(a.Id), QuizId: Str(QuizId(a.Quiz)), Attempt: a.Number, State: EffectiveState(a),
        TimeStart: a.Start, TimeFinish: a.Finish, SumGrades: a.SumGrades, CurrentPage: a.CurrentPage);

    public Task<IReadOnlyList<LmsQuiz>> GetQuizzesAsync(IReadOnlyList<string>? courseIds = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LmsQuiz>>(
            _quizList.Where(q => courseIds is null || courseIds.Count == 0 || courseIds.Contains(q.CourseId)).ToList());

    public Task<IReadOnlyList<QuizAttempt>> GetQuizAttemptsAsync(string quizId, CancellationToken ct = default)
    {
        var quiz = FindQuiz(quizId);
        lock (_quizLock)
            return Task.FromResult<IReadOnlyList<QuizAttempt>>(
                _attempts.Where(a => a.Quiz == quiz).OrderBy(a => a.Number).Select(ToAttempt).ToList());
    }

    public Task<QuizAttempt> StartQuizAttemptAsync(
        string quizId, IReadOnlyList<QuizAnswer>? preflight = null, bool forceNew = false, CancellationToken ct = default)
    {
        var quiz = FindQuiz(quizId);
        var info = _quizList.First(q => q.Id == quizId);
        var now = _clock.Now;
        lock (_quizLock)
        {
            if (info.TimeOpen is { } open && now < open)
                throw new AdapterUnavailableException("Quiz nie jest jeszcze otwarty") { IsTransient = false };
            if (info.TimeClose is { } close && now > close)
                throw new AdapterUnavailableException("Quiz jest już zamknięty") { IsTransient = false };

            var mine = _attempts.Where(a => a.Quiz == quiz).ToList();
            var open1 = mine.FirstOrDefault(a => IsOpen(EffectiveState(a)));
            if (open1 is not null && !forceNew) return Task.FromResult(ToAttempt(open1));
            if (open1 is not null)
            {
                open1.State = QuizAttemptState.Abandoned;
                open1.Finish = now;
            }
            if (quiz.Attempts > 0 && mine.Count >= quiz.Attempts)
                throw new AdapterUnavailableException("Wykorzystano wszystkie dozwolone podejścia") { IsTransient = false };

            var attempt = new AttemptRecord(_nextAttemptId++, quiz, mine.Count + 1, now);
            _attempts.Add(attempt);
            return Task.FromResult(ToAttempt(attempt));
        }
    }

    public Task<QuizAttemptData> GetQuizAttemptDataAsync(
        string attemptId, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default)
    {
        lock (_quizLock)
        {
            var a = FindAttempt(attemptId);
            if (!IsOpen(EffectiveState(a)))
                throw new AdapterUnavailableException("Podejście jest zakończone; użyj przeglądu") { IsTransient = false };
            var questions = a.Quiz.Questions.OrderBy(q => q.Page).ThenBy(q => q.Slot).Select(q => RenderQuestion(a, q, review: false)).ToList();
            return Task.FromResult(new QuizAttemptData(ToAttempt(a), questions, preflight ?? []));
        }
    }

    public Task SaveQuizAttemptAsync(
        string attemptId, IReadOnlyList<QuizAnswer> answers, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default)
    {
        lock (_quizLock)
        {
            var a = FindAttempt(attemptId);
            RequireOpen(a);
            ApplyAnswers(a, answers);
        }
        return Task.CompletedTask;
    }

    public Task<QuizAttemptState> ProcessQuizAttemptAsync(
        string attemptId, IReadOnlyList<QuizAnswer> answers, bool finish, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default)
    {
        lock (_quizLock)
        {
            var a = FindAttempt(attemptId);
            RequireOpen(a);
            ApplyAnswers(a, answers);
            if (!finish) return Task.FromResult(EffectiveState(a));

            a.SumGrades = a.Quiz.Questions.Sum(q => IsCorrect(q, a.Answers.GetValueOrDefault(q.Slot)) ? q.MaxMark : 0);
            a.State = QuizAttemptState.Finished;
            a.Finish = _clock.Now;
            return Task.FromResult(QuizAttemptState.Finished);
        }
    }

    public Task<QuizAttemptReview> GetQuizAttemptReviewAsync(string attemptId, CancellationToken ct = default)
    {
        lock (_quizLock)
        {
            var a = FindAttempt(attemptId);
            if (a.State != QuizAttemptState.Finished)
                throw new AdapterUnavailableException("Przegląd jest dostępny dopiero po zakończeniu podejścia") { IsTransient = false };
            var maxMarks = a.Quiz.Questions.Sum(q => q.MaxMark);
            var grade = maxMarks > 0 ? Math.Round((a.SumGrades ?? 0) / maxMarks * a.Quiz.Grade, 2, MidpointRounding.AwayFromZero) : 0;
            var questions = a.Quiz.Questions.OrderBy(q => q.Page).ThenBy(q => q.Slot).Select(q => RenderQuestion(a, q, review: true)).ToList();
            return Task.FromResult(new QuizAttemptReview(ToAttempt(a), grade, a.Quiz.Grade, questions));
        }
    }

    private void RequireOpen(AttemptRecord a)
    {
        if (!IsOpen(EffectiveState(a)))
            throw new AdapterUnavailableException("Podejście zostało już zamknięte") { IsTransient = false };
    }

    [GeneratedRegex(@"^q(\d+):(\d+)_answer$")] private static partial Regex AnswerFieldName();

    private static void ApplyAnswers(AttemptRecord a, IReadOnlyList<QuizAnswer> answers)
    {
        foreach (var answer in answers)
        {
            var m = AnswerFieldName().Match(answer.Name);
            if (!m.Success || m.Groups[1].Value != Str(a.Id)) continue; // sequencecheck, flagged itp. pomijamy
            var slot = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var question = a.Quiz.Questions.FirstOrDefault(q => q.Slot == slot);
            if (question is null) continue;

            var hasValue = !string.IsNullOrWhiteSpace(answer.Value) && !(question.Kind != MockQuestionKind.ShortAnswer && answer.Value == "-1");
            var changed = hasValue
                ? !a.Answers.TryGetValue(slot, out var old) || old != answer.Value
                : a.Answers.ContainsKey(slot);
            if (!changed) continue;
            if (hasValue) a.Answers[slot] = answer.Value; else a.Answers.Remove(slot);
            a.SequenceChecks[slot] = a.SequenceChecks.GetValueOrDefault(slot, 1) + 1;
            a.CurrentPage = question.Page;
        }
    }

    private static bool IsCorrect(MockQuestion q, string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return false;
        return q.Kind == MockQuestionKind.ShortAnswer
            ? q.Correct.Any(c => string.Equals(c, answer.Trim(), StringComparison.OrdinalIgnoreCase))
            : q.Correct.Contains(answer);
    }

    // ---------- HTML pytań (na wzór formularzy Moodle) ----------

    private static QuizQuestion RenderQuestion(AttemptRecord a, MockQuestion q, bool review)
    {
        var answer = a.Answers.GetValueOrDefault(q.Slot);
        var answered = !string.IsNullOrWhiteSpace(answer);
        var correct = IsCorrect(q, answer);
        var html = BuildQuestionHtml(a, q, answer, review, correct);

        string state, status;
        double? mark = null;
        if (review)
        {
            state = correct ? "gradedright" : "gradedwrong";
            status = correct ? "Poprawnie" : "Niepoprawnie";
            mark = correct ? q.MaxMark : 0;
        }
        else
        {
            state = answered ? "complete" : "todo";
            status = answered ? "Odpowiedź zapisana" : "Jeszcze nie udzielono odpowiedzi";
        }
        return new QuizQuestion(
            Slot: q.Slot, Page: q.Page, Type: q.TypeName, Html: html,
            SequenceCheck: Str(a.SequenceChecks.GetValueOrDefault(q.Slot, 1)),
            State: state, Status: status, Mark: mark, MaxMark: q.MaxMark,
            FieldNames: PlatonFunctions.ExtractFieldNames(html));
    }

    private static string BuildQuestionHtml(AttemptRecord a, MockQuestion q, string? answer, bool review, bool correct)
    {
        var u = a.Id;
        var s = q.Slot;
        var name = $"q{u}:{s}_answer";
        static string enc(string v) => WebUtility.HtmlEncode(v);
        var stateClass = review ? (correct ? "correct" : "incorrect") : (string.IsNullOrWhiteSpace(answer) ? "notyetanswered" : "answersaved");
        var disabled = review ? " disabled=\"disabled\"" : string.Empty;
        var seq = a.SequenceChecks.GetValueOrDefault(s, 1);

        var sb = new StringBuilder();
        void Ap(FormattableString f) => sb.Append(f.ToString(CultureInfo.InvariantCulture));
        Ap($"<div id=\"question-{u}-{s}\" class=\"que {q.TypeName} deferredfeedback {stateClass}\">");
        sb.Append("<div class=\"info\">");
        Ap($"<h3 class=\"no\">Pytanie <span class=\"qno\">{s}</span></h3>");
        Ap($"<div class=\"state\">{enc(review ? (correct ? "Poprawnie" : "Niepoprawnie") : (string.IsNullOrWhiteSpace(answer) ? "Jeszcze nie udzielono odpowiedzi" : "Odpowiedź zapisana"))}</div>");
        sb.Append(review
            ? $"<div class=\"grade\">Punkty: {F2(correct ? q.MaxMark : 0)} z {F2(q.MaxMark)}</div>"
            : $"<div class=\"grade\">Punkty maks.: {F2(q.MaxMark)}</div>");
        sb.Append("</div>");
        sb.Append("<div class=\"content\"><div class=\"formulation clearfix\">");
        sb.Append("<h4 class=\"accesshide\">Treść pytania</h4>");
        Ap($"<input type=\"hidden\" name=\"q{u}:{s}_:sequencecheck\" value=\"{seq}\" />");
        Ap($"<div class=\"qtext\"><p>{enc(q.Text)}</p></div>");
        sb.Append("<div class=\"ablock\">");

        if (q.Kind == MockQuestionKind.ShortAnswer)
        {
            sb.Append("<div class=\"prompt\">Odpowiedź:</div>");
            sb.Append("<span class=\"answer\">");
            Ap($"<label class=\"subq accesshide\" for=\"{name}\">Odpowiedź</label>");
            Ap($"<input type=\"text\" name=\"{name}\" value=\"{enc(answer ?? string.Empty)}\" id=\"{name}\" size=\"80\" class=\"form-control d-inline\"{disabled} />");
            sb.Append("</span>");
        }
        else
        {
            var options = q.Kind == MockQuestionKind.TrueFalse
                ? new[] { ("1", "Prawda"), ("0", "Fałsz") }
                : q.Choices.Select((c, i) => (i.ToString(CultureInfo.InvariantCulture), c)).ToArray();
            sb.Append("<div class=\"prompt\">Wybierz jedną odpowiedź:</div>");
            sb.Append("<div class=\"answer\">");
            Ap($"<input type=\"hidden\" name=\"{name}\" value=\"-1\" />");
            for (var i = 0; i < options.Length; i++)
            {
                var (value, label) = options[i];
                var id = $"{name}{(q.Kind == MockQuestionKind.TrueFalse ? (value == "1" ? "1" : "0") : value)}";
                var checkedAttr = answer == value ? " checked=\"checked\"" : string.Empty;
                var prefix = q.Kind == MockQuestionKind.MultiChoice ? $"<span class=\"answernumber\">{(char)('a' + i)}. </span>" : string.Empty;
                Ap($"<div class=\"r{i % 2}\">");
                Ap($"<input type=\"radio\" name=\"{name}\" value=\"{value}\" id=\"{id}\" aria-labelledby=\"{id}_label\"{checkedAttr}{disabled} />");
                Ap($"<div class=\"d-flex w-auto\" id=\"{id}_label\" data-region=\"answer-label\">{prefix}<div class=\"flex-fill ml-1\">{enc(label)}</div></div>");
                sb.Append("</div>");
            }
            sb.Append("</div>");
        }

        sb.Append("</div>"); // ablock
        if (review)
        {
            var right = q.Kind switch
            {
                MockQuestionKind.MultiChoice => q.Choices[int.Parse(q.Correct[0], CultureInfo.InvariantCulture)],
                MockQuestionKind.TrueFalse => q.Correct[0] == "1" ? "Prawda" : "Fałsz",
                _ => q.Correct[0]
            };
            Ap($"<div class=\"outcome clearfix\"><div class=\"rightanswer\">Poprawna odpowiedź: {enc(right)}</div></div>");
        }
        sb.Append("</div></div></div>"); // formulation, content, que
        return sb.ToString();
    }
}
