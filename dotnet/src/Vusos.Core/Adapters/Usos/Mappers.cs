// Pure mappers: raw USOS API JSON -> Vusos.Core.Domain types.
// Input is JsonElement and validated leniently via the DTOs in Raw.cs; entries that do not deserialize are skipped
// (lists) or reported as AdapterUnavailableException (single-object responses).
//
// TODO(verify): every raw field name below is based on the public USOS API docs and has not been checked against the
// Vistula installation. Search for "TODO(verify)" in this folder for the full list.
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vusos.Core.Domain;

namespace Vusos.Core.Adapters.Usos;

/// <summary>Context for <see cref="Mappers.MapGrades"/>: names and class types taken from courses/user.</summary>
public sealed record GradeMapContext(
    IReadOnlyDictionary<string, LangText>? CourseNames = null,
    IReadOnlyDictionary<string, string>? UnitClassTypes = null);

public sealed record GradeInfo(bool? Passes, LangText? Description);

public static partial class Mappers
{
    // ---------- generic helpers ----------

    /// <summary>Elements of a JSON array, else nothing.</summary>
    public static IEnumerable<JsonElement> Items(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];

    public static bool IsRecord(JsonElement e) => e.ValueKind == JsonValueKind.Object;

    /// <summary>Property of an object, or null when missing / JSON null / not an object.</summary>
    public static JsonElement? Prop(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v
            : null;

    public static string? StringProp(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    /// <summary>String or number as string (ids), else null.</summary>
    public static string? IdOf(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetRawText(),
        _ => null,
    };

    /// <summary>"text" | {pl,en} | null -> LangText.</summary>
    public static LangText ToLangText(JsonElement? v, string fallback = "")
    {
        if (v is { ValueKind: JsonValueKind.String } s) return new LangText(s.GetString() ?? "");
        if (v is { ValueKind: JsonValueKind.Object } o)
        {
            var pl = NonEmpty(StringProp(o, "pl"));
            var en = NonEmpty(StringProp(o, "en"));
            return new LangText(pl ?? en ?? fallback, en);
        }
        return new LangText(fallback);

        static string? NonEmpty(string? x) => string.IsNullOrEmpty(x) ? null : x;
    }

    public static decimal? ToNumber(JsonElement? v)
    {
        if (v is null) return null;
        switch (v.Value.ValueKind)
        {
            case JsonValueKind.Number:
                if (v.Value.TryGetDecimal(out var d)) return d;
                var dbl = v.Value.GetDouble();
                return double.IsFinite(dbl) && Math.Abs(dbl) < 7.9e27 ? (decimal)dbl : null;
            case JsonValueKind.String:
                return ToNumber(v.Value.GetString());
            default:
                return null;
        }
    }

    /// <summary>"1 250,00" / "4.5" -> number; blank or unparseable -> null.</summary>
    public static decimal? ToNumber(string? s)
    {
        if (s is null || s.Trim().Length == 0) return null;
        var t = WhitespaceRegex().Replace(s.Trim(), "");
        var i = t.IndexOf(',');
        if (i >= 0) t = string.Concat(t.AsSpan(0, i), ".", t.AsSpan(i + 1));
        return decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>Whole number from a "number-like" value, truncated.</summary>
    public static int? ToInt(JsonElement? v) => ToNumber(v) is { } n ? (int)Math.Truncate(n) : null;

    /// <summary>Number without trailing zeros ("1", "1.5"), like JavaScript's String(number).</summary>
    public static string NumberText(decimal n) =>
        (n / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    private static string FullName(string? first, string? last) =>
        string.Join(' ', new[] { first, last }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>Person given as string or {first_name,last_name}.</summary>
    public static string? PersonName(JsonElement? v)
    {
        if (v is { ValueKind: JsonValueKind.String } s)
        {
            var t = s.GetString()?.Trim();
            return string.IsNullOrEmpty(t) ? null : t;
        }
        if (v is { ValueKind: JsonValueKind.Object } o)
        {
            var n = FullName(StringProp(o, "first_name"), StringProp(o, "last_name"));
            return n.Length == 0 ? null : n;
        }
        return null;
    }

    /// <summary>USOS timestamp ("YYYY-MM-DD HH:MM:SS", Warsaw) or date -> instant with offset; unparseable -> null.</summary>
    public static DateTimeOffset? UsosDateTime(string? v) => string.IsNullOrEmpty(v) ? null : WarsawTime.ParseLocal(v);

    private static string? ValidUrl(string? v) =>
        !string.IsNullOrEmpty(v) && Uri.TryCreate(v, UriKind.Absolute, out _) ? v : null;

    private static string? ValidEmail(string? v) => !string.IsNullOrEmpty(v) && EmailRegex().IsMatch(v) ? v : null;

    /// <summary>Entries of an object, or of an array (keys "1", "2", ...), else nothing.</summary>
    private static IEnumerable<(string Key, JsonElement Value)> EntriesOf(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Array)
        {
            var i = 1;
            foreach (var x in v.EnumerateArray()) yield return ((i++).ToString(CultureInfo.InvariantCulture), x);
        }
        else if (v.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in v.EnumerateObject()) yield return (p.Name, p.Value);
        }
    }

    [GeneratedRegex(@"\s+")] private static partial Regex WhitespaceRegex();
    [GeneratedRegex(@"^[^\s@]+@[^\s@]+\.[^\s@]+$")] private static partial Regex EmailRegex();
    [GeneratedRegex(@"[^a-z0-9]+")] private static partial Regex NonSlugRegex();
    [GeneratedRegex(@"^(y|yes|t|true|1|tak)$", RegexOptions.IgnoreCase)] private static partial Regex TruthyRegex();
    [GeneratedRegex(@"^\s*([2-5](?:[.,]\d)?)\s*$")] private static partial Regex NumericGradeRegex();
    [GeneratedRegex(@"cancel|anul|storn|umorz")] private static partial Regex CancelledRegex();
    [GeneratedRegex(@"^paid$|zap[łl]acon")] private static partial Regex PaidRegex();
    [GeneratedRegex(@"<[^>]*>")] private static partial Regex TagRegex();
    [GeneratedRegex(@"^(zal\.?|zaliczone|zaliczony|zaliczenie)$", RegexOptions.IgnoreCase)] private static partial Regex ZalRegex();
    [GeneratedRegex(@"^(nzal\.?|niezaliczone|niezaliczony)$", RegexOptions.IgnoreCase)] private static partial Regex NzalRegex();

    private static AdapterUnavailableException Unexpected(string what) =>
        new($"USOS: nieoczekiwany format odpowiedzi {what}") { IsTransient = false };

    // ---------- profile ----------

    public static StudentProfile MapProfile(JsonElement raw)
    {
        var u = RawReader.User(raw) ?? throw Unexpected("users/user");
        string? photo = null;
        if (u.PhotoUrls is { } urls)
        {
            if (urls.TryGetValue("200x200", out var p200) && p200.ValueKind == JsonValueKind.String)
                photo = p200.GetString();
            else
                photo = urls.Values.Where(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(x.GetString()))
                    .Select(x => x.GetString()).FirstOrDefault();
        }

        var programmes = (u.StudentProgrammes ?? []).Select(sp =>
        {
            var levels = new[] { ToLangText(sp.Programme?.LevelOfStudies).Pl, ToLangText(sp.Programme?.ModeOfStudies).Pl }
                .Where(s => s != "").ToList();
            return new Programme(
                sp.Id!,
                ToLangText(sp.Programme?.Description, sp.Programme?.Id ?? sp.Id!),
                sp.Status,
                // TODO(verify): there may be no `stage` on student_programme; we fall back to level/mode of the programme.
                sp.Stage ?? (levels.Count > 0 ? string.Join(", ", levels) : null));
        }).ToList();

        return new StudentProfile(
            u.Id!,
            u.FirstName ?? "",
            u.LastName ?? "",
            ValidEmail(u.Email),
            u.StudentNumber,
            ValidUrl(photo),
            programmes);
    }

    // ---------- terms ----------

    public static Term? MapTerm(JsonElement raw, DateOnly today)
    {
        var t = RawReader.Term(raw);
        if (t is null) return null;
        var start = WarsawTime.ToDateOnly(t.StartDate);
        var end = WarsawTime.ToDateOnly(t.EndDate);
        if (start is null || end is null) return null;
        // `finish_date` is the last day of the term including exam sessions; prefer it for "is the term running".
        var finish = WarsawTime.ToDateOnly(t.FinishDate) ?? end.Value;
        return new Term(t.Id!, ToLangText(t.Name, t.Id!), start.Value, end.Value, start.Value <= today && today <= finish);
    }

    public static IReadOnlyList<Term> MapTerms(IEnumerable<JsonElement> raw, DateOnly today) =>
        raw.Select(x => MapTerm(x, today)).OfType<Term>().OrderByDescending(t => t.StartDate).ToList();

    public static IReadOnlyList<Term> MapTerms(JsonElement raw, DateOnly today) => MapTerms(Items(raw), today);

    // ---------- courses ----------

    /// <summary>Flattens courses/user `course_editions` (dict term_id -> list, or flat list) into typed editions.</summary>
    public static List<RawCourseEdition> ExtractCourseEditions(JsonElement raw, string? termId = null)
    {
        if (raw.ValueKind != JsonValueKind.Object) throw Unexpected("courses/user");
        var output = new List<RawCourseEdition>();
        void Push(JsonElement item, string? keyTerm)
        {
            var e = RawReader.CourseEdition(item);
            if (e is not null) output.Add(e with { TermId = e.TermId ?? keyTerm });
        }

        if (Prop(raw, "course_editions") is { } ce)
        {
            if (ce.ValueKind == JsonValueKind.Array)
                foreach (var x in ce.EnumerateArray()) Push(x, null);
            else if (ce.ValueKind == JsonValueKind.Object)
                foreach (var p in ce.EnumerateObject())
                    foreach (var x in Items(p.Value)) Push(x, p.Name);
        }
        return string.IsNullOrEmpty(termId) ? output : output.Where(e => e.TermId == termId).ToList();
    }

    public static IReadOnlyList<CourseUnit> MapCourseUnits(JsonElement raw, string? termId = null)
    {
        var units = new List<CourseUnit>();
        foreach (var ed in ExtractCourseEditions(raw, termId))
        {
            var term = ed.TermId ?? "";
            var courseName = ToLangText(ed.CourseName, ed.CourseId!);
            var groups = ed.UserGroups ?? [];
            if (groups.Count == 0)
            {
                units.Add(new CourseUnit($"{ed.CourseId}@{term}", ed.CourseId!, courseName, "", term, null, [], null, null));
                continue;
            }
            foreach (var g in groups)
            {
                units.Add(new CourseUnit(
                    g.CourseUnitId!,
                    ed.CourseId!,
                    courseName,
                    ToLangText(g.ClassType).Pl,
                    term,
                    ToInt(g.GroupNumber),
                    (g.Lecturers ?? []).Select(l => FullName(l.FirstName, l.LastName)).Where(s => s != "").ToList(),
                    // TODO(verify): ECTS is not part of courses/user; would need courses/course_edition2 (credit_value?).
                    null,
                    null));
            }
        }
        return units;
    }

    // ---------- timetable ----------

    public static IReadOnlyList<string> CollectLecturerIds(IEnumerable<JsonElement> rawList)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>();
        foreach (var raw in rawList)
        {
            var a = RawReader.Activity(raw);
            if (a?.LecturerIds is not { } arr) continue;
            foreach (var id in Items(arr).Select(IdOf))
                if (id is not null && seen.Add(id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>Maps tt/user activities. <paramref name="lecturerNames"/> maps user id -> "First Last" (resolved via users/users).</summary>
    public static IReadOnlyList<TimetableEntry> MapActivities(
        IEnumerable<JsonElement> rawList, IReadOnlyDictionary<string, string>? lecturerNames = null)
    {
        var output = new List<TimetableEntry>();
        foreach (var raw in rawList)
        {
            var a = RawReader.Activity(raw);
            if (a is null) continue;
            var start = UsosDateTime(a.StartTime);
            var end = UsosDateTime(a.EndTime);
            if (start is null || end is null) continue;
            var name = ToLangText(a.Name);
            var courseName = a.CourseName is not null ? ToLangText(a.CourseName, name.Pl) : name;
            var classType = ToLangText(a.ClasstypeName).Pl;
            var building = ToLangText(a.BuildingName).Pl;
            var key = a.UnitId ?? a.CourseId ?? NonSlugRegex().Replace(name.Pl.ToLowerInvariant(), "-");
            var digits = new string(a.StartTime!.Where(char.IsAsciiDigit).ToArray());
            var lecturers = a.LecturerIds is { } ids
                ? Items(ids).Select(IdOf)
                    .Select(id => id is not null && lecturerNames is not null && lecturerNames.TryGetValue(id, out var n) ? n : null)
                    .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList()
                : [];
            output.Add(new TimetableEntry(
                $"usos-tt:{key}:{digits}",
                SourceSystem.Usos,
                start.Value,
                end.Value,
                a.CourseId,
                courseName,
                classType.Length == 0 ? null : classType,
                ToInt(a.GroupNumber),
                string.IsNullOrEmpty(a.RoomNumber) ? null : a.RoomNumber,
                building.Length == 0 ? null : building,
                lecturers,
                string.IsNullOrEmpty(a.Url) ? null : a.Url));
        }
        return output;
    }

    // ---------- grades ----------

    private static bool CountsIntoAverage(string? v) => v is not null && TruthyRegex().IsMatch(v.Trim());

    /// <summary>
    /// Flattens grades/terms2:
    /// { term_id: { course_id: { course_grades: {session: grade}, course_units_grades: {unit_id: {session: grade}} } } }
    /// TODO(verify): exact nesting (dict vs. list per session) on the live installation; both shapes are tolerated.
    /// </summary>
    public static IReadOnlyList<Grade> MapGrades(JsonElement raw, GradeMapContext? ctx = null)
    {
        var output = new List<Grade>();
        if (raw.ValueKind != JsonValueKind.Object) return output;

        void AddGrade(JsonElement rawGrade, string termId, string courseId, string? unitId, string sessionKey)
        {
            var g = RawReader.Grade(rawGrade);
            if (g is null || string.IsNullOrEmpty(g.ValueSymbol)) return;
            var sessionNum = ToNumber(g.ExamSessionNumber) ?? ToNumber(sessionKey);
            var desc = g.ValueDescription is null ? null : ToLangText(g.ValueDescription);
            LangText? courseName = null;
            ctx?.CourseNames?.TryGetValue(courseId, out courseName);
            string? classType = null;
            if (unitId is not null) ctx?.UnitClassTypes?.TryGetValue(unitId, out classType);
            output.Add(new Grade(
                $"usos-grade:{termId}:{courseId}:{unitId ?? "course"}:{(sessionNum is { } sn ? NumberText(sn) : sessionKey)}",
                termId,
                courseId,
                courseName ?? new LangText(courseId),
                classType,
                sessionNum is { } s ? (int)Math.Truncate(s) : null,
                g.ValueSymbol,
                desc is { Pl: not "" } ? desc : null,
                g.Passes,
                CountsIntoAverage(g.CountsIntoAverage),
                UsosDateTime(g.DateModified),
                PersonName(g.ModificationAuthor),
                string.IsNullOrEmpty(g.Comment) ? null : g.Comment));
        }

        foreach (var term in raw.EnumerateObject())
        {
            if (term.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var course in term.Value.EnumerateObject())
            {
                if (course.Value.ValueKind != JsonValueKind.Object) continue;
                var cg = course.Value;
                if (Prop(cg, "course_grades") is { } courseGrades)
                    foreach (var (session, g) in EntriesOf(courseGrades)) AddGrade(g, term.Name, course.Name, null, session);
                if (Prop(cg, "course_units_grades") is { } unitGrades)
                {
                    foreach (var (unitId, sessions) in EntriesOf(unitGrades))
                    {
                        if (sessions.ValueKind == JsonValueKind.Object && sessions.TryGetProperty("value_symbol", out _))
                            AddGrade(sessions, term.Name, course.Name, unitId, "1");
                        else
                            foreach (var (session, g) in EntriesOf(sessions)) AddGrade(g, term.Name, course.Name, unitId, session);
                    }
                }
            }
        }
        return output;
    }

    /// <summary>"4,5" / "4.5" / "5" -> number; "zal", "nzal", "5!" etc. -> null.</summary>
    public static decimal? NumericGrade(string symbol)
    {
        var m = NumericGradeRegex().Match(symbol);
        return m.Success ? decimal.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// Plain (unweighted - ECTS weights are unknown) mean of numeric grades that count into the average.
    /// For a course/class type graded in several sessions only the latest session counts.
    /// </summary>
    public static decimal? ComputeAverage(IEnumerable<Grade> grades)
    {
        var latest = new Dictionary<string, Grade>();
        foreach (var g in grades)
        {
            if (!g.CountsIntoAverage || NumericGrade(g.ValueSymbol) is null) continue;
            var key = $"{g.TermId}|{g.CourseId}|{g.ClassType ?? ""}";
            if (!latest.TryGetValue(key, out var prev) || (g.ExamSession ?? 0) >= (prev.ExamSession ?? 0)) latest[key] = g;
        }
        var values = latest.Values.Select(g => NumericGrade(g.ValueSymbol)).OfType<decimal>().ToList();
        if (values.Count == 0) return null;
        return Math.Round(values.Sum() / values.Count, 2, MidpointRounding.AwayFromZero);
    }

    // ---------- payments ----------

    /// <summary>
    /// TODO(verify): meaning of total_amount / saldo_amount and the possible `state` values.
    /// Assumption: total_amount = amount charged; saldo_amount = balance (negative = still owed, 0 = settled).
    /// </summary>
    public static IReadOnlyList<Payment> MapPayments(IEnumerable<JsonElement> rawList, DateOnly today)
    {
        var output = new List<Payment>();
        foreach (var raw in rawList)
        {
            var p = RawReader.Payment(raw);
            if (p is null) continue;
            var total = ToNumber(p.TotalAmount) ?? 0m;
            var saldo = ToNumber(p.SaldoAmount);
            var owed = saldo is null ? total : Math.Max(0m, -saldo.Value);
            var paid = Math.Max(0m, Math.Round(total - owed, 2, MidpointRounding.AwayFromZero));
            var due = WarsawTime.ToDateOnly(p.PaymentDeadline);
            var rawState = (p.State ?? "").ToLowerInvariant();
            PaymentState state;
            if (CancelledRegex().IsMatch(rawState)) state = PaymentState.Cancelled;
            else if (owed <= 0 && total > 0) state = PaymentState.Paid;
            else if (PaidRegex().IsMatch(rawState) && saldo is null) state = PaymentState.Paid;
            else if (owed > 0 && due is not null && due < today) state = PaymentState.Overdue;
            else if (owed > 0 && paid > 0) state = PaymentState.PartiallyPaid;
            else state = PaymentState.Unpaid;
            if (state == PaymentState.Paid) paid = total;
            output.Add(new Payment(
                p.Id!,
                p.Type ?? "other",
                ToLangText(p.Description, p.Type ?? p.Id!),
                total,
                paid,
                p.Currency ?? "PLN", // TODO(verify): currency field; Vistula tuition is billed in PLN
                due,
                state,
                ToNumber(p.Interest),
                p.AccountNumber));
        }
        return output;
    }

    // ---------- messages (USOSmail) ----------

    private static string StripHtml(string html) =>
        WhitespaceRegex().Replace(TagRegex().Replace(html, " ").Replace("&nbsp;", " "), " ").Trim();

    /// <summary>TODO(verify): USOSmail raw shape (subject/sender/recipients/date/is_read/preview/body/attachments).</summary>
    public static MessageThread? MapMessageThread(JsonElement raw)
    {
        var m = RawReader.MailMessage(raw);
        return m is null ? null : ToThread(m);
    }

    private static MessageThread ToThread(RawMailMessage m)
    {
        var preview = m.Preview ?? (!string.IsNullOrEmpty(m.Body) ? Truncate(StripHtml(m.Body), 160) : "");
        var to = m.Recipients is { } r ? Items(r).Select(x => PersonName(x)).OfType<string>().ToList() : [];
        return new MessageThread(
            m.Id!,
            SourceSystem.Usos,
            m.Subject ?? "(bez tematu)",
            PersonName(m.Sender) ?? "",
            to,
            UsosDateTime(m.Date) ?? default,
            preview,
            m.IsRead is { } read && !read,
            null);
    }

    internal static string Truncate(string s, int max) => s.Length > max ? s[..max] : s;

    public static Message MapMessage(JsonElement raw)
    {
        var m = RawReader.MailMessage(raw) ?? throw new AdapterUnavailableException("USOSmail: nieoczekiwany format wiadomości") { IsTransient = false };
        var attachments = (m.Attachments ?? [])
            .Where(a => a.Name is not null && a.Url is not null)
            .Select(a => new Attachment(a.Name!, a.Url!, ToNumber(a.Size) is { } n ? (long)Math.Truncate(n) : null))
            .ToList();
        return new Message(ToThread(m), m.Body ?? "", attachments);
    }

    // ---------- dean's office ----------

    /// <summary>
    /// TODO(verify): fac/faculty exposes `phone_numbers` and `postal_address` but (to our knowledge) no e-mail.
    /// One Contact per faculty; postal address is stored in `Room` (the domain type has no address field).
    /// </summary>
    public static IReadOnlyList<Contact> MapFacultyContacts(JsonElement raw)
    {
        var f = RawReader.Faculty(raw);
        if (f is null) return [];
        return [new Contact(
            $"usos-fac:{f.Id}",
            ToLangText(f.Name, f.Id!).Pl,
            "Dziekanat",
            null,
            f.PhoneNumbers is { Count: > 0 } ph ? ph[0] : null,
            f.PostalAddress,
            null)];
    }

    // ---------- news ----------

    /// <summary>TODO(verify): news method + fields (title/content/publication_date/author/category/url).</summary>
    public static IReadOnlyList<Announcement> MapNews(IEnumerable<JsonElement> rawList)
    {
        var output = new List<Announcement>();
        foreach (var raw in rawList)
        {
            var a = RawReader.News(raw);
            if (a is null) continue;
            var category = ToLangText(a.Category).Pl;
            output.Add(new Announcement(
                $"usos-news:{a.Id}",
                SourceSystem.Usos,
                ToLangText(a.Title).Pl,
                ToLangText(a.Content).Pl,
                UsosDateTime(a.PublicationDate) ?? default,
                PersonName(a.Author),
                category.Length == 0 ? null : category,
                string.IsNullOrEmpty(a.Url) ? null : a.Url));
        }
        return output.OrderByDescending(x => x.PublishedAt).ToList();
    }

    // ---------- grade symbols (shared by web scraper and mock) ----------

    private static readonly Dictionary<string, LangText> GradeNames = new()
    {
        ["5.0"] = new("bardzo dobry", "very good"),
        ["4.5"] = new("dobry plus", "good plus"),
        ["4.0"] = new("dobry", "good"),
        ["3.5"] = new("dostateczny plus", "satisfactory plus"),
        ["3.0"] = new("dostateczny", "satisfactory"),
        ["2.0"] = new("niedostateczny", "fail"),
    };

    /// <summary>"4,5" -> "4.5", "5" -> "5.0", "ZAL" -> "zal"; unknown symbols are returned trimmed.</summary>
    public static string NormalizeGradeSymbol(string raw)
    {
        var s = raw.Trim();
        if (NumericGrade(s) is { } n) return n.ToString("F1", CultureInfo.InvariantCulture);
        if (ZalRegex().IsMatch(s)) return "zal";
        if (NzalRegex().IsMatch(s)) return "nzal";
        return s;
    }

    public static GradeInfo DescribeGrade(string symbol)
    {
        if (NumericGrade(symbol) is { } n)
            return new GradeInfo(n >= 3, GradeNames.GetValueOrDefault(n.ToString("F1", CultureInfo.InvariantCulture)));
        return symbol switch
        {
            "zal" => new GradeInfo(true, new LangText("zaliczony", "passed")),
            "nzal" => new GradeInfo(false, new LangText("niezaliczony", "not passed")),
            _ => new GradeInfo(null, null),
        };
    }
}
