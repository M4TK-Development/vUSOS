// USOS adapter, mode "web": works on USOSweb session cookies (Cookie header string) and scrapes kontroler.php
// pages with AngleSharp (see Parsers.cs).
//
// UNVERIFIED: selectors and query parameters were written without access to the live Vistula USOSweb and are tested
// only against hand-written fixtures in tests/Vusos.Core.Tests/Fixtures/Usos. Adjust after comparing with real pages.
// Limitations in this mode: no message sending, no dean's office contacts, courses are derived from the timetable,
// terms from the grades page + the academic calendar.
//
// The HttpClient should not follow redirects and should not manage cookies (see CreateHttpClient); if it follows
// redirects anyway, the final URL is checked as well.
using System.Net;
using System.Net.Http.Headers;
using Vusos.Core.Domain;
using Vusos.Core.Platform;

namespace Vusos.Core.Adapters.Usos;

public sealed class UsosWebAdapter : IUsosAdapter
{
    // TODO(verify): all _action values below.
    public static class Actions
    {
        public const string Home = "home/index";
        public const string Plan = "home/plan";
        public const string Grades = "dla_stud/studia/oceny/index";
        public const string Payments = "dla_stud/platnosci/index";
        public const string Messages = "dla_stud/studia/komunikacja";
    }

    private const int MaxRedirects = 5;
    private const int MaxWeeks = 14;

    private readonly HttpClient _http;
    private readonly IClock _clock;
    private readonly string _base;
    private readonly string _origin;
    private readonly string _cookie;
    private readonly ParseOptions _parseOpts;

    public UsosWebAdapter(HttpClient http, UsosWebCredentials creds, IClock clock)
    {
        _http = http;
        _clock = clock;
        if (string.IsNullOrWhiteSpace(creds.BaseUrl))
            throw new AdapterUnavailableException("Brak adresu USOSweb w poświadczeniach") { IsTransient = false };
        _base = UsosHttp.NormalizeBaseUrl(creds.BaseUrl);
        _origin = OriginOf(new Uri(_base));
        _cookie = creds.Cookies.Replace('\r', ' ').Replace('\n', ' ').Trim();
        _parseOpts = new ParseOptions(_base);
    }

    /// <summary>HttpClient suited for this adapter: no automatic redirects (login redirects must be seen), no cookie jar.</summary>
    public static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

    private DateOnly Today => WarsawTime.DateOf(_clock.Now);

    private static string OriginOf(Uri u) => $"{u.Scheme}://{u.Authority}".ToLowerInvariant();

    private static string EncodeQuery(string action, IReadOnlyDictionary<string, string>? extra)
    {
        var parts = new List<string> { $"_action={Uri.EscapeDataString(action)}" };
        if (extra is not null)
            parts.AddRange(extra.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return string.Join('&', parts);
    }

    private async Task<string> GetHtmlAsync(string action, IReadOnlyDictionary<string, string>? extra, CancellationToken ct)
    {
        var url = new Uri($"{_base}kontroler.php?{EncodeQuery(action, extra)}");
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            var current = url;
            var (status, location, finalUri, html) = await FetchAsync(current, ct).ConfigureAwait(false);
            var code = (int)status;
            if (code is >= 300 and < 400)
            {
                if (string.IsNullOrEmpty(location) || Parsers.IsLoginRedirect(location))
                    throw new AdapterAuthException("Sesja USOSweb wygasła");
                var next = new Uri(current, location);
                if (OriginOf(next) != _origin)
                    throw new AdapterAuthException("USOSweb przekierował poza domenę (prawdopodobnie SSO)");
                url = next;
                continue;
            }
            if (code is 401 or 403) throw new AdapterAuthException("Sesja USOSweb wygasła");
            if (code is < 200 or >= 300)
                throw new AdapterUnavailableException($"USOSweb: HTTP {code} ({action})") { IsTransient = code >= 500 || code is 408 or 429 };

            // A handler that follows redirects on its own hides the login redirect: look at where we ended up.
            if (finalUri is not null && (OriginOf(finalUri) != _origin || Parsers.IsLoginRedirect(finalUri.AbsoluteUri)))
                throw new AdapterAuthException("Sesja USOSweb wygasła");
            if (Parsers.LooksLikeLoginPage(html)) throw new AdapterAuthException("Sesja USOSweb wygasła");
            return html;
        }
        throw new AdapterUnavailableException("USOSweb: zbyt wiele przekierowań") { IsTransient = false };
    }

    private async Task<(HttpStatusCode Status, string? Location, Uri? FinalUri, string Body)> FetchAsync(Uri url, CancellationToken ct)
    {
        string? location = null;
        Uri? finalUri = null;
        var (status, body) = await UsosHttp.SendForTextAsync(async c =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Cookie", _cookie);
            req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
            req.Headers.AcceptLanguage.ParseAdd("pl,en;q=0.8");
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; vUSOS/0.1)");
            var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, c).ConfigureAwait(false);
            location = res.Headers.Location?.OriginalString;
            finalUri = res.RequestMessage?.RequestUri;
            return res;
        }, "USOSweb niedostępny", UsosHttp.DefaultTimeout, ct).ConfigureAwait(false);
        return (status, location, finalUri, body);
    }

    private async Task<IReadOnlyList<TimetableEntry>> PlanWeeksAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (to < from) return [];
        var mondays = new List<DateOnly>();
        for (var m = WarsawTime.MondayOf(from); m <= to && mondays.Count < MaxWeeks; m = m.AddDays(7)) mondays.Add(m);
        var entries = new List<TimetableEntry>();
        foreach (var monday in mondays)
        {
            var html = await GetHtmlAsync(Actions.Plan, Parsers.PlanWeekParams(monday), ct).ConfigureAwait(false);
            entries.AddRange(Parsers.ParsePlan(html, _parseOpts));
        }
        var inRange = entries.Where(e => WarsawTime.LocalDateOf(e.Start) >= from && WarsawTime.LocalDateOf(e.Start) <= to);
        return UsosApiAdapter.DedupeById(inRange, e => e.Id).OrderBy(e => e.Start).ToList();
    }

    // ---------- IUsosAdapter ----------

    public async Task<StudentProfile> GetProfileAsync(CancellationToken ct = default) =>
        Parsers.ParseProfile(await GetHtmlAsync(Actions.Home, null, ct).ConfigureAwait(false))
        ?? throw new AdapterUnavailableException("Nie udało się odczytać profilu z USOSweb (zmiana układu strony?)") { IsTransient = false };

    public async Task<IReadOnlyList<Term>> GetTermsAsync(CancellationToken ct = default)
    {
        var today = Today;
        var parsed = Parsers.ParseGrades(await GetHtmlAsync(Actions.Grades, null, ct).ConfigureAwait(false), _parseOpts);
        var ids = parsed.Terms.Select(t => t.Id).ToList();
        var current = Parsers.TermIdForDate(today);
        if (!ids.Contains(current)) ids.Add(current);
        return ids.Select(id => Parsers.TermFromId(id, today)).OfType<Term>().OrderByDescending(t => t.StartDate).ToList();
    }

    public async Task<IReadOnlyList<CourseUnit>> GetCoursesAsync(string? termId = null, CancellationToken ct = default)
    {
        // Derived from the timetable of the current and next week (USOSweb has no cheap "my courses" view).
        var today = Today;
        var currentTerm = Parsers.TermIdForDate(today);
        if (!string.IsNullOrEmpty(termId) && termId != currentTerm) return [];
        var monday = WarsawTime.MondayOf(today);
        var entries = await PlanWeeksAsync(monday, monday.AddDays(13), ct).ConfigureAwait(false);

        var order = new List<string>();
        var first = new Dictionary<string, TimetableEntry>();
        var lecturers = new Dictionary<string, List<string>>();
        foreach (var e in entries)
        {
            var courseKey = e.CourseId ?? e.CourseName.Pl;
            var id = $"usos-web-unit:{courseKey}:{e.ClassType ?? ""}:{e.GroupNumber ?? 0}";
            if (first.ContainsKey(id))
            {
                foreach (var l in e.Lecturers) if (!lecturers[id].Contains(l)) lecturers[id].Add(l);
                continue;
            }
            order.Add(id);
            first[id] = e;
            lecturers[id] = [.. e.Lecturers];
        }
        return order.Select(id =>
        {
            var e = first[id];
            return new CourseUnit(id, e.CourseId ?? e.CourseName.Pl, e.CourseName, e.ClassType ?? "", currentTerm, e.GroupNumber,
                lecturers[id], null, null);
        }).ToList();
    }

    public Task<IReadOnlyList<TimetableEntry>> GetTimetableAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        PlanWeeksAsync(from, WarsawTime.DiffDays(from, to) > 98 ? from.AddDays(98) : to, ct);

    public async Task<IReadOnlyList<Grade>> GetGradesAsync(IReadOnlyList<string>? termIds = null, CancellationToken ct = default)
    {
        var parsed = Parsers.ParseGrades(await GetHtmlAsync(Actions.Grades, null, ct).ConfigureAwait(false), _parseOpts);
        return termIds is { Count: > 0 } ? parsed.Grades.Where(g => termIds.Contains(g.TermId)).ToList() : parsed.Grades;
    }

    public async Task<IReadOnlyList<Payment>> GetPaymentsAsync(CancellationToken ct = default) =>
        Parsers.ParsePayments(await GetHtmlAsync(Actions.Payments, null, ct).ConfigureAwait(false), Today);

    public async Task<IReadOnlyList<MessageThread>> ListMessagesAsync(int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        var all = Parsers.ParseMessages(await GetHtmlAsync(Actions.Messages, null, ct).ConfigureAwait(false), _parseOpts);
        return all.Skip(Math.Max(0, offset)).Take(limit > 0 ? limit : int.MaxValue).ToList();
    }

    public async Task<Message> GetMessageAsync(string id, CancellationToken ct = default)
    {
        // TODO(verify): detail URL parameter (msg_id).
        var html = await GetHtmlAsync(Actions.Messages, new Dictionary<string, string> { ["msg_id"] = id }, ct).ConfigureAwait(false);
        return Parsers.ParseMessage(html, id, _parseOpts)
            ?? throw new AdapterUnavailableException("Nie udało się odczytać wiadomości z USOSweb") { IsTransient = false };
    }

    public Task<string> SendMessageAsync(IReadOnlyList<string> to, string subject, string body, CancellationToken ct = default) =>
        // TODO(verify): the compose form (field names, CSRF token) is unknown; not implemented in web mode.
        Task.FromException<string>(new AdapterNotSupportedException("Wysyłanie wiadomości nie jest dostępne w trybie web (użyj USOS API)"));

    public Task<IReadOnlyList<Contact>> GetDeanOfficeContactsAsync(CancellationToken ct = default) =>
        // TODO(verify): find the page with dean's office contact data and add a parser.
        Task.FromResult<IReadOnlyList<Contact>>([]);

    public async Task<IReadOnlyList<Announcement>> GetAnnouncementsAsync(int limit = 20, CancellationToken ct = default) =>
        Parsers.ParseNews(await GetHtmlAsync(Actions.Home, null, ct).ConfigureAwait(false), _parseOpts).Take(limit).ToList();
}
