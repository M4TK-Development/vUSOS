// USOS adapter, mode "api": OAuth 1.0a USOS API (services/...), requests signed by an IUsosRequestSigner.
//
// Method names and fields follow the public USOS API reference. Nothing here was run against the Vistula
// installation (no network from the build machine) - see the TODO(verify) markers, esp. for payments,
// USOSmail (mailclient), news and the grades/terms2 nesting.
using System.Globalization;
using System.Text.Json;
using Vusos.Core.Domain;
using Vusos.Core.Platform;

namespace Vusos.Core.Adapters.Usos;

public sealed class UsosApiAdapter(IUsosRequestSigner signer, UsosApiCredentials creds, IClock clock) : IUsosAdapter
{
    public const string ProfileFields =
        "id|first_name|last_name|email|student_number|photo_urls[200x200]|" +
        "student_programmes[id|programme[id|description|level_of_studies|mode_of_studies]|status]"; // TODO(verify): `stage`/`status` subfields
    public const string CourseFields =
        "course_editions[course_id|course_name|term_id|user_groups[course_unit_id|class_type|group_number|lecturers[first_name|last_name]]]";
    public const string TimetableFields =
        "type|start_time|end_time|name|url|unit_id|course_id|course_name|classtype_name|group_number|room_number|building_name|lecturer_ids";
    public const string GradeFields =
        "value_symbol|value_description|passes|counts_into_average|exam_session_number|date_modified|modification_author|comment"; // TODO(verify): does terms2 accept `fields`?
    public const string PaymentFields =
        "id|type|description|total_amount|saldo_amount|state|payment_deadline|interest|account_number"; // TODO(verify): names, esp. total_amount/saldo_amount
    public const string TermFields = "id|name|start_date|end_date|finish_date|is_active";
    // TODO(verify): the whole USOSmail ("mailclient") module - method names, params and fields are a best guess.
    public const string MailListFields = "id|subject|sender|recipients|date|is_read|preview";
    public const string MailMessageFields = "id|subject|sender|recipients|date|is_read|body|attachments[name|url|size]";
    // TODO(verify): news module name (news/search vs news/articles) and fields.
    public const string NewsFields = "id|title|content|publication_date|author|category|url";

    private const int MaxTimetableDays = 93;
    private const int TimetableParallelism = 4;
    private static readonly TimeSpan CoursesTtl = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Dictionary<bool, (Task<JsonElement> Task, DateTimeOffset At)> _courses = [];
    private readonly Dictionary<string, string> _lecturers = [];

    private DateOnly Today => WarsawTime.DateOf(clock.Now);

    // ---------- transport ----------

    private static Dictionary<string, string> Params(params (string Key, object? Value)[] values)
    {
        var d = new Dictionary<string, string>();
        foreach (var (key, value) in values)
        {
            switch (value)
            {
                case null: break;
                case string s: d[key] = s; break;
                case bool b: d[key] = b ? "true" : "false"; break;
                case IEnumerable<string> list: d[key] = string.Join('|', list); break; // USOS list convention
                case IFormattable f: d[key] = f.ToString(null, CultureInfo.InvariantCulture); break;
                default: d[key] = value.ToString() ?? ""; break;
            }
        }
        return d;
    }

    /// <summary>Signed request. 401 -> AdapterAuthException; other non-2xx -> UsosHttpException; transport -> AdapterUnavailableException.</summary>
    private async Task<JsonElement> CallAsync(string path, Dictionary<string, string> parameters, HttpMethod? method = null, CancellationToken ct = default)
    {
        method ??= HttpMethod.Get;
        var (status, text) = await UsosHttp.SendForTextAsync(
            c => signer.SendAsync(method, path, parameters, creds, c),
            $"USOS API niedostępne (services/{path})", UsosHttp.DefaultTimeout, ct).ConfigureAwait(false);
        var code = (int)status;
        if (code == 401)
        {
            var msg = UsosHttp.ExtractMessage(text);
            throw new AdapterAuthException($"USOS: sesja wygasła lub token cofnięty ({(msg.Length > 0 ? msg : "401")})");
        }
        if (code is < 200 or >= 300)
            throw new UsosHttpException($"USOS API services/{path}: HTTP {code} {UsosHttp.ExtractMessage(text)}".Trim(), code, text, path);
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new AdapterUnavailableException($"USOS API services/{path}: odpowiedź nie jest poprawnym JSON-em") { IsTransient = false };
        }
    }

    private static List<JsonElement> AsList(JsonElement raw, params string[] keys)
    {
        if (raw.ValueKind == JsonValueKind.Array) return [.. raw.EnumerateArray()];
        if (raw.ValueKind == JsonValueKind.Object)
            foreach (var k in keys)
                if (raw.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array)
                    return [.. v.EnumerateArray()];
        return [];
    }

    /// <summary>courses/user is needed by several methods (names, class types, term ids): memoised briefly, failures are not cached.</summary>
    private Task<JsonElement> CoursesUserAsync(bool activeOnly, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = clock.Now;
            if (_courses.TryGetValue(activeOnly, out var cached) && now - cached.At < CoursesTtl && !cached.Task.IsFaulted && !cached.Task.IsCanceled)
                return cached.Task;
            var task = CallAsync("courses/user", Params(("fields", CourseFields), ("active_terms_only", activeOnly)), null, ct);
            _courses[activeOnly] = (task, now);
            return task;
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveLecturersAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        List<string> missing;
        lock (_gate) missing = ids.Where(id => !_lecturers.ContainsKey(id)).ToList();
        for (var i = 0; i < missing.Count; i += 50)
        {
            var chunk = missing.Skip(i).Take(50).ToList();
            try
            {
                var res = await CallAsync("users/users", Params(("user_ids", chunk), ("fields", "id|first_name|last_name")), null, ct).ConfigureAwait(false);
                if (res.ValueKind == JsonValueKind.Object)
                {
                    foreach (var u in res.EnumerateObject())
                    {
                        if (u.Value.ValueKind != JsonValueKind.Object) continue;
                        var name = string.Join(' ', new[] { Mappers.StringProp(u.Value, "first_name"), Mappers.StringProp(u.Value, "last_name") }
                            .Where(s => !string.IsNullOrEmpty(s)));
                        if (name.Length > 0) lock (_gate) _lecturers[u.Name] = name;
                    }
                }
            }
            catch (AdapterUnavailableException)
            {
                // lecturers are decoration - the timetable is still useful without names; auth errors still propagate
            }
        }
        lock (_gate) return new Dictionary<string, string>(_lecturers);
    }

    private async Task<JsonElement> MailAsync(string path, Dictionary<string, string> parameters, HttpMethod? method, CancellationToken ct)
    {
        try { return await CallAsync(path, parameters, method, ct).ConfigureAwait(false); }
        catch (UsosHttpException ex) when (ex.IsMethodNotFound)
        {
            throw new AdapterNotSupportedException("USOSmail API niedostępne");
        }
    }

    /// <summary>400/404 = method or field unknown on this installation -> degrade gracefully.</summary>
    private static async Task<T> OptionalAsync<T>(Func<Task<T>> fn, T fallback)
    {
        try { return await fn().ConfigureAwait(false); }
        catch (UsosHttpException ex) when (ex.Status is 400 or 404) { return fallback; }
    }

    // ---------- IUsosAdapter ----------

    public async Task<StudentProfile> GetProfileAsync(CancellationToken ct = default) =>
        Mappers.MapProfile(await CallAsync("users/user", Params(("fields", ProfileFields)), null, ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<Term>> GetTermsAsync(CancellationToken ct = default)
    {
        var today = Today;
        // Primary: terms attached to the user's course editions (all terms the student has ever had classes in).
        var cu = await CoursesUserAsync(false, ct).ConfigureAwait(false);
        var fromCourses = Mappers.Prop(cu, "terms") is { } t ? Mappers.MapTerms(t, today) : [];
        if (fromCourses.Count > 0) return fromCourses;

        // Fallback: search by year. TODO(verify): terms/search arguments (query, fields) and result shape.
        var found = new List<Term>();
        foreach (var q in new[] { (today.Year - 1).ToString(CultureInfo.InvariantCulture), today.Year.ToString(CultureInfo.InvariantCulture) })
        {
            var res = await OptionalAsync(() => CallAsync("terms/search", Params(("query", q), ("fields", TermFields)), null, ct), default).ConfigureAwait(false);
            found.AddRange(Mappers.MapTerms(AsList(res, "terms", "items"), today));
        }
        return DedupeById(found, x => x.Id).OrderByDescending(x => x.StartDate).ToList();
    }

    public async Task<IReadOnlyList<CourseUnit>> GetCoursesAsync(string? termId = null, CancellationToken ct = default) =>
        Mappers.MapCourseUnits(await CoursesUserAsync(string.IsNullOrEmpty(termId), ct).ConfigureAwait(false), termId);

    public async Task<IReadOnlyList<TimetableEntry>> GetTimetableAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (to < from) return [];
        var end = WarsawTime.DiffDays(from, to) >= MaxTimetableDays ? from.AddDays(MaxTimetableDays - 1) : to;

        var chunks = new List<(DateOnly Start, int Days)>();
        for (var s = from; s <= end; s = s.AddDays(7)) chunks.Add((s, Math.Min(7, WarsawTime.DiffDays(s, end) + 1)));

        var all = new List<JsonElement>();
        for (var i = 0; i < chunks.Count; i += TimetableParallelism)
        {
            var batch = chunks.Skip(i).Take(TimetableParallelism);
            var results = await Task.WhenAll(batch.Select(c => CallAsync(
                "tt/user", Params(("start", WarsawTime.Format(c.Start)), ("days", c.Days), ("fields", TimetableFields)), null, ct))).ConfigureAwait(false);
            foreach (var r in results) all.AddRange(AsList(r, "activities"));
        }
        var names = await ResolveLecturersAsync(Mappers.CollectLecturerIds(all), ct).ConfigureAwait(false);
        var entries = Mappers.MapActivities(all, names);
        return DedupeById(entries, e => e.Id).OrderBy(e => e.Start).ToList();
    }

    public async Task<IReadOnlyList<Grade>> GetGradesAsync(IReadOnlyList<string>? termIds = null, CancellationToken ct = default)
    {
        var courses = await CoursesUserAsync(false, ct).ConfigureAwait(false);
        var courseNames = new Dictionary<string, LangText>();
        var unitClassTypes = new Dictionary<string, string>();
        var knownTerms = new List<string>();
        foreach (var ed in Mappers.ExtractCourseEditions(courses))
        {
            courseNames[ed.CourseId!] = Mappers.ToLangText(ed.CourseName, ed.CourseId!);
            if (!string.IsNullOrEmpty(ed.TermId) && !knownTerms.Contains(ed.TermId)) knownTerms.Add(ed.TermId);
            foreach (var g in ed.UserGroups ?? []) unitClassTypes[g.CourseUnitId!] = Mappers.ToLangText(g.ClassType).Pl;
        }
        IReadOnlyList<string> ids = termIds is { Count: > 0 } ? termIds : knownTerms;
        if (ids.Count == 0) return [];

        JsonElement raw;
        try
        {
            raw = await CallAsync("grades/terms2", Params(("term_ids", ids), ("fields", GradeFields)), null, ct).ConfigureAwait(false);
        }
        catch (UsosHttpException ex) when (ex.Status == 400 && !ex.IsMethodNotFound)
        {
            // TODO(verify): if terms2 rejects `fields` (HTTP 400) retry with the defaults.
            raw = await CallAsync("grades/terms2", Params(("term_ids", ids)), null, ct).ConfigureAwait(false);
        }
        return Mappers.MapGrades(raw, new GradeMapContext(courseNames, unitClassTypes));
    }

    public async Task<IReadOnlyList<Payment>> GetPaymentsAsync(CancellationToken ct = default)
    {
        var raw = await CallAsync("payments/user_payments", Params(("fields", PaymentFields)), null, ct).ConfigureAwait(false);
        return Mappers.MapPayments(AsList(raw, "payments", "items"), Today);
    }

    public async Task<IReadOnlyList<MessageThread>> ListMessagesAsync(int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        var raw = await MailAsync("mailclient/messages",
            Params(("folder", "inbox"), ("limit", limit), ("offset", offset), ("fields", MailListFields)), null, ct).ConfigureAwait(false);
        return AsList(raw, "messages", "items").Select(Mappers.MapMessageThread).OfType<MessageThread>().ToList();
    }

    public async Task<Message> GetMessageAsync(string id, CancellationToken ct = default) =>
        Mappers.MapMessage(await MailAsync("mailclient/message",
            Params(("message_id", id), ("fields", MailMessageFields)), null, ct).ConfigureAwait(false));

    public async Task<string> SendMessageAsync(IReadOnlyList<string> to, string subject, string body, CancellationToken ct = default)
    {
        // TODO(verify): recipients may need to be USOS user ids rather than e-mail addresses.
        var res = await MailAsync("mailclient/send",
            Params(("recipients", to), ("subject", subject), ("body", body)), HttpMethod.Post, ct).ConfigureAwait(false);
        var id = Mappers.Prop(res, "id") ?? Mappers.Prop(res, "message_id");
        return id is { } v ? Mappers.IdOf(v) ?? "" : "";
    }

    public Task<IReadOnlyList<Contact>> GetDeanOfficeContactsAsync(CancellationToken ct = default) =>
        // TODO(verify): no dedicated "dean's office" method exists in USOS API; we expose the faculty card
        // (phone + address) of the user's programme(s). Returns [] when anything is missing.
        OptionalAsync<IReadOnlyList<Contact>>(async () =>
        {
            var user = await CallAsync("users/user", Params(("fields", "student_programmes[programme[faculty[id|name]]]")), null, ct).ConfigureAwait(false);
            var facIds = new List<string>();
            if (Mappers.Prop(user, "student_programmes") is { } progs)
            {
                foreach (var p in Mappers.Items(progs))
                {
                    if (Mappers.Prop(p, "programme") is { } prog
                        && Mappers.Prop(prog, "faculty") is { } fac
                        && Mappers.Prop(fac, "id") is { } idEl
                        && Mappers.IdOf(idEl) is { } id
                        && !facIds.Contains(id))
                        facIds.Add(id);
                }
            }
            var contacts = new List<Contact>();
            foreach (var facId in facIds)
            {
                var raw = await CallAsync("fac/faculty",
                    Params(("fac_id", facId), ("fields", "id|name|postal_address|phone_numbers|homepage_url")), null, ct).ConfigureAwait(false);
                contacts.AddRange(Mappers.MapFacultyContacts(raw));
            }
            return contacts;
        }, []);

    public Task<IReadOnlyList<Announcement>> GetAnnouncementsAsync(int limit = 20, CancellationToken ct = default) =>
        OptionalAsync<IReadOnlyList<Announcement>>(async () =>
        {
            var raw = await CallAsync("news/search", Params(("fields", NewsFields), ("limit", limit)), null, ct).ConfigureAwait(false);
            return Mappers.MapNews(AsList(raw, "articles", "items", "news")).Take(limit).ToList();
        }, []);

    /// <summary>Like <c>new Map(items.map(x => [key(x), x]))</c>: the last duplicate wins, first-seen order is kept.</summary>
    internal static List<T> DedupeById<T>(IEnumerable<T> items, Func<T, string> key)
    {
        var index = new Dictionary<string, int>();
        var list = new List<T>();
        foreach (var item in items)
        {
            var k = key(item);
            if (index.TryGetValue(k, out var i)) list[i] = item;
            else { index[k] = list.Count; list.Add(item); }
        }
        return list;
    }
}
