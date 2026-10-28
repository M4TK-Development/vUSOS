// HTML parsers for USOSweb (kontroler.php) pages, built on AngleSharp.
//
// IMPORTANT - UNVERIFIED SELECTORS: there was no network access to the Vistula USOSweb when this was written.
// Markup assumptions (table.grey, .news-item, .act, th/td header texts, "prz_kod" links, ...) come from general
// knowledge of USOSweb and from the hand-written fixtures in Fixtures/Usos/*.html - NOT from captured pages.
// Expect to adjust selectors after comparing with the live site (save real pages over the fixtures and re-run the
// tests). All parsers are defensive: unknown markup yields an empty list / null instead of throwing.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Vusos.Core.Domain;

namespace Vusos.Core.Adapters.Usos;

public sealed record ParseOptions(string? BaseUrl = null);

public sealed record ParsedTerm(string Id, string Name);

public sealed record ParsedGrades(IReadOnlyList<ParsedTerm> Terms, IReadOnlyList<Grade> Grades);

public static partial class Parsers
{
    // ---------- helpers ----------

    private static readonly HtmlParser HtmlParser = new();

    private static IElement? Root(string html) => HtmlParser.ParseDocument(html ?? "").DocumentElement;

    private static IEnumerable<IElement> All(IParentNode node, string selector) => node.QuerySelectorAll(selector);

    private static string Clean(string s) => WhitespaceRegex().Replace(s, " ").Trim();

    private static string TextOf(IElement? el) => el is null ? "" : Clean(el.TextContent);

    private static string Slug(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
        {
            sb.Append(ch switch
            {
                'ą' => 'a', 'ć' => 'c', 'ę' => 'e', 'ł' => 'l', 'ń' => 'n', 'ó' => 'o', 'ś' => 's', 'ź' => 'z', 'ż' => 'z',
                _ => ch,
            });
        }
        var decomposed = sb.ToString();
        try { decomposed = decomposed.Normalize(NormalizationForm.FormD); }
        catch (ArgumentException) { /* no normalisation data: keep the manual mapping */ }
        var stripped = MarkRegex().Replace(decomposed, "");
        var slug = NonSlugRegex().Replace(stripped, "-").Trim('-');
        return slug.Length == 0 ? "x" : slug;
    }

    private static string? ResolveUrl(string? href, ParseOptions opts)
    {
        if (string.IsNullOrEmpty(href)) return null;
        if (opts.BaseUrl is not null && Uri.TryCreate(opts.BaseUrl, UriKind.Absolute, out var b)
            && Uri.TryCreate(b, href, out var abs))
            return abs.Scheme is "http" or "https" ? abs.AbsoluteUri : null; // never hand out javascript:/data: links
        return UnsafeSchemeRegex().IsMatch(href) ? null : href;
    }

    private static string? CourseIdFromHref(string? href)
    {
        var m = href is null ? null : CourseHrefRegex().Match(href);
        return m is { Success: true } ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>"dd.mm.yyyy" | "yyyy-mm-dd" [+ "HH:MM[:SS]" / "THH:MM"] -> "YYYY-MM-DD[ HH:MM:SS]" or null.</summary>
    public static string? NormalizeLocalDate(string s)
    {
        var t = Clean(s);
        var iso = IsoDateRegex().Match(t);
        if (iso.Success)
        {
            var (y, mo, d) = (iso.Groups[1].Value, iso.Groups[2].Value, iso.Groups[3].Value);
            return iso.Groups[4].Success
                ? $"{y}-{mo}-{d} {iso.Groups[4].Value.PadLeft(2, '0')}:{iso.Groups[5].Value}:{(iso.Groups[6].Success ? iso.Groups[6].Value : "00")}"
                : $"{y}-{mo}-{d}";
        }
        var pl = PlDateRegex().Match(t);
        if (pl.Success)
        {
            var date = $"{pl.Groups[3].Value}-{pl.Groups[2].Value.PadLeft(2, '0')}-{pl.Groups[1].Value.PadLeft(2, '0')}";
            return pl.Groups[4].Success
                ? $"{date} {pl.Groups[4].Value.PadLeft(2, '0')}:{pl.Groups[5].Value}:{(pl.Groups[6].Success ? pl.Groups[6].Value : "00")}"
                : date;
        }
        return null;
    }

    private static DateTimeOffset? ToIsoOrNull(string? s)
    {
        var n = string.IsNullOrEmpty(s) ? null : NormalizeLocalDate(s);
        return n is null ? null : WarsawTime.ParseLocal(n);
    }

    /// <summary>"1 250,00 zł" -> 1250.</summary>
    public static decimal? ParseMoney(string s)
    {
        var t = Clean(CurrencyRegex().Replace(Clean(s), "")).Replace(" ", "");
        return t.Length == 0 ? null : Mappers.ToNumber(t);
    }

    private sealed record TableRow(IReadOnlyList<IElement> Cells, IElement Row);

    private sealed record TableData(List<string> Headers, List<TableRow> Rows);

    private static TableData ReadTable(IElement table)
    {
        var data = new TableData([], []);
        foreach (var tr in table.QuerySelectorAll("tr"))
        {
            var ths = tr.Children.Where(c => c.LocalName == "th").ToList();
            var tds = tr.Children.Where(c => c.LocalName == "td").ToList();
            if (ths.Count > 0 && tds.Count == 0 && data.Headers.Count == 0)
                data.Headers.AddRange(ths.Select(th => TextOf(th).ToLowerInvariant()));
            else if (tds.Count > 0)
                data.Rows.Add(new TableRow(tds, tr));
        }
        return data;
    }

    private static int FindCol(IReadOnlyList<string> headers, Regex re)
    {
        for (var i = 0; i < headers.Count; i++) if (re.IsMatch(headers[i])) return i;
        return -1;
    }

    private static string? Cell(TableRow row, int col) => col >= 0 && col < row.Cells.Count ? TextOf(row.Cells[col]) : null;

    [GeneratedRegex(@"\s+")] private static partial Regex WhitespaceRegex();
    [GeneratedRegex(@"\p{M}+")] private static partial Regex MarkRegex();
    [GeneratedRegex(@"[^a-z0-9]+")] private static partial Regex NonSlugRegex();
    [GeneratedRegex(@"^\s*(javascript|data|vbscript):", RegexOptions.IgnoreCase)] private static partial Regex UnsafeSchemeRegex();
    [GeneratedRegex(@"[?&]prz_kod=([^&#]+)")] private static partial Regex CourseHrefRegex();
    [GeneratedRegex(@"(\d{4})-(\d{2})-(\d{2})(?:[ T](\d{1,2}):(\d{2})(?::(\d{2}))?)?")] private static partial Regex IsoDateRegex();
    [GeneratedRegex(@"(\d{1,2})\.(\d{1,2})\.(\d{4})(?:\s+(\d{1,2}):(\d{2})(?::(\d{2}))?)?")] private static partial Regex PlDateRegex();
    [GeneratedRegex(@"(zł|pln)", RegexOptions.IgnoreCase)] private static partial Regex CurrencyRegex();

    // ---------- login detection ----------

    [GeneratedRegex(@"logowanie|login\.microsoftonline\.com|/login\b|login\.php", RegexOptions.IgnoreCase)]
    private static partial Regex LoginRedirectRegex();

    /// <summary>True when an HTTP redirect target is the (SSO) login page.</summary>
    public static bool IsLoginRedirect(string location) => LoginRedirectRegex().IsMatch(location);

    /// <summary>True when a 200 response body is the login page rather than a logged-in view.</summary>
    public static bool LooksLikeLoginPage(string html)
    {
        if (html.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase)) return true;
        return html.Contains("Zaloguj", StringComparison.Ordinal) && !html.Contains("Wyloguj", StringComparison.Ordinal);
    }

    // ---------- profile ----------

    [GeneratedRegex(@"(?:Zalogowany(?:/a)?(?:\s+jako)?:?)\s*([^|(]+?)\s*(?:\(|\||$)", RegexOptions.IgnoreCase)]
    private static partial Regex LoggedInRegex();

    [GeneratedRegex(@"(?:nr|numer)\s+albumu\s*:?\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AlbumRegex();

    public static StudentProfile? ParseProfile(string html)
    {
        var root = Root(html);
        if (root is null) return null;
        var info = TextOf(root.QuerySelector("#login-info, .user-info, #user-info"));
        var m = LoggedInRegex().Match(info);
        var name = m.Success ? Clean(m.Groups[1].Value) : "";
        if (name.Length == 0) return null;
        var parts = name.Split(' ');
        var lastName = parts.Length > 1 ? parts[^1] : "";
        var firstName = parts.Length > 1 ? string.Join(' ', parts[..^1]) : name;
        var album = AlbumRegex().Match(info) is { Success: true } a ? a.Groups[1].Value : null;
        var programmes = All(root, "ul.programmes li").Select((li, i) =>
        {
            var label = TextOf(li);
            var comma = label.IndexOf(',');
            var name0 = comma < 0 ? label : label[..comma];
            return new Programme(
                li.GetAttribute("data-programme-id") ?? $"web-programme-{i + 1}",
                new LangText(Clean(name0)),
                null,
                comma >= 0 ? Clean(label[(comma + 1)..]) : null);
        }).ToList();
        return new StudentProfile(
            album ?? $"web:{Slug(name)}",
            firstName,
            lastName,
            null, // TODO(verify): USOSweb shows the e-mail on "Moje dane" (dla_stud/.../dane?), not on the home page
            album,
            null,
            programmes);
    }

    // ---------- timetable ----------

    private sealed record ActFields(
        string Start, string End, string Name, string? CourseId, string? ClassType, int? Group,
        string? Room, string? Building, List<string> Lecturers, string? Url);

    private static TimetableEntry? BuildEntry(ActFields a)
    {
        var start = WarsawTime.ParseLocal(a.Start);
        var end = WarsawTime.ParseLocal(a.End);
        if (start is null || end is null || a.Name.Length == 0) return null;
        var digits = new string(a.Start.Where(char.IsAsciiDigit).ToArray());
        return new TimetableEntry(
            $"usos-web-tt:{a.CourseId ?? Slug(a.Name)}:{digits}",
            SourceSystem.Usos,
            start.Value,
            end.Value,
            a.CourseId,
            new LangText(a.Name),
            a.ClassType,
            a.Group,
            a.Room,
            a.Building,
            a.Lecturers,
            a.Url);
    }

    private static List<TimetableEntry> PlanFromJson(IElement root)
    {
        var output = new List<TimetableEntry>();
        foreach (var s in All(root, "script[type=\"application/json\"]"))
        {
            try
            {
                using var doc = JsonDocument.Parse(s.TextContent);
                var el = doc.RootElement;
                var list = el.ValueKind == JsonValueKind.Array ? el
                    : Mappers.Prop(el, "activities") is { ValueKind: JsonValueKind.Array } acts ? acts
                    : default;
                output.AddRange(Mappers.MapActivities(Mappers.Items(list).Select(x => x.Clone()).ToList()));
            }
            catch (JsonException)
            {
                // not our JSON
            }
        }
        return output;
    }

    private static List<TimetableEntry> PlanFromDataAttrs(IElement root, ParseOptions opts)
    {
        var output = new List<TimetableEntry>();
        foreach (var el in All(root, "[data-start][data-end]"))
        {
            var start = NormalizeLocalDate(el.GetAttribute("data-start") ?? "");
            var end = NormalizeLocalDate(el.GetAttribute("data-end") ?? "");
            if (start is null || end is null) continue;
            var group = Mappers.ToNumber(el.GetAttribute("data-group"));
            var entry = BuildEntry(new ActFields(
                start.Length == 10 ? $"{start} 00:00:00" : start,
                end.Length == 10 ? $"{end} 00:00:00" : end,
                Clean(el.GetAttribute("data-name") ?? TextOf(el)),
                el.GetAttribute("data-course-id"),
                NullIfEmpty(el.GetAttribute("data-classtype")),
                group is null ? null : (int)Math.Truncate(group.Value),
                NullIfEmpty(el.GetAttribute("data-room")),
                NullIfEmpty(el.GetAttribute("data-building")),
                (el.GetAttribute("data-lecturers") ?? "").Split(',').Select(Clean).Where(s => s.Length > 0).ToList(),
                ResolveUrl(el.GetAttribute("data-url"), opts)));
            if (entry is not null) output.Add(entry);
        }
        return output;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    [GeneratedRegex(@"(\d{1,2}):(\d{2})\s*[-–—]\s*(\d{1,2}):(\d{2})")] private static partial Regex TimeRegex();
    [GeneratedRegex(@"(\d+)")] private static partial Regex DigitsRegex();
    [GeneratedRegex(@"^(sala|s\.|room)\s*", RegexOptions.IgnoreCase)] private static partial Regex RoomPrefixRegex();

    private static TimetableEntry? ParseAct(IElement el, string date, ParseOptions opts)
    {
        var timeText = TextOf(el.QuerySelector(".time"));
        var t = TimeRegex().Match(timeText.Length > 0 ? timeText : TextOf(el));
        if (!t.Success) return null;
        static string Hhmm(string h, string m) => $"{h.PadLeft(2, '0')}:{m}:00";
        var link = el.QuerySelector("a");
        var name = link is not null && TextOf(link).Length > 0 ? TextOf(link) : TextOf(el.QuerySelector(".name"));
        var groupMatch = DigitsRegex().Match(TextOf(el.QuerySelector(".group, .grp")));
        var room = RoomPrefixRegex().Replace(TextOf(el.QuerySelector(".room, .loc")), "");
        var lecturers = All(el, ".lecturer, .lect")
            .SelectMany(l => TextOf(l).Split(';'))
            .Select(Clean).Where(s => s.Length > 0).ToList();
        var classType = TextOf(el.QuerySelector(".type, .classtype"));
        var href = link?.GetAttribute("href");
        return BuildEntry(new ActFields(
            $"{date} {Hhmm(t.Groups[1].Value, t.Groups[2].Value)}",
            $"{date} {Hhmm(t.Groups[3].Value, t.Groups[4].Value)}",
            name,
            CourseIdFromHref(href),
            classType.Length > 0 ? Capitalize(classType) : null,
            groupMatch.Success ? int.Parse(groupMatch.Groups[1].Value, CultureInfo.InvariantCulture) : null,
            NullIfEmpty(room),
            NullIfEmpty(TextOf(el.QuerySelector(".building"))),
            lecturers,
            ResolveUrl(href, opts)));
    }

    private static List<TimetableEntry> PlanFromTables(IElement root, ParseOptions opts)
    {
        var output = new List<TimetableEntry>();
        foreach (var table in All(root, "table"))
        {
            var firstRow = table.QuerySelector("tr");
            var dates = (firstRow?.Children.Where(c => c.LocalName == "th") ?? [])
                .Select(th => NormalizeLocalDate(th.GetAttribute("data-date") ?? TextOf(th)) is { } d ? d[..10] : null)
                .ToList();
            if (!dates.Any(d => d is not null)) continue;
            foreach (var tr in table.QuerySelectorAll("tr"))
            {
                var tds = tr.Children.Where(c => c.LocalName == "td").ToList();
                for (var idx = 0; idx < tds.Count; idx++)
                {
                    var date = idx < dates.Count ? dates[idx] : null;
                    if (date is null) continue;
                    var cell = tds[idx];
                    var acts = cell.QuerySelectorAll(".act, .activity, li").ToList();
                    if (acts.Count == 0 && TimeRegex().IsMatch(TextOf(cell))) acts = [cell];
                    foreach (var a in acts)
                    {
                        var entry = ParseAct(a, date, opts);
                        if (entry is not null) output.Add(entry);
                    }
                }
            }
        }
        return output;
    }

    /// <summary>Tries, in order: embedded JSON, data-start/data-end elements, day-column table. First non-empty wins.</summary>
    public static IReadOnlyList<TimetableEntry> ParsePlan(string html, ParseOptions? opts = null)
    {
        opts ??= new ParseOptions();
        var root = Root(html);
        if (root is null) return [];
        var strategies = new Func<List<TimetableEntry>>[]
        {
            () => PlanFromJson(root),
            () => PlanFromDataAttrs(root, opts),
            () => PlanFromTables(root, opts),
        };
        foreach (var run in strategies)
        {
            var entries = run();
            if (entries.Count > 0)
                return UsosApiAdapter.DedupeById(entries, e => e.Id).OrderBy(e => e.Start).ToList();
        }
        return [];
    }

    // ---------- terms (academic calendar) ----------

    [GeneratedRegex(@"\b(20\d{2})([ZL])\b", RegexOptions.IgnoreCase)] private static partial Regex DirectTermRegex();
    [GeneratedRegex(@"\b(20\d{2})\s*/\s*(?:20)?\d{2}\b")] private static partial Regex YearTermRegex();
    [GeneratedRegex(@"^(20\d{2})([ZL])$")] private static partial Regex TermIdRegex();
    [GeneratedRegex(@"\s*\(\s*20\d{2}[ZL]\s*\)\s*$", RegexOptions.IgnoreCase)] private static partial Regex TermSuffixRegex();

    /// <summary>"2025Z" / "Semestr zimowy 2025/26" -> "2025Z". TODO(verify): Vistula's real term ids.</summary>
    public static string? TermIdFromText(string text)
    {
        var direct = DirectTermRegex().Match(text);
        if (direct.Success) return $"{direct.Groups[1].Value}{direct.Groups[2].Value.ToUpperInvariant()}";
        var yr = YearTermRegex().Match(text);
        if (yr.Success)
        {
            if (text.Contains("zimow", StringComparison.OrdinalIgnoreCase)) return $"{yr.Groups[1].Value}Z";
            if (text.Contains("letni", StringComparison.OrdinalIgnoreCase)) return $"{yr.Groups[1].Value}L";
        }
        return null;
    }

    /// <summary>Approximate Polish academic calendar. TODO(verify): real term boundaries come from USOS.</summary>
    public static Term? TermFromId(string id, DateOnly today)
    {
        var m = TermIdRegex().Match(id);
        if (!m.Success) return null;
        var y = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var winter = m.Groups[2].Value == "Z";
        var start = winter ? new DateOnly(y, 10, 1) : new DateOnly(y + 1, 2, 16);
        var end = winter ? new DateOnly(y + 1, 2, 15) : new DateOnly(y + 1, 9, 30);
        var yy = ((y + 1) % 100).ToString("00", CultureInfo.InvariantCulture);
        return new Term(id, new LangText($"Semestr {(winter ? "zimowy" : "letni")} {y}/{yy}"), start, end, start <= today && today <= end);
    }

    /// <summary>Term id containing the given date (approximate calendar).</summary>
    public static string TermIdForDate(DateOnly date)
    {
        var md = date.Month * 100 + date.Day;
        if (md >= 1001) return $"{date.Year}Z";
        if (md <= 215) return $"{date.Year - 1}Z";
        return $"{date.Year - 1}L";
    }

    // ---------- grades ----------

    private static (string Id, string Name)? TermContext(IElement table)
    {
        var candidates = new List<string>();
        var dataTerm = table.Closest("[data-term-id]")?.GetAttribute("data-term-id");
        if (!string.IsNullOrEmpty(dataTerm)) candidates.Add(dataTerm);
        candidates.Add(TextOf(table.QuerySelector("caption")));
        IElement? cur = table;
        for (var depth = 0; depth < 3 && cur is not null; depth++)
        {
            IElement? heading = null;
            for (var sib = cur.PreviousElementSibling; sib is not null; sib = sib.PreviousElementSibling)
            {
                if (sib.Matches("h1, h2, h3, h4, .term-header, .semestr")) { heading = sib; break; }
            }
            if (heading is not null)
            {
                candidates.Add(TextOf(heading));
                break;
            }
            cur = cur.ParentElement;
        }
        foreach (var c in candidates)
        {
            var id = TermIdFromText(c);
            if (id is null) continue;
            var name = TermSuffixRegex().Replace(c, "");
            return (id, name.Length > 0 ? name : id);
        }
        return null;
    }

    [GeneratedRegex(@"przedmiot|nazwa")] private static partial Regex ColCourseRegex();
    [GeneratedRegex(@"ocena")] private static partial Regex ColGradeRegex();
    [GeneratedRegex(@"typ|forma|rodzaj")] private static partial Regex ColTypeRegex();
    [GeneratedRegex(@"termin|podej|sesja")] private static partial Regex ColSessionRegex();
    [GeneratedRegex(@"data")] private static partial Regex ColDateRegex();
    [GeneratedRegex(@"wystawi|nauczyciel|prowadz")] private static partial Regex ColByRegex();
    [GeneratedRegex(@"komentarz|uwagi")] private static partial Regex ColCommentRegex();

    public static ParsedGrades ParseGrades(string html, ParseOptions? opts = null)
    {
        _ = opts;
        var terms = new List<ParsedTerm>();
        var grades = new List<Grade>();
        var root = Root(html);
        if (root is null) return new ParsedGrades(terms, grades);

        foreach (var table in All(root, "table"))
        {
            var ctx = TermContext(table);
            if (ctx is null) continue;
            var (headers, rows) = ReadTable(table);
            var cCourse = FindCol(headers, ColCourseRegex());
            var cGrade = FindCol(headers, ColGradeRegex());
            if (cCourse < 0 || cGrade < 0 || rows.Count == 0) continue;
            var cType = FindCol(headers, ColTypeRegex());
            var cSession = FindCol(headers, ColSessionRegex());
            var cDate = FindCol(headers, ColDateRegex());
            var cBy = headers.FindIndex(h => ColByRegex().IsMatch(h) && !ColDateRegex().IsMatch(h));
            var cComment = FindCol(headers, ColCommentRegex());

            var existing = terms.FindIndex(t => t.Id == ctx.Value.Id);
            if (existing >= 0) terms[existing] = new ParsedTerm(ctx.Value.Id, ctx.Value.Name);
            else terms.Add(new ParsedTerm(ctx.Value.Id, ctx.Value.Name));

            foreach (var row in rows)
            {
                if (cCourse >= row.Cells.Count || cGrade >= row.Cells.Count) continue;
                var courseCell = row.Cells[cCourse];
                var rawSymbol = TextOf(row.Cells[cGrade]);
                if (rawSymbol.Length == 0 || rawSymbol is "-" or "—") continue;
                var symbol = Mappers.NormalizeGradeSymbol(rawSymbol);
                var link = courseCell.QuerySelector("a");
                var courseName = TextOf(courseCell);
                var courseId = CourseIdFromHref(link?.GetAttribute("href")) ?? $"web:{Slug(courseName)}";
                var classType = Cell(row, cType) ?? "";
                var sessionText = Cell(row, cSession);
                var sessionNum = sessionText is null ? null : DigitsRegex().Match(sessionText) is { Success: true } sm ? Mappers.ToNumber(sm.Value) : null;
                var info = Mappers.DescribeGrade(symbol);
                grades.Add(new Grade(
                    $"usos-web-grade:{ctx.Value.Id}:{courseId}:{Slug(classType)}:{(sessionNum is { } sn ? Mappers.NumberText(sn) : "1")}",
                    ctx.Value.Id,
                    courseId,
                    new LangText(courseName),
                    classType.Length > 0 ? classType : null,
                    sessionNum is { } s ? (int)Math.Truncate(s) : null,
                    symbol,
                    info.Description,
                    info.Passes,
                    // TODO(verify): USOSweb does not show "counts into average"; assume numeric grades do.
                    Mappers.NumericGrade(symbol) is not null,
                    Cell(row, cDate) is { } d ? ToIsoOrNull(d) : null,
                    NullIfEmpty(Cell(row, cBy)),
                    NullIfEmpty(Cell(row, cComment))));
            }
        }
        return new ParsedGrades(terms, grades);
    }

    // ---------- payments ----------

    [GeneratedRegex(@"\b(\d{2}(?:\s?\d{4}){6})\b")] private static partial Regex AccountRegex();
    [GeneratedRegex(@"opis|tytu")] private static partial Regex PayDescRegex();
    [GeneratedRegex(@"kwota|nale")] private static partial Regex PayAmountRegex();
    [GeneratedRegex(@"termin|do dnia|p[łl]atne")] private static partial Regex PayDueRegex();
    [GeneratedRegex(@"wp[łl]acono|zap[łl]acono")] private static partial Regex PayPaidRegex();
    [GeneratedRegex(@"odsetk")] private static partial Regex PayInterestRegex();
    [GeneratedRegex(@"status|stan")] private static partial Regex PayStateRegex();
    [GeneratedRegex(@"anul|storn")] private static partial Regex StCancelRegex();
    [GeneratedRegex(@"cz[eę][śs]ciowo")] private static partial Regex StPartialRegex();
    [GeneratedRegex(@"zap[łl]acon|op[łl]acon")] private static partial Regex StPaidRegex();
    [GeneratedRegex(@"nie|do zap")] private static partial Regex StNotPaidRegex();
    [GeneratedRegex(@"zaleg|po terminie|przeterm")] private static partial Regex StOverdueRegex();
    [GeneratedRegex(@"czesne|rata", RegexOptions.IgnoreCase)] private static partial Regex TypeTuitionRegex();
    [GeneratedRegex(@"legitymac", RegexOptions.IgnoreCase)] private static partial Regex TypeIdCardRegex();
    [GeneratedRegex(@"wpisow", RegexOptions.IgnoreCase)] private static partial Regex TypeEnrollRegex();

    private static string PaymentType(string desc) =>
        TypeTuitionRegex().IsMatch(desc) ? "tuition"
        : TypeIdCardRegex().IsMatch(desc) ? "id_card"
        : TypeEnrollRegex().IsMatch(desc) ? "enrollment_fee"
        : "other";

    public static IReadOnlyList<Payment> ParsePayments(string html, DateOnly today)
    {
        var output = new List<Payment>();
        var root = Root(html);
        if (root is null) return output;
        var accountText = TextOf(root.QuerySelector(".account-number, .numer-konta"));
        var pageText = Clean(root.QuerySelector("body")?.TextContent ?? "");
        var account = AccountRegex().Match(accountText.Length > 0 ? accountText : pageText) is { Success: true } am ? am.Groups[1].Value : null;

        foreach (var table in All(root, "table"))
        {
            var (headers, rows) = ReadTable(table);
            var cDesc = FindCol(headers, PayDescRegex());
            var cAmount = FindCol(headers, PayAmountRegex());
            if (cDesc < 0 || cAmount < 0) continue;
            var cDue = FindCol(headers, PayDueRegex());
            var cPaid = FindCol(headers, PayPaidRegex());
            var cInterest = FindCol(headers, PayInterestRegex());
            var cState = FindCol(headers, PayStateRegex());
            foreach (var row in rows)
            {
                var description = Cell(row, cDesc) ?? "";
                var amount = Cell(row, cAmount) is { } at ? ParseMoney(at) : null;
                if (description.Length == 0 || amount is null) continue;
                var dueRaw = Cell(row, cDue) is { } dt ? NormalizeLocalDate(dt) : null;
                var due = dueRaw is null ? (DateOnly?)null : WarsawTime.ToDateOnly(dueRaw[..10]);
                var paid = Cell(row, cPaid) is { } pt ? ParseMoney(pt) ?? 0m : 0m;
                var statusText = (Cell(row, cState) ?? "").ToLowerInvariant();
                var owed = Math.Max(0m, amount.Value - paid);
                PaymentState state;
                if (StCancelRegex().IsMatch(statusText)) state = PaymentState.Cancelled;
                else if (StPartialRegex().IsMatch(statusText) && owed > 0)
                    state = due is not null && due < today ? PaymentState.Overdue : PaymentState.PartiallyPaid;
                else if (StPaidRegex().IsMatch(statusText) && !StNotPaidRegex().IsMatch(statusText)) state = PaymentState.Paid;
                else if (StOverdueRegex().IsMatch(statusText)) state = PaymentState.Overdue;
                else if (owed <= 0 && amount > 0) state = PaymentState.Paid;
                else if (owed > 0 && due is not null && due < today) state = PaymentState.Overdue;
                else if (paid > 0) state = PaymentState.PartiallyPaid;
                else state = PaymentState.Unpaid;
                output.Add(new Payment(
                    $"usos-web-pay:{Slug(description)}:{(due is { } dd ? WarsawTime.Format(dd) : "nodue")}",
                    PaymentType(description),
                    new LangText(description),
                    amount.Value,
                    state == PaymentState.Paid ? amount.Value : paid,
                    "PLN",
                    due,
                    state,
                    Cell(row, cInterest) is { } it ? ParseMoney(it) : null,
                    account is null ? null : WhitespaceRegex().Replace(account, "")));
            }
        }
        return output;
    }

    // ---------- news ----------

    public static IReadOnlyList<Announcement> ParseNews(string html, ParseOptions? opts = null)
    {
        opts ??= new ParseOptions();
        var output = new List<Announcement>();
        var root = Root(html);
        if (root is null) return output;
        var i = 0;
        foreach (var el in All(root, ".news-item, .wiadomosc, article.news"))
        {
            var index = i++;
            var titleEl = el.QuerySelector(".title, h2, h3, h4");
            var title = TextOf(titleEl);
            if (title.Length == 0) continue;
            var link = titleEl?.QuerySelector("a");
            var dateText = el.QuerySelector("time")?.GetAttribute("datetime") ?? TextOf(el.QuerySelector(".date, time, .news-date"));
            var published = ToIsoOrNull(dateText);
            var idAttr = el.GetAttribute("data-id");
            var content = el.QuerySelector(".news-content, .content, .tresc");
            var stamp = published is { } p ? p.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : index.ToString(CultureInfo.InvariantCulture);
            output.Add(new Announcement(
                $"usos-web-news:{idAttr ?? $"{Slug(title)}-{stamp}"}",
                SourceSystem.Usos,
                title,
                (content?.InnerHtml ?? "").Trim(),
                published ?? default,
                NullIfEmpty(TextOf(el.QuerySelector(".author, .autor"))),
                NullIfEmpty(TextOf(el.QuerySelector(".category, .kategoria"))),
                ResolveUrl(link?.GetAttribute("href"), opts)));
        }
        return output.OrderByDescending(a => a.PublishedAt).ToList();
    }

    // ---------- messages ----------

    [GeneratedRegex(@"[?&](?:msg_id|message_id|wiadomosc_id|mid)=([^&#]+)")] private static partial Regex MessageIdRegex();
    [GeneratedRegex(@"temat")] private static partial Regex ColSubjectRegex();
    [GeneratedRegex(@"nadawca|od\b")] private static partial Regex ColFromRegex();
    [GeneratedRegex(@"[;,]")] private static partial Regex RecipientSplitRegex();

    private static string? MessageIdFromHref(string? href)
    {
        var m = href is null ? null : MessageIdRegex().Match(href);
        return m is { Success: true } ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    }

    public static IReadOnlyList<MessageThread> ParseMessages(string html, ParseOptions? opts = null)
    {
        opts ??= new ParseOptions();
        var output = new List<MessageThread>();
        var root = Root(html);
        if (root is null) return output;
        foreach (var table in All(root, "table"))
        {
            var (headers, rows) = ReadTable(table);
            var cSubject = FindCol(headers, ColSubjectRegex());
            if (cSubject < 0) continue;
            var cFrom = FindCol(headers, ColFromRegex());
            var cDate = FindCol(headers, ColDateRegex());
            foreach (var row in rows)
            {
                if (cSubject >= row.Cells.Count) continue;
                var subjectCell = row.Cells[cSubject];
                var link = subjectCell.QuerySelector("a");
                var id = MessageIdFromHref(link?.GetAttribute("href"));
                if (id is null) continue;
                var unread = row.Row.Matches(".unread, .new, .nieprzeczytana")
                    || row.Row.GetAttribute("data-unread") == "1"
                    || subjectCell.QuerySelector("strong, b") is not null;
                output.Add(new MessageThread(
                    id,
                    SourceSystem.Usos,
                    TextOf(subjectCell),
                    Cell(row, cFrom) ?? "",
                    [],
                    Cell(row, cDate) is { } d ? ToIsoOrNull(d) ?? default : default,
                    "",
                    unread,
                    ResolveUrl(link?.GetAttribute("href"), opts)));
            }
        }
        return output;
    }

    public static Message? ParseMessage(string html, string id, ParseOptions? opts = null)
    {
        opts ??= new ParseOptions();
        var doc = HtmlParser.ParseDocument(html ?? "");
        IParentNode? scope = doc.QuerySelector(".message") ?? (IParentNode?)doc.Body;
        if (scope is null) return null;
        var subject = TextOf(scope.QuerySelector(".subject, h1, h2"));
        var body = scope.QuerySelector(".message-body, .tresc, .body");
        if (subject.Length == 0 || body is null) return null;
        var to = TextOf(scope.QuerySelector(".to"));
        var attachments = All(scope, "a.attachment")
            .Select(a => new Attachment(TextOf(a), ResolveUrl(a.GetAttribute("href"), opts) ?? "", null))
            .Where(a => a.Url.Length > 0)
            .ToList();
        var preview = Clean(body.TextContent);
        var thread = new MessageThread(
            id,
            SourceSystem.Usos,
            subject,
            TextOf(scope.QuerySelector(".from")),
            to.Length > 0 ? RecipientSplitRegex().Split(to).Select(Clean).Where(s => s.Length > 0).ToList() : [],
            ToIsoOrNull(TextOf(scope.QuerySelector(".date"))) ?? default,
            preview.Length > 160 ? preview[..160] : preview,
            false,
            null);
        return new Message(thread, body.InnerHtml.Trim(), attachments);
    }

    // ---------- week helpers used by the adapter ----------

    /// <summary>Plan-page query params for a given Monday. TODO(verify): real parameter name of the week selector.</summary>
    public static IReadOnlyDictionary<string, string> PlanWeekParams(DateOnly monday) =>
        new Dictionary<string, string>
        {
            ["plan_start"] = WarsawTime.Format(monday),
            ["plan_end"] = WarsawTime.Format(monday.AddDays(6)),
        };
}
