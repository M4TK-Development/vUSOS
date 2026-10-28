// USOS adapter, mode "mock": deterministic, realistic data for a fictional Vistula student
// (Informatyka, studia I stopnia, semestr zimowy 2025/26). All ids are stable.
//
// - Terms, courses and grades are fixed (2025Z is always "current", independent of the system clock).
// - Timetable is generated for whatever [from, to] range is requested: every Mon-Fri has classes.
// - Payments, messages and announcements are dated RELATIVE to the injected clock so the demo never looks stale;
//   the ids stay the same.
// All names, e-mail addresses (.example) and phone numbers are fictional.
using System.Globalization;
using System.Text.RegularExpressions;
using Vusos.Core.Domain;
using Vusos.Core.Platform;

namespace Vusos.Core.Adapters.Usos;

public sealed partial class UsosMockAdapter(IClock clock) : IUsosAdapter
{
    public const string CurrentTermId = "2025Z";
    private const string StudentName = "Anna Nowak";

    // ---------- static catalogue ----------

    private static readonly IReadOnlyList<Term> Terms =
    [
        new("2025Z", new("Semestr zimowy 2025/26", "Winter semester 2025/26"), new(2025, 10, 1), new(2026, 2, 15), true),
        new("2024L", new("Semestr letni 2024/25", "Summer semester 2024/25"), new(2025, 2, 17), new(2025, 9, 30), false),
        new("2024Z", new("Semestr zimowy 2024/25", "Winter semester 2024/25"), new(2024, 10, 1), new(2025, 2, 16), false),
    ];

    private sealed record MockUnit(string UnitId, string ClassType, int? Group, string[] Lecturers);

    private sealed record MockCourse(string Id, string TermId, LangText Name, int Ects, MockUnit[] Units);

    private static readonly MockCourse[] Courses =
    [
        new("INF-PROG1", "2025Z", new("Programowanie obiektowe", "Object-oriented programming"), 6,
        [
            new("U-5101", "Wykład", 1, ["dr hab. Piotr Zieliński"]),
            new("U-5102", "Laboratorium", 2, ["mgr inż. Katarzyna Wójcik"]),
        ]),
        new("INF-ALG", "2025Z", new("Algorytmy i struktury danych", "Algorithms and data structures"), 6,
        [
            new("U-5201", "Wykład", 1, ["prof. dr hab. Marek Nowicki"]),
            new("U-5202", "Ćwiczenia", 1, ["dr Tomasz Lis"]),
        ]),
        new("INF-BD", "2025Z", new("Bazy danych", "Databases"), 5,
        [
            new("U-5301", "Wykład", 1, ["dr Anna Maj"]),
            new("U-5302", "Laboratorium", 2, ["dr Anna Maj", "mgr inż. Jan Zając"]),
        ]),
        new("INF-MAT", "2025Z", new("Matematyka dyskretna", "Discrete mathematics"), 5,
        [
            new("U-5401", "Wykład", 1, ["dr hab. Jan Wiśniewski"]),
            new("U-5402", "Ćwiczenia", 2, ["dr Ewa Kaczmarek"]),
        ]),
        new("INF-ANG", "2025Z", new("Język angielski B2", "English B2"), 2,
            [new("U-5501", "Lektorat", 3, ["mgr Emily Carter"])]),
        new("INF-SO", "2025Z", new("Systemy operacyjne", "Operating systems"), 4,
        [
            new("U-5601", "Wykład", 1, ["dr inż. Paweł Dąbrowski"]),
            new("U-5602", "Laboratorium", 1, ["mgr inż. Michał Kruk"]),
        ]),
        // past terms (course list only; used for grades and GetCoursesAsync(termId))
        new("INF-WIN", "2024Z", new("Wstęp do informatyki", "Introduction to computer science"), 4,
            [new("U-4101", "Wykład", 1, ["dr Piotr Zieliński"])]),
        new("INF-AM1", "2024Z", new("Analiza matematyczna I", "Calculus I"), 6,
        [
            new("U-4201", "Wykład", 1, ["prof. dr hab. Jan Wiśniewski"]),
            new("U-4202", "Ćwiczenia", 3, ["dr Ewa Kaczmarek"]),
        ]),
        new("INF-PP", "2024Z", new("Podstawy programowania", "Programming fundamentals"), 6,
        [
            new("U-4301", "Wykład", 1, ["dr Piotr Zieliński"]),
            new("U-4302", "Laboratorium", 2, ["mgr inż. Katarzyna Wójcik"]),
        ]),
        new("INF-AK", "2024Z", new("Architektura komputerów", "Computer architecture"), 4,
            [new("U-4401", "Wykład", 1, ["dr inż. Paweł Dąbrowski"])]),
        new("INF-ANG1", "2024Z", new("Język angielski B1", "English B1"), 2,
            [new("U-4501", "Lektorat", 3, ["mgr Emily Carter"])]),
        new("INF-AM2", "2024L", new("Analiza matematyczna II", "Calculus II"), 6,
            [new("U-3201", "Wykład", 1, ["prof. dr hab. Jan Wiśniewski"])]),
        new("INF-AL", "2024L", new("Algebra liniowa", "Linear algebra"), 5,
            [new("U-3301", "Wykład", 1, ["dr hab. Ewa Kaczmarek"])]),
        new("INF-PP2", "2024L", new("Programowanie strukturalne", "Structured programming"), 6,
            [new("U-3401", "Laboratorium", 2, ["mgr inż. Katarzyna Wójcik"])]),
        new("INF-ASK", "2024L", new("Architektura systemów komputerowych", "Computer systems architecture"), 4,
            [new("U-3501", "Wykład", 1, ["dr inż. Paweł Dąbrowski"])]),
        new("INF-ANG2", "2024L", new("Język angielski B1+", "English B1+"), 2,
            [new("U-3601", "Lektorat", 3, ["mgr Emily Carter"])]),
    ];

    /// <summary>At = local Warsaw time.</summary>
    private sealed record GradeSeed(
        string TermId, string CourseId, string? ClassType, int Session, string Symbol, string At, string By,
        bool Counts = true, string? Comment = null);

    private static readonly GradeSeed[] GradeSeeds =
    [
        // 2025Z (in progress)
        new("2025Z", "INF-PROG1", "Laboratorium", 1, "4.5", "2025-11-12 14:20:00", "mgr inż. Katarzyna Wójcik"),
        new("2025Z", "INF-ALG", "Ćwiczenia", 1, "4.0", "2025-11-19 12:05:00", "dr Tomasz Lis", Comment: "Kolokwium 1: 78%"),
        new("2025Z", "INF-BD", "Laboratorium", 1, "5.0", "2025-12-03 13:40:00", "dr Anna Maj"),
        new("2025Z", "INF-ANG", "Lektorat", 1, "zal", "2025-12-10 11:35:00", "mgr Emily Carter", Counts: false),
        // 2024L
        new("2024L", "INF-AM2", null, 1, "2.0", "2025-06-20 10:10:00", "prof. dr hab. Jan Wiśniewski"),
        new("2024L", "INF-AM2", null, 2, "3.5", "2025-09-05 09:45:00", "prof. dr hab. Jan Wiśniewski", Comment: "Termin poprawkowy"),
        new("2024L", "INF-AL", null, 1, "4.0", "2025-06-24 12:30:00", "dr hab. Ewa Kaczmarek"),
        new("2024L", "INF-PP2", "Laboratorium", 1, "5.0", "2025-06-12 15:00:00", "mgr inż. Katarzyna Wójcik"),
        new("2024L", "INF-ASK", null, 1, "3.5", "2025-06-26 11:15:00", "dr inż. Paweł Dąbrowski"),
        new("2024L", "INF-ANG2", "Lektorat", 1, "zal", "2025-06-10 13:00:00", "mgr Emily Carter", Counts: false),
        // 2024Z
        new("2024Z", "INF-WIN", null, 1, "4.5", "2025-01-28 10:20:00", "dr Piotr Zieliński"),
        new("2024Z", "INF-AM1", null, 1, "3.0", "2025-02-04 10:12:00", "prof. dr hab. Jan Wiśniewski"),
        new("2024Z", "INF-PP", "Laboratorium", 1, "4.0", "2025-01-22 16:00:00", "mgr inż. Katarzyna Wójcik"),
        new("2024Z", "INF-AK", null, 1, "4.0", "2025-02-06 12:00:00", "dr inż. Paweł Dąbrowski"),
        new("2024Z", "INF-ANG1", "Lektorat", 1, "zal", "2025-01-20 11:00:00", "mgr Emily Carter", Counts: false),
    ];

    /// <summary>Weekday: 1 = Monday.</summary>
    private sealed record Slot(int Weekday, string Start, string End, string UnitId, string Room, string Building);

    private static readonly Slot[] WeeklySlots =
    [
        new(1, "08:30", "10:00", "U-5201", "A-214", "Budynek A"),
        new(1, "12:30", "14:00", "U-5402", "B-105", "Budynek B"),
        new(2, "09:00", "10:30", "U-5301", "A-301", "Budynek A"),
        new(2, "10:45", "12:15", "U-5302", "Lab 3.12", "Budynek A"),
        new(3, "08:30", "10:00", "U-5601", "A-214", "Budynek A"),
        new(3, "10:15", "11:45", "U-5202", "B-104", "Budynek B"),
        new(3, "12:30", "14:00", "U-5102", "Lab 3.10", "Budynek A"),
        new(4, "09:00", "10:30", "U-5401", "A-301", "Budynek A"),
        new(4, "10:45", "12:15", "U-5602", "Lab 3.11", "Budynek A"),
        new(5, "10:00", "11:30", "U-5501", "C-12", "Budynek C"),
        new(5, "11:45", "13:15", "U-5101", "A-214", "Budynek A"),
    ];

    private static readonly Dictionary<string, (MockCourse Course, MockUnit Unit)> UnitIndex =
        Courses.SelectMany(c => c.Units.Select(u => (c, u))).ToDictionary(x => x.u.UnitId, x => (x.c, x.u));

    // ---------- mutable state ----------

    private readonly object _gate = new();
    private readonly List<Message> _sent = [];
    private readonly HashSet<string> _readIds = [];
    private IReadOnlyList<Message>? _baseMessages;

    private DateOnly Today => WarsawTime.DateOf(clock.Now);

    private DateTimeOffset IsoAt(int daysAgo, string time) =>
        WarsawTime.ParseLocal($"{WarsawTime.Format(Today.AddDays(-daysAgo))} {time}:00") ?? default;

    private static string Paragraphs(params string[] lines) => string.Concat(lines.Select(l => $"<p>{l}</p>"));

    [GeneratedRegex(@"<[^>]*>")] private static partial Regex TagRegex();
    [GeneratedRegex(@"\s+")] private static partial Regex WhitespaceRegex();

    private IReadOnlyList<Message> BaseMessages()
    {
        if (_baseMessages is not null) return _baseMessages;
        Message Make(int n, string from, string subject, int daysAgo, string time, bool unread, string body)
        {
            var preview = WhitespaceRegex().Replace(TagRegex().Replace(body, " "), " ").Trim();
            var thread = new MessageThread(
                $"mock-msg-{1000 + n}", SourceSystem.Usos, subject, from, [StudentName], IsoAt(daysAgo, time),
                preview.Length > 140 ? preview[..140] : preview, unread, null);
            return new Message(thread, body, []);
        }
        _baseMessages =
        [
            Make(1, "Dziekanat", "Potwierdzenie wpisu na semestr zimowy 2025/26", 1, "10:15", true,
                Paragraphs("Szanowna Pani,", "potwierdzamy wpis na semestr zimowy roku akademickiego 2025/26 (Informatyka, studia I stopnia, semestr 5).", "Pozdrawiamy,<br>Dziekanat")),
            Make(2, "Biuro ds. Studenckich", "Zmiana sali zajęć z Baz danych (wtorek)", 2, "15:42", true,
                Paragraphs("Informujemy, że wykład z Baz danych w najbliższy wtorek odbędzie się w sali A-301 (zamiast A-214).", "Biuro ds. Studenckich")),
            Make(3, "Dziekanat", "Przypomnienie: zaległa rata czesnego", 4, "09:05", false,
                Paragraphs("Przypominamy o konieczności uregulowania raty czesnego 3/5. Brak wpłaty skutkuje naliczeniem odsetek ustawowych.", "Dane do przelewu znajdziesz w zakładce Płatności.")),
            Make(4, "Biuro ds. Studenckich", "Zapisy na lektoraty na semestr letni", 9, "12:30", false,
                Paragraphs("Ruszają zapisy na lektoraty na semestr letni 2025/26. Zapisy prowadzimy w USOSweb do końca stycznia.")),
            Make(5, "Dziekanat", "Harmonogram sesji egzaminacyjnej zimowej 2025/26", 15, "08:50", false,
                Paragraphs("W załączeniu (USOSweb → Komunikacja) przesyłamy harmonogram sesji zimowej. Sesja podstawowa: 26.01–08.02.2026, poprawkowa: 09.02–15.02.2026.")),
            Make(6, "Biuro ds. Studenckich", "Wnioski o stypendium rektora - termin składania", 21, "14:10", false,
                Paragraphs("Przypominamy, że wnioski o stypendium rektora na rok akademicki 2025/26 można składać do 15 października.", "Wniosek składasz elektronicznie w USOSweb.")),
        ];
        return _baseMessages;
    }

    /// <summary>Caller holds the lock.</summary>
    private List<Message> AllMessages() =>
        _sent.Concat(BaseMessages())
            .Select(m => m with { Thread = m.Thread with { Unread = m.Thread.Unread && !_readIds.Contains(m.Thread.Id) } })
            .ToList();

    // ---------- IUsosAdapter ----------

    public Task<StudentProfile> GetProfileAsync(CancellationToken ct = default) =>
        Task.FromResult(new StudentProfile(
            "mock-usos-1001", "Anna", "Nowak", "anna.nowak@stu.vistula.example", "61234", null,
            [new Programme("INF-I-S", new("Informatyka", "Computer Science"), "active", "Studia I stopnia, stacjonarne, semestr 5")]));

    public Task<IReadOnlyList<Term>> GetTermsAsync(CancellationToken ct = default) => Task.FromResult(Terms);

    public Task<IReadOnlyList<CourseUnit>> GetCoursesAsync(string? termId = null, CancellationToken ct = default)
    {
        var wanted = string.IsNullOrEmpty(termId) ? CurrentTermId : termId;
        IReadOnlyList<CourseUnit> result = Courses.Where(c => c.TermId == wanted)
            .SelectMany(c => c.Units.Select(u => new CourseUnit(
                u.UnitId, c.Id, c.Name, u.ClassType, c.TermId, u.Group, [.. u.Lecturers], c.Ects, null)))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<TimetableEntry>> GetTimetableAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        IReadOnlyList<TimetableEntry> result = [];
        if (to >= from)
        {
            var output = new List<TimetableEntry>();
            foreach (var date in WarsawTime.EachDay(from, to).Take(366))
            {
                foreach (var slot in WeeklySlots.Where(s => s.Weekday == (int)date.DayOfWeek))
                {
                    if (!UnitIndex.TryGetValue(slot.UnitId, out var found)) continue;
                    var day = WarsawTime.Format(date);
                    var start = WarsawTime.ParseLocal($"{day} {slot.Start}:00");
                    var end = WarsawTime.ParseLocal($"{day} {slot.End}:00");
                    if (start is null || end is null) continue;
                    output.Add(new TimetableEntry(
                        $"mock-tt-{slot.UnitId}-{day.Replace("-", "", StringComparison.Ordinal)}",
                        SourceSystem.Usos, start.Value, end.Value, found.Course.Id, found.Course.Name, found.Unit.ClassType,
                        found.Unit.Group, slot.Room, slot.Building, [.. found.Unit.Lecturers], null));
                }
            }
            result = output.OrderBy(e => e.Start).ToList();
        }
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Grade>> GetGradesAsync(IReadOnlyList<string>? termIds = null, CancellationToken ct = default)
    {
        var courses = Courses.ToDictionary(c => c.Id);
        var grades = new List<Grade>();
        foreach (var s in GradeSeeds)
        {
            if (termIds is { Count: > 0 } && !termIds.Contains(s.TermId)) continue;
            if (!courses.TryGetValue(s.CourseId, out var course)) continue;
            var info = Mappers.DescribeGrade(s.Symbol);
            grades.Add(new Grade(
                $"mock-grade-{s.TermId}-{s.CourseId}-{(s.ClassType ?? "kurs").ToLowerInvariant()}-{s.Session}",
                s.TermId, s.CourseId, course.Name, s.ClassType, s.Session, s.Symbol, info.Description, info.Passes, s.Counts,
                WarsawTime.ParseLocal(s.At), s.By, s.Comment));
        }
        IReadOnlyList<Grade> result = grades.OrderByDescending(g => g.DateModified ?? default(DateTimeOffset)).ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Payment>> GetPaymentsAsync(CancellationToken ct = default)
    {
        var t = Today;
        const string account = "61109010140000071219812874";
        Payment Installment(int n, int dueOffset, decimal paid, PaymentState state, decimal? interest) => new(
            $"mock-pay-2025Z-{n}",
            "tuition",
            new($"Czesne za semestr zimowy 2025/26 - rata {n}/5", $"Tuition, winter semester 2025/26 - instalment {n}/5"),
            1250m, paid, "PLN", t.AddDays(dueOffset), state, interest, account);

        IReadOnlyList<Payment> result =
        [
            Installment(1, -72, 1250m, PaymentState.Paid, 0m),
            Installment(2, -40, 1250m, PaymentState.Paid, 0m),
            // 12 days late at 11.5% p.a.: 1250 * 0.115 / 365 * 12 = 4.73
            Installment(3, -12, 0m, PaymentState.Overdue, 4.73m),
            Installment(4, 18, 0m, PaymentState.Unpaid, null),
            Installment(5, 48, 0m, PaymentState.Unpaid, null),
            new("mock-pay-legitymacja", "id_card", new("Opłata za legitymację studencką", "Student ID card fee"),
                22m, 22m, "PLN", t.AddDays(-100), PaymentState.Paid, null, account),
        ];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<MessageThread>> ListMessagesAsync(int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<MessageThread> result = AllMessages().Skip(Math.Max(0, offset)).Take(Math.Max(0, limit)).Select(m => m.Thread).ToList();
            return Task.FromResult(result);
        }
    }

    public Task<Message> GetMessageAsync(string id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var msg = AllMessages().FirstOrDefault(m => m.Thread.Id == id)
                ?? throw new AdapterUnavailableException($"Nie znaleziono wiadomości {id}") { IsTransient = false };
            _readIds.Add(id);
            return Task.FromResult(msg with { Thread = msg.Thread with { Unread = false } });
        }
    }

    public Task<string> SendMessageAsync(IReadOnlyList<string> to, string subject, string body, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var id = $"mock-msg-sent-{_sent.Count + 1}";
            var thread = new MessageThread(
                id, SourceSystem.Usos, subject, StudentName, [.. to], clock.Now,
                body.Length > 140 ? body[..140] : body, false, null);
            var html = $"<p>{body.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)}</p>";
            _sent.Insert(0, new Message(thread, html, []));
            return Task.FromResult(id);
        }
    }

    public Task<IReadOnlyList<Contact>> GetDeanOfficeContactsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<Contact> result =
        [
            new("mock-contact-dziekanat", "Dziekanat Wydziału Informatyki", "Dziekanat", "dziekanat.informatyka@vistula.example",
                "+48 22 000 01 01", "Budynek A, pok. 105", "pon-pt 9:00-15:00, śr do 17:00"),
            new("mock-contact-bss", "Biuro ds. Studenckich", "Obsługa studentów", "bss@vistula.example",
                "+48 22 000 01 02", "Budynek A, parter", "pon-pt 8:30-16:00"),
            new("mock-contact-kwestura", "Kwestura - rozliczenia czesnego", "Płatności", "kwestura@vistula.example",
                "+48 22 000 01 03", "Budynek B, pok. 12", "pon, śr, pt 10:00-14:00"),
            new("mock-contact-opiekun", "dr Tomasz Lis", "Opiekun roku", "tomasz.lis@vistula.example",
                null, "Budynek A, pok. 311", "wt 13:00-14:30 (po wcześniejszym umówieniu)"),
            new("mock-contact-it", "Pomoc techniczna USOS / Platon", "Wsparcie IT", "pomoc-it@vistula.example",
                "+48 22 000 01 04", null, "pon-pt 8:00-18:00"),
        ];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<Announcement>> GetAnnouncementsAsync(int limit = 20, CancellationToken ct = default)
    {
        Announcement Item(int n, string title, int daysAgo, string author, string category, string body) =>
            new($"mock-news-{n}", SourceSystem.Usos, title, body, IsoAt(daysAgo, "09:00"), author, category, null);

        IReadOnlyList<Announcement> all =
        [
            Item(1, "Zapisy na seminaria dyplomowe", 2, "Dziekanat", "Dydaktyka",
                Paragraphs("Zapisy na seminaria dyplomowe dla studentów semestru 5 rozpoczynają się w poniedziałek o 10:00 w USOSweb.")),
            Item(2, "Zmiana godzin pracy dziekanatu w listopadzie", 5, "Biuro ds. Studenckich", "Organizacja",
                Paragraphs("W dniach 3-4 listopada dziekanat jest nieczynny. Sprawy pilne: dziekanat.informatyka@vistula.example.")),
            Item(3, "Stypendia rektora - wyniki", 11, "Komisja stypendialna", "Stypendia",
                Paragraphs("Wyniki postępowania stypendialnego są dostępne w USOSweb w zakładce Stypendia.")),
            Item(4, "Dzień otwarty biblioteki i szkolenie z baz naukowych", 16, "Biblioteka", "Wydarzenia",
                Paragraphs("Zapraszamy na szkolenie z korzystania z baz naukowych w czwartek o 16:00 (sala C-12).")),
            Item(5, "Harmonogram sesji zimowej 2025/26", 24, "Dziekanat", "Dydaktyka",
                Paragraphs("Opublikowano harmonogram sesji egzaminacyjnej. Szczegóły w wiadomości od dziekanatu.")),
        ];
        return Task.FromResult<IReadOnlyList<Announcement>>(all.Take(Math.Max(0, limit)).ToList());
    }
}
