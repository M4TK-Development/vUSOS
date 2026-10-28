using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vusos.Core.Domain;
using Vusos.Core.Platform;

namespace Vusos.Core.Adapters.Platon;

public sealed record PlatonFunctionsOptions
{
    public required string BaseUrl { get; init; }

    /// <summary>Znane id użytkownika (z zapisanych poświadczeń) — oszczędza wywołanie site_info, gdy funkcja jest niedostępna.</summary>
    public int? UserId { get; init; }

    public IClock? Clock { get; init; }

    /// <summary>Kursy, których forum „news” trafia do ogłoszeń. Domyślnie [1] (aktualności serwisu).</summary>
    public IReadOnlyList<long> NewsCourseIds { get; init; } = [1];

    /// <summary>Maks. liczba zadań, dla których pobieramy status zgłoszenia (jedno wywołanie na zadanie).</summary>
    public int MaxSubmissionStatusCalls { get; init; } = 20;

    /// <summary>
    /// Gdy true, adresy plików w treści kursu przechodzą przez transport (ws: token w URL). Domyślnie false:
    /// adres zostaje bez tokenu, a pobieranie idzie przez <see cref="PlatonFunctions.GetFileDownloadAsync"/>.
    /// </summary>
    public bool EmbedFileAuth { get; init; }
}

/// <summary>Adapter Platona (Moodle) nad dowolnym <see cref="IMoodleTransport"/>.</summary>
public sealed partial class PlatonFunctions : IPlatonAdapter
{
    private const int MaxQuizPages = 500;

    private readonly IMoodleTransport _transport;
    private readonly PlatonFunctionsOptions _options;
    private readonly Uri _base;
    private readonly IClock _clock;
    private readonly IReadOnlyList<long> _newsCourseIds;
    private readonly int _maxStatusCalls;

    private readonly object _siteInfoLock = new();
    private Task<RawSiteInfo>? _siteInfo;

    private static readonly IReadOnlyDictionary<string, object?> NoArgs = new Dictionary<string, object?>();

    public PlatonFunctions(IMoodleTransport transport, PlatonFunctionsOptions options)
    {
        _transport = transport;
        _options = options;
        _base = MoodleUrls.Normalize(options.BaseUrl);
        _clock = options.Clock ?? new SystemClock();
        _newsCourseIds = options.NewsCourseIds;
        _maxStatusCalls = options.MaxSubmissionStatusCalls;
    }

    // ---------- helpers (publiczne, testowane) ----------

    public static LmsModuleType ToModuleType(string modName) => modName switch
    {
        "resource" => LmsModuleType.Resource,
        "folder" => LmsModuleType.Folder,
        "url" => LmsModuleType.Url,
        "page" => LmsModuleType.Page,
        "book" => LmsModuleType.Book,
        "assign" => LmsModuleType.Assign,
        "quiz" => LmsModuleType.Quiz,
        "forum" => LmsModuleType.Forum,
        "label" => LmsModuleType.Label,
        "lesson" => LmsModuleType.Lesson,
        _ => LmsModuleType.Other
    };

    /// <summary>Sekundy Unix → UTC; 0/null → null.</summary>
    public static DateTimeOffset? FromUnix(long? seconds) =>
        seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;

    /// <summary>Usuwa znacznik <c>@@PLUGINFILE@@/</c> (poza edytorem nie ma znaczenia).</summary>
    public static string? StripPluginfilePlaceholders(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var cleaned = PluginfilePlaceholder().Replace(html, string.Empty);
        return cleaned.Trim().Length == 0 ? null : cleaned;
    }

    [GeneratedRegex("@@PLUGINFILE@@/?")] private static partial Regex PluginfilePlaceholder();
    [GeneratedRegex("<[^>]*>")] private static partial Regex HtmlTag();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex("""<(input|select|textarea)\b[^>]*?\bname\s*=\s*(?:"([^"]*)"|'([^']*)')[^>]*>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FormFieldTag();
    [GeneratedRegex("""\btype\s*=\s*["']?(submit|button|image|reset|file)["']?""", RegexOptions.IgnoreCase)]
    private static partial Regex NonValueInputType();

    private static string StripTags(string html) =>
        Whitespace().Replace(HtmlTag().Replace(html, string.Empty).Replace("&nbsp;", " "), " ").Trim();

    private static string? EmptyToNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Moodle używa „-” dla „brak oceny”.</summary>
    private static string? DashToNull(string? s)
    {
        var v = EmptyToNull(s)?.Trim();
        return v is not null && v != "-" ? v : null;
    }

    /// <summary>Nazwy pól formularza (input/select/textarea), unikalne, w kolejności wystąpienia.</summary>
    public static IReadOnlyList<string> ExtractFieldNames(string? html)
    {
        if (string.IsNullOrEmpty(html)) return [];
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in FormFieldTag().Matches(html))
        {
            if (m.Groups[1].Value.Equals("input", StringComparison.OrdinalIgnoreCase) && NonValueInputType().IsMatch(m.Value))
                continue;
            var name = System.Net.WebUtility.HtmlDecode(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
            if (name.Length > 0 && seen.Add(name)) names.Add(name);
        }
        return names;
    }

    private static T Parse<T>(JsonElement data, string fn) where T : class
    {
        try
        {
            return data.Deserialize<T>(PlatonJson.Options)
                ?? throw new AdapterUnavailableException($"Nieoczekiwana odpowiedź Platona ({fn}): pusta") { IsTransient = false };
        }
        catch (JsonException ex)
        {
            throw new AdapterUnavailableException($"Nieoczekiwana odpowiedź Platona ({fn}): {ex.Message}", ex) { IsTransient = false };
        }
    }

    private static async Task<TResult[]> MapLimit<T, TResult>(IReadOnlyList<T> items, int limit, Func<T, Task<TResult>> fn)
    {
        var results = new TResult[items.Count];
        var next = -1;
        async Task Worker()
        {
            for (;;)
            {
                var i = Interlocked.Increment(ref next);
                if (i >= items.Count) return;
                results[i] = await fn(items[i]).ConfigureAwait(false);
            }
        }
        var workers = Enumerable.Range(0, Math.Max(1, Math.Min(limit, items.Count))).Select(_ => Worker());
        await Task.WhenAll(workers).ConfigureAwait(false);
        return results;
    }

    private static long ParseId(string id, string what) =>
        long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new AdapterUnavailableException($"Nieprawidłowy identyfikator ({what}): {id}") { IsTransient = false };

    private string CourseUrl(long id) => $"{_base}course/view.php?id={id}";

    private Task<JsonElement> Call(string fn, Dictionary<string, object?> args, CancellationToken ct) =>
        _transport.CallAsync(fn, args, ct);

    // ---------- site info ----------

    private Task<RawSiteInfo> LoadSiteInfoAsync()
    {
        lock (_siteInfoLock)
        {
            // Nie cache'ujemy błędów: zadanie zakończone porażką zostaje zastąpione nowym wywołaniem.
            if (_siteInfo is { IsFaulted: false, IsCanceled: false }) return _siteInfo;
            return _siteInfo = LoadSiteInfoCoreAsync();
        }
    }

    private async Task<RawSiteInfo> LoadSiteInfoCoreAsync()
    {
        const string fn = "core_webservice_get_site_info";
        return Parse<RawSiteInfo>(await _transport.CallAsync(fn, NoArgs, CancellationToken.None).ConfigureAwait(false), fn);
    }

    private async Task<long> GetUserIdAsync(CancellationToken ct)
    {
        if (_options.UserId is { } id) return id;
        return (await LoadSiteInfoAsync().WaitAsync(ct).ConfigureAwait(false)).UserId;
    }

    public async Task<PlatonSiteInfo> GetSiteInfoAsync(CancellationToken ct = default)
    {
        RawSiteInfo info;
        try
        {
            info = await LoadSiteInfoAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not AdapterAuthException && _options.UserId is not null)
        {
            // TODO(verify): core_webservice_get_site_info może nie być wystawione przez lib/ajax/service.php (tryb web).
            return new PlatonSiteInfo(_options.UserId.Value, string.Empty, "Platon", null);
        }
        return new PlatonSiteInfo((int)info.UserId, info.FullName, info.SiteName, EmptyToNull(info.Release));
    }

    // ---------- kursy i treści ----------

    public async Task<IReadOnlyList<LmsCourse>> GetCoursesAsync(CancellationToken ct = default)
    {
        const string fn = "core_course_get_enrolled_courses_by_timeline_classification";
        var raw = Parse<RawEnrolledCourses>(await Call(fn, new()
        {
            ["classification"] = "all", ["limit"] = 0, ["offset"] = 0, ["sort"] = "fullname"
        }, ct).ConfigureAwait(false), fn);

        return raw.Courses.Select(c => new LmsCourse(
            Id: c.Id.ToString(CultureInfo.InvariantCulture),
            ShortName: c.ShortName,
            FullName: c.FullName,
            Category: EmptyToNull(c.CourseCategory),
            Progress: c.Progress,
            StartDate: FromUnix(c.StartDate),
            EndDate: FromUnix(c.EndDate),
            ImageUrl: EmptyToNull(c.CourseImage),
            Url: CourseUrl(c.Id),
            // TODO(verify): czy Vistula trzyma kod kursu USOS w idnumber Moodle.
            UsosCourseId: EmptyToNull(c.IdNumber),
            LastAccess: FromUnix(c.LastAccess))).ToList();
    }

    public async Task<IReadOnlyList<LmsSection>> GetCourseContentsAsync(string courseId, CancellationToken ct = default)
    {
        const string fn = "core_course_get_contents";
        var raw = Parse<List<RawSection>>(await Call(fn, new() { ["courseid"] = ParseId(courseId, "kurs") }, ct).ConfigureAwait(false), fn);
        var sections = new List<LmsSection>(raw.Count);
        for (var index = 0; index < raw.Count; index++)
        {
            var s = raw[index];
            var id = s.Id.ToString(CultureInfo.InvariantCulture);
            var name = EmptyToNull(s.Name) ?? $"Sekcja {s.Section ?? index}";
            sections.Add(new LmsSection(
                id, name, StripPluginfilePlaceholders(s.Summary),
                (s.Modules ?? []).Select(m => MapModule(courseId, id, name, m)).ToList()));
        }
        return sections;
    }

    private string FileUrl(string url) => _options.EmbedFileAuth ? _transport.RewriteFileUrl(url) : url;

    private LmsModule MapModule(string courseId, string sectionId, string sectionName, RawModule m)
    {
        var type = ToModuleType(m.ModName);
        var contents = m.Contents ?? [];
        var files = contents
            .Where(c => (c.Type == "file" || c.Type is null) && !string.IsNullOrEmpty(c.FileUrl))
            .Select(c => new LmsFile(
                c.FileName ?? "file",
                FileUrl(c.FileUrl!),
                EmptyToNull(c.MimeType),
                c.FileSize,
                FromUnix(c.TimeModified)))
            .ToList();
        // mod_url: prawdziwy cel to wpis typu "url"; adres modułu tylko do niego przekierowuje.
        var externalUrl = type == LmsModuleType.Url ? contents.FirstOrDefault(c => c.Type == "url")?.FileUrl : null;
        var state = m.CompletionData?.State;
        return new LmsModule(
            Id: m.Id.ToString(CultureInfo.InvariantCulture),
            CourseId: courseId,
            SectionId: sectionId,
            SectionName: sectionName,
            Name: m.Name,
            Type: type,
            RawType: m.ModName,
            Url: EmptyToNull(externalUrl) ?? EmptyToNull(m.Url),
            DescriptionHtml: StripPluginfilePlaceholders(m.Description),
            Files: files,
            // completion 0 = śledzenie ukończenia wyłączone
            Completed: m.Completion == 0 || state is null ? null : state != 0,
            Visible: m.UserVisible ?? m.Visible != 0,
            InstanceId: m.Instance?.ToString(CultureInfo.InvariantCulture));
    }

    // ---------- zadania ----------

    private sealed record SubmissionState(bool Submitted, bool Graded, string? Grade);

    public async Task<IReadOnlyList<LmsAssignment>> GetAssignmentsAsync(CancellationToken ct = default)
    {
        const string fn = "mod_assign_get_assignments";
        var raw = Parse<RawAssignments>(await Call(fn, new(), ct).ConfigureAwait(false), fn);
        var nowSec = _clock.Now.ToUnixTimeSeconds();

        var items = raw.Courses.SelectMany(c => c.Assignments.Select(a => (Course: c, A: a))).ToList();
        // Status zgłoszenia to jedno wywołanie na zadanie: tylko dla najbliższych terminów (przeszłych i przyszłych).
        var nearest = items
            .Where(i => (i.A.DueDate ?? 0) > 0 && i.A.NoSubmissions != 1)
            .OrderBy(i => Math.Abs((i.A.DueDate ?? 0) - nowSec))
            .Take(_maxStatusCalls)
            .ToList();

        var statuses = new System.Collections.Concurrent.ConcurrentDictionary<long, SubmissionState>();
        await MapLimit(nearest, 5, async item =>
        {
            const string sfn = "mod_assign_get_submission_status";
            try
            {
                var s = Parse<RawSubmissionStatus>(
                    await Call(sfn, new() { ["assignid"] = item.A.Id }, ct).ConfigureAwait(false), sfn);
                var graded = s.LastAttempt?.Graded == true || (s.Feedback?.GradedDate ?? 0) > 0;
                var display = string.IsNullOrEmpty(s.Feedback?.GradeForDisplay) ? string.Empty : StripTags(s.Feedback.GradeForDisplay);
                statuses[item.A.Id] = new SubmissionState(
                    s.LastAttempt?.Submission?.Status == "submitted" || graded,
                    graded,
                    graded ? DashToNull(display) : null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Best effort: zostaje „nieznane” (submitted=false). Martwy token wywaliłby już główne wywołanie;
                // accessexception (→ AdapterAuthException) znaczy tu tylko brak uprawnień do statusu tego zadania.
            }
            return 0;
        }).ConfigureAwait(false);

        var output = items.Select(i =>
        {
            statuses.TryGetValue(i.A.Id, out var st);
            return new LmsAssignment(
                Id: i.A.Id.ToString(CultureInfo.InvariantCulture),
                CourseId: i.Course.Id.ToString(CultureInfo.InvariantCulture),
                CourseName: i.Course.FullName,
                Name: i.A.Name,
                DueDate: FromUnix(i.A.DueDate),
                CutoffDate: FromUnix(i.A.CutoffDate),
                Submitted: st?.Submitted ?? false,
                Graded: st?.Graded ?? false,
                Grade: st?.Grade,
                Url: $"{_base}mod/assign/view.php?id={i.A.CmId}");
        });
        // Rosnąco po terminie, zadania bez terminu na końcu.
        return output.OrderBy(a => a.DueDate is null).ThenBy(a => a.DueDate).ToList();
    }

    // ---------- kalendarz ----------

    public async Task<IReadOnlyList<LmsEvent>> GetUpcomingEventsAsync(int days = 14, CancellationToken ct = default)
    {
        const string fn = "core_calendar_get_action_events_by_timesort";
        var from = _clock.Now.ToUnixTimeSeconds();
        var raw = Parse<RawEvents>(await Call(fn, new()
        {
            ["timesortfrom"] = from,
            ["timesortto"] = from + (long)Math.Round(days * 86_400.0, MidpointRounding.AwayFromZero),
            ["limitnum"] = 50,
            ["limittononsuspendedevents"] = true
        }, ct).ConfigureAwait(false), fn);

        return raw.Events.Select(e => new LmsEvent(
            Id: e.Id.ToString(CultureInfo.InvariantCulture),
            CourseId: e.Course is null ? null : e.Course.Id.ToString(CultureInfo.InvariantCulture),
            CourseName: EmptyToNull(e.Course?.FullName),
            Name: e.Name,
            Type: e.EventType ?? "unknown",
            ModuleType: EmptyToNull(e.ModuleName),
            Start: DateTimeOffset.FromUnixTimeSeconds(e.TimeStart),
            End: e.TimeDuration is > 0 ? DateTimeOffset.FromUnixTimeSeconds(e.TimeStart + e.TimeDuration.Value) : null,
            Url: EmptyToNull(e.Action?.Url) ?? EmptyToNull(e.Url),
            ActionName: EmptyToNull(e.Action?.Name))).ToList();
    }

    // ---------- oceny ----------

    public async Task<IReadOnlyList<LmsGradeItem>> GetGradesAsync(string courseId, CancellationToken ct = default)
    {
        const string fn = "gradereport_user_get_grade_items";
        var userId = await GetUserIdAsync(ct).ConfigureAwait(false);
        var raw = Parse<RawGradeItems>(
            await Call(fn, new() { ["courseid"] = ParseId(courseId, "kurs"), ["userid"] = userId }, ct).ConfigureAwait(false), fn);
        return raw.UserGrades.SelectMany(ug => ug.GradeItems.Select(g =>
        {
            var type = g.ItemType ?? "unknown";
            return new LmsGradeItem(
                Id: g.Id.ToString(CultureInfo.InvariantCulture),
                CourseId: ug.CourseId.ToString(CultureInfo.InvariantCulture),
                ItemName: EmptyToNull(g.ItemName) ?? (type == "course" ? "Suma końcowa" : type == "category" ? "Kategoria" : "Element oceny"),
                ItemType: type,
                GradeFormatted: DashToNull(g.GradeFormatted),
                GradeRaw: g.GradeRaw,
                GradeMax: g.GradeMax,
                Percentage: DashToNull(g.PercentageFormatted),
                FeedbackHtml: StripPluginfilePlaceholders(g.Feedback),
                DateGraded: FromUnix(g.GradeDateGraded));
        })).ToList();
    }

    // ---------- ogłoszenia ----------

    public async Task<IReadOnlyList<Announcement>> GetAnnouncementsAsync(int limit = 20, CancellationToken ct = default)
    {
        const string forumsFn = "mod_forum_get_forums_by_courses";
        var forums = Parse<List<RawForum>>(
            await Call(forumsFn, new() { ["courseids"] = _newsCourseIds }, ct).ConfigureAwait(false), forumsFn)
            .Where(f => f.Type == "news").ToList();

        var perForum = await MapLimit(forums, 4, async forum =>
        {
            try
            {
                const string fn = "mod_forum_get_forum_discussions";
                var d = Parse<RawDiscussions>(await Call(fn, new()
                {
                    ["forumid"] = forum.Id, ["sortorder"] = -1, ["page"] = 0, ["perpage"] = limit
                }, ct).ConfigureAwait(false), fn);
                return d.Discussions.Select(x =>
                {
                    var discussionId = x.Discussion ?? x.Id;
                    return new Announcement(
                        Id: $"platon:{discussionId}",
                        Source: SourceSystem.Platon,
                        Title: EmptyToNull(x.Subject) ?? EmptyToNull(x.Name) ?? "(bez tytułu)",
                        BodyHtml: StripPluginfilePlaceholders(x.Message) ?? string.Empty,
                        PublishedAt: FromUnix(x.Created ?? x.TimeModified ?? x.Modified) ?? DateTimeOffset.UnixEpoch,
                        Author: EmptyToNull(x.UserFullName),
                        Category: forum.Name,
                        Url: $"{_base}mod/forum/discuss.php?d={discussionId}");
                }).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return new List<Announcement>(); // jedno niedostępne forum nie może ukryć pozostałych
            }
        }).ConfigureAwait(false);

        return perForum.SelectMany(x => x).OrderByDescending(a => a.PublishedAt).Take(limit).ToList();
    }

    // ---------- pliki ----------

    public Task<AuthenticatedDownload> GetFileDownloadAsync(string url, CancellationToken ct = default)
    {
        // Nigdy nie oddajemy poświadczeń obcemu hostowi.
        if (!MoodleUrls.IsSameOrigin(url, _base))
            throw new AdapterUnavailableException("Odmowa pobrania pliku z innego źródła niż Platon") { IsTransient = false };
        return Task.FromResult(new AuthenticatedDownload(new Uri(_transport.RewriteFileUrl(url), UriKind.Absolute), _transport.FileHeaders()));
    }

    // ---------- testy (mod_quiz) ----------
    // TODO(verify): funkcje mod_quiz_* przez lib/ajax/service.php (tryb web) — zależy od flagi 'ajax' w definicji usługi.

    public async Task<IReadOnlyList<LmsQuiz>> GetQuizzesAsync(IReadOnlyList<string>? courseIds = null, CancellationToken ct = default)
    {
        var raw = await FetchQuizzesAsync(courseIds, ct).ConfigureAwait(false);
        return raw.Select(q => new LmsQuiz(
            Id: q.Id.ToString(CultureInfo.InvariantCulture),
            CourseModuleId: q.CourseModule.ToString(CultureInfo.InvariantCulture),
            CourseId: q.Course.ToString(CultureInfo.InvariantCulture),
            Name: q.Name,
            IntroHtml: StripPluginfilePlaceholders(q.Intro),
            TimeOpen: FromUnix(q.TimeOpen),
            TimeClose: FromUnix(q.TimeClose),
            TimeLimitSeconds: q.TimeLimit is > 0 ? (int)q.TimeLimit.Value : null,
            Attempts: (int)(q.Attempts ?? 0),
            AllowOfflineAttempts: JsonLoose.ToBool(q.AllowOfflineAttempts),
            HasQuestions: JsonLoose.ToBool(q.HasQuestions),
            Url: $"{_base}mod/quiz/view.php?id={q.CourseModule}")).ToList();
    }

    private async Task<IReadOnlyList<RawQuiz>> FetchQuizzesAsync(IReadOnlyList<string>? courseIds, CancellationToken ct)
    {
        const string fn = "mod_quiz_get_quizzes_by_courses";
        var args = new Dictionary<string, object?>();
        // Bez courseids Moodle zwraca quizy ze wszystkich kursów użytkownika.
        if (courseIds is { Count: > 0 }) args["courseids"] = courseIds.Select(c => ParseId(c, "kurs")).ToList();
        return Parse<RawQuizzes>(await Call(fn, args, ct).ConfigureAwait(false), fn).Quizzes;
    }

    public async Task<IReadOnlyList<QuizAttempt>> GetQuizAttemptsAsync(string quizId, CancellationToken ct = default)
    {
        const string fn = "mod_quiz_get_user_attempts";
        var raw = Parse<RawQuizAttempts>(await Call(fn, new()
        {
            ["quizid"] = ParseId(quizId, "quiz"), ["status"] = "all", ["includepreviews"] = false
        }, ct).ConfigureAwait(false), fn);
        return raw.Attempts.Select(MapAttempt).ToList();
    }

    public async Task<QuizAttempt> StartQuizAttemptAsync(
        string quizId, IReadOnlyList<QuizAnswer>? preflight = null, bool forceNew = false, CancellationToken ct = default)
    {
        const string fn = "mod_quiz_start_attempt";
        var args = new Dictionary<string, object?> { ["quizid"] = ParseId(quizId, "quiz") };
        AddNameValues(args, "preflightdata", preflight);
        args["forcenew"] = forceNew;
        var raw = Parse<RawStartAttempt>(await Call(fn, args, ct).ConfigureAwait(false), fn);
        return MapAttempt(raw.Attempt);
    }

    public async Task<QuizAttemptData> GetQuizAttemptDataAsync(
        string attemptId, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default)
    {
        const string fn = "mod_quiz_get_attempt_data";
        var attemptNumber = ParseId(attemptId, "podejście");
        var questions = new List<QuizQuestion>();
        var visited = new HashSet<long>();
        RawQuizAttempt? attempt = null;
        long page = 0;
        while (visited.Count < MaxQuizPages && visited.Add(page))
        {
            var args = new Dictionary<string, object?> { ["attemptid"] = attemptNumber, ["page"] = page };
            AddNameValues(args, "preflightdata", preflight);
            var raw = Parse<RawAttemptData>(await Call(fn, args, ct).ConfigureAwait(false), fn);
            attempt ??= raw.Attempt;
            questions.AddRange((raw.Questions ?? []).Select(MapQuestion));
            var next = raw.NextPage ?? -1;
            if (next < 0) break;
            page = next;
        }
        return new QuizAttemptData(MapAttempt(attempt!), questions, preflight ?? []);
    }

    public async Task SaveQuizAttemptAsync(
        string attemptId, IReadOnlyList<QuizAnswer> answers, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default)
    {
        const string fn = "mod_quiz_save_attempt";
        if (answers.Count == 0) return; // TODO(verify): 'data' jest wymagany przez Moodle, więc puste zapisy pomijamy.
        var args = new Dictionary<string, object?> { ["attemptid"] = ParseId(attemptId, "podejście") };
        AddNameValues(args, "data", answers);
        AddNameValues(args, "preflightdata", preflight);
        var raw = Parse<RawSaveResult>(await Call(fn, args, ct).ConfigureAwait(false), fn);
        if (raw.Status is { } status && status.ValueKind != JsonValueKind.Null && !JsonLoose.ToBool(status))
            throw new AdapterUnavailableException("Platon nie zapisał odpowiedzi testu") { IsTransient = false };
    }

    public async Task<QuizAttemptState> ProcessQuizAttemptAsync(
        string attemptId, IReadOnlyList<QuizAnswer> answers, bool finish, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default)
    {
        const string fn = "mod_quiz_process_attempt";
        var args = new Dictionary<string, object?> { ["attemptid"] = ParseId(attemptId, "podejście") };
        AddNameValues(args, "data", answers);
        args["finishattempt"] = finish;
        args["timeup"] = false;
        AddNameValues(args, "preflightdata", preflight);
        var raw = Parse<RawProcessResult>(await Call(fn, args, ct).ConfigureAwait(false), fn);
        return ParseAttemptState(raw.State);
    }

    public async Task<QuizAttemptReview> GetQuizAttemptReviewAsync(string attemptId, CancellationToken ct = default)
    {
        const string fn = "mod_quiz_get_attempt_review";
        var raw = Parse<RawAttemptReview>(await Call(fn, new()
        {
            ["attemptid"] = ParseId(attemptId, "podejście"), ["page"] = -1
        }, ct).ConfigureAwait(false), fn);

        // Odpowiedź review nie zawiera maksymalnej oceny quizu — bierzemy ją z listy quizów (best effort).
        // TODO(verify): pole 'grade' quizu jest maksymalną oceną quizu (skala), a 'grade' w review oceną podejścia w tej skali.
        double? maxGrade = null;
        try
        {
            var quizzes = await FetchQuizzesAsync(null, ct).ConfigureAwait(false);
            maxGrade = JsonLoose.ToDouble(quizzes.FirstOrDefault(q => q.Id == raw.Attempt.Quiz)?.Grade);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
        }

        return new QuizAttemptReview(
            MapAttempt(raw.Attempt),
            JsonLoose.ToDouble(raw.Grade),
            maxGrade,
            (raw.Questions ?? []).Select(MapQuestion).ToList());
    }

    private static void AddNameValues(Dictionary<string, object?> args, string key, IReadOnlyList<QuizAnswer>? values)
    {
        if (values is not { Count: > 0 }) return;
        args[key] = values.Select(v => (object?)new Dictionary<string, object?> { ["name"] = v.Name, ["value"] = v.Value }).ToList();
    }

    /// <summary>Stany podejścia Moodle: inprogress / overdue / finished / abandoned.</summary>
    public static QuizAttemptState ParseAttemptState(string? state) => state?.Trim().ToLowerInvariant() switch
    {
        "inprogress" => QuizAttemptState.InProgress,
        "overdue" => QuizAttemptState.Overdue,
        "finished" => QuizAttemptState.Finished,
        "abandoned" => QuizAttemptState.Abandoned,
        _ => throw new AdapterUnavailableException($"Nieznany stan podejścia Moodle: {state}") { IsTransient = false }
    };

    private static QuizAttempt MapAttempt(RawQuizAttempt a) => new(
        Id: a.Id.ToString(CultureInfo.InvariantCulture),
        QuizId: a.Quiz.ToString(CultureInfo.InvariantCulture),
        Attempt: (int)(a.Attempt ?? 0),
        State: ParseAttemptState(a.State),
        TimeStart: FromUnix(a.TimeStart),
        TimeFinish: FromUnix(a.TimeFinish),
        SumGrades: JsonLoose.ToDouble(a.SumGrades),
        CurrentPage: a.CurrentPage is { } p ? (int)p : null);

    private static QuizQuestion MapQuestion(RawQuestion q) => new(
        Slot: (int)q.Slot,
        Page: (int)(q.Page ?? 0),
        Type: q.Type ?? string.Empty,
        Html: q.Html ?? string.Empty,
        SequenceCheck: JsonLoose.ToText(q.SequenceCheck),
        State: q.State ?? string.Empty,
        Status: EmptyToNull(q.Status),
        Mark: JsonLoose.ToDouble(q.Mark),
        MaxMark: JsonLoose.ToDouble(q.MaxMark),
        FieldNames: ExtractFieldNames(q.Html));
}
