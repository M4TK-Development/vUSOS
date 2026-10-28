using Vusos.Core.Domain;
using Vusos.UI.Util;

namespace Vusos.WebHost.Dev;

/// <summary>Mały, realistyczny zestaw danych po polsku do trybu Adapters:Mode=fake. Daty liczone względem „teraz”.</summary>
public static class FakeData
{
    public const string AccountNumber = "PL 61 1090 1014 0000 0712 1981 2874";

    private static DateTimeOffset At(DateOnly day, int hour, int minute)
    {
        var local = day.ToDateTime(new TimeOnly(hour, minute), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Fmt.Warsaw.GetUtcOffset(local));
    }

    // ---------- USOS ----------

    public static StudentProfile Profile() => new(
        "usos-1001", "Anna", "Kowalska", "anna.kowalska@stu.vistula.edu.pl", "63012",
        null, [new Programme("INF-I", LangText.Of("Informatyka i ekonometria, I stopień"), "aktywny", "semestr 3")]);

    public static IReadOnlyList<Term> Terms() =>
    [
        new("2026Z", LangText.Of("2026/27 zimowy"), new DateOnly(2026, 10, 1), new DateOnly(2027, 2, 28), true),
        new("2026L", LangText.Of("2025/26 letni"), new DateOnly(2026, 2, 23), new DateOnly(2026, 9, 30), false),
        new("2025Z", LangText.Of("2025/26 zimowy"), new DateOnly(2025, 10, 1), new DateOnly(2026, 2, 22), false),
    ];

    private sealed record Slot(DayOfWeek Day, int H1, int M1, int H2, int M2, string CourseId, string Name, string Type, int? Group, string Room, string Building, string Lecturer, string? Url = null);

    private static readonly Slot[] Week =
    [
        new(DayOfWeek.Monday, 9, 0, 10, 30, "MIK", "Mikroekonomia", "wykład", null, "201", "budynek A", "prof. dr hab. Anna Nowicka"),
        new(DayOfWeek.Monday, 11, 0, 12, 30, "STA", "Statystyka", "ćwiczenia", 3, "114", "budynek B", "dr Piotr Wiśniewski"),
        new(DayOfWeek.Tuesday, 8, 30, 10, 0, "BD", "Bazy danych", "laboratorium", 2, "L3", "budynek C", "mgr inż. Marek Zieliński"),
        new(DayOfWeek.Tuesday, 10, 15, 11, 45, "ANG", "Język angielski B2", "lektorat", 5, "305", "budynek A", "mgr Emily Carter"),
        new(DayOfWeek.Wednesday, 10, 0, 11, 30, "PO", "Programowanie obiektowe", "wykład", null, "Aula 1", "budynek A", "dr inż. Tomasz Lewandowski"),
        new(DayOfWeek.Wednesday, 12, 0, 13, 30, "PO", "Programowanie obiektowe", "laboratorium", 2, "L1", "budynek C", "mgr inż. Marek Zieliński"),
        new(DayOfWeek.Wednesday, 15, 0, 16, 30, "ZP", "Zarządzanie projektami", "seminarium", 1, "212", "budynek B", "dr Katarzyna Nowak"),
        new(DayOfWeek.Thursday, 9, 0, 10, 30, "MIK", "Mikroekonomia", "ćwiczenia", 4, "118", "budynek B", "dr Piotr Wiśniewski"),
        new(DayOfWeek.Thursday, 13, 0, 14, 30, "BD", "Bazy danych", "wykład", null, "Aula 2", "budynek A", "dr hab. Jan Kamiński"),
        new(DayOfWeek.Friday, 8, 0, 9, 30, "PG", "Prawo gospodarcze", "wykład", null, "online", "MS Teams", "dr hab. Tomasz Lewandowski", "https://teams.microsoft.com/l/meetup-join/fake"),
    ];

    public static IReadOnlyList<TimetableEntry> Timetable(DateOnly from, DateOnly to)
    {
        var result = new List<TimetableEntry>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var i = 0;
            foreach (var s in Week.Where(s => s.Day == d.DayOfWeek))
            {
                result.Add(new TimetableEntry(
                    $"tt-{d:yyyyMMdd}-{i++}", SourceSystem.Usos, At(d, s.H1, s.M1), At(d, s.H2, s.M2),
                    s.CourseId, LangText.Of(s.Name), s.Type, s.Group, s.Room, s.Building, [s.Lecturer], s.Url));
            }
        }
        return result;
    }

    public static IReadOnlyList<Grade> Grades(DateTimeOffset now)
    {
        Grade G(string id, string term, string course, string name, string? type, int? session, string symbol, string? desc, bool? passes, bool counts, DateTimeOffset? modified, string by, string? comment = null)
            => new(id, term, course, LangText.Of(name), type, session, symbol, desc is null ? null : LangText.Of(desc), passes, counts, modified, by, comment);

        return
        [
            G("g1", "2026Z", "MIK", "Mikroekonomia", "ćwiczenia", 1, "4,5", "dobry plus", true, true, now.AddDays(-3), "dr Piotr Wiśniewski", "Kolokwium 1 – bardzo dobra analiza popytu."),
            G("g2", "2026Z", "PO", "Programowanie obiektowe", "laboratorium", 1, "5", "bardzo dobry", true, true, now.AddDays(-9), "mgr inż. Marek Zieliński"),
            G("g3", "2026Z", "BD", "Bazy danych", "laboratorium", 1, "4", "dobry", true, true, now.AddDays(-15), "mgr inż. Marek Zieliński"),
            G("g4", "2026Z", "ANG", "Język angielski B2", "lektorat", 1, "zal", "zaliczone", true, false, now.AddDays(-20), "mgr Emily Carter"),

            G("g5", "2026L", "MAK", "Makroekonomia", "egzamin", 1, "3,5", "dostateczny plus", true, true, new DateTimeOffset(2026, 6, 22, 11, 0, 0, TimeSpan.FromHours(2)), "prof. dr hab. Anna Nowicka"),
            G("g6", "2026L", "RACH", "Rachunkowość finansowa", "egzamin", 1, "2", "niedostateczny", false, true, new DateTimeOffset(2026, 6, 18, 12, 0, 0, TimeSpan.FromHours(2)), "dr Ewa Malinowska", "Przystąp do terminu poprawkowego."),
            G("g7", "2026L", "RACH", "Rachunkowość finansowa", "egzamin", 2, "4", "dobry", true, true, new DateTimeOffset(2026, 9, 7, 10, 0, 0, TimeSpan.FromHours(2)), "dr Ewa Malinowska"),
            G("g8", "2026L", "STAT", "Statystyka opisowa", "ćwiczenia", 1, "4,5", "dobry plus", true, true, new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.FromHours(2)), "dr Piotr Wiśniewski"),
            G("g9", "2026L", "WPR", "Wstęp do programowania", "laboratorium", 1, "5", "bardzo dobry", true, true, new DateTimeOffset(2026, 6, 12, 9, 0, 0, TimeSpan.FromHours(2)), "mgr inż. Marek Zieliński"),

            G("g10", "2025Z", "MAT", "Matematyka dla ekonomistów", "egzamin", 1, "4", "dobry", true, true, new DateTimeOffset(2026, 2, 5, 10, 0, 0, TimeSpan.FromHours(1)), "dr hab. Jan Kamiński"),
            G("g11", "2025Z", "PPR", "Podstawy prawa", "egzamin", 1, "5", "bardzo dobry", true, true, new DateTimeOffset(2026, 2, 9, 10, 0, 0, TimeSpan.FromHours(1)), "dr hab. Tomasz Lewandowski"),
            G("g12", "2025Z", "ANG", "Język angielski B1", "lektorat", 1, "zal", "zaliczone", true, false, new DateTimeOffset(2026, 1, 28, 10, 0, 0, TimeSpan.FromHours(1)), "mgr Emily Carter"),
        ];
    }

    public static IReadOnlyList<Payment> Payments() =>
    [
        new("p1", "Czesne", LangText.Of("Czesne – rata 1/10 (październik 2026)"), 1450m, 1450m, "PLN", new DateOnly(2026, 10, 10), PaymentState.Paid, null, AccountNumber),
        new("p2", "Czesne", LangText.Of("Czesne – rata 2/10 (listopad 2026)"), 1450m, 0m, "PLN", new DateOnly(2026, 11, 10), PaymentState.Unpaid, null, AccountNumber),
        new("p3", "Opłata dodatkowa", LangText.Of("Powtarzanie przedmiotu: Rachunkowość finansowa"), 300m, 0m, "PLN", new DateOnly(2026, 10, 14), PaymentState.Overdue, 4.80m, AccountNumber),
        new("p4", "Ubezpieczenie", LangText.Of("Ubezpieczenie NNW 2026/27"), 100m, 40m, "PLN", new DateOnly(2026, 11, 1), PaymentState.PartiallyPaid, null, AccountNumber),
        new("p5", "Opłata administracyjna", LangText.Of("Elektroniczna legitymacja studencka"), 22m, 22m, "PLN", new DateOnly(2026, 10, 2), PaymentState.Paid, null, AccountNumber),
    ];

    public static IReadOnlyList<MessageThread> Threads(DateTimeOffset now) =>
    [
        new("m1", SourceSystem.Usos, "Zmiana sali na zajęciach z Mikroekonomii", "Dziekanat Wydziału Ekonomii", ["Anna Kowalska"], now.AddHours(-3), "Informujemy, że ćwiczenia w czwartek odbędą się w sali 118...", true, null),
        new("m2", SourceSystem.Usos, "Rata czesnego za listopad", "Dział Płatności", ["Anna Kowalska"], now.AddDays(-1), "Przypominamy o terminie płatności kolejnej raty czesnego...", true, null),
        new("m3", SourceSystem.Usos, "Re: Pytanie o kolokwium", "dr Piotr Wiśniewski", ["Anna Kowalska"], now.AddDays(-2), "Kolokwium obejmuje materiał z wykładów 1–4. Powodzenia!", false, null),
        new("m4", SourceSystem.Usos, "Praktyki studenckie – nabór", "Biuro Karier", ["Anna Kowalska"], now.AddDays(-5), "Ruszył nabór na praktyki zawodowe w partnerskich firmach...", false, null),
        new("m5", SourceSystem.Usos, "Dni otwarte biblioteki", "Biblioteka Główna", ["Anna Kowalska"], now.AddDays(-9), "Zapraszamy na szkolenie z korzystania z baz naukowych.", false, null),
        new("m6", SourceSystem.Usos, "Wybory do Samorządu Studenckiego", "Samorząd Studencki", ["Anna Kowalska"], now.AddDays(-14), "Zgłoszenia kandydatów przyjmujemy do końca miesiąca.", false, null),
    ];

    public static Message MessageBody(MessageThread t) => t.Id switch
    {
        "m1" => new Message(t, "<p>Szanowni Państwo,</p><p>informujemy, że <strong>ćwiczenia z Mikroekonomii w czwartek</strong> odbędą się w <b>sali 118</b> (budynek B) zamiast sali 204.</p><p>Pozdrawiamy,<br>Dziekanat</p>", []),
        "m2" => new Message(t, "<p>Dzień dobry,</p><p>przypominamy o terminie płatności raty czesnego za listopad: <strong>10.11.2026</strong>. Numer konta znajdziesz w zakładce <em>Płatności</em>.</p>", [new Attachment("Regulamin_oplat_2026.pdf", "https://usosweb.vistula.edu.pl/fake/regulamin.pdf", 184_320)]),
        "m3" => new Message(t, "<p>Dzień dobry,</p><ul><li>wykłady 1–4,</li><li>ćwiczenia z zestawów 1–3.</li></ul><p>Powodzenia!<br>P. Wiśniewski</p>", []),
        _ => new Message(t, $"<p>{t.Preview}</p><p>Szczegóły na stronie uczelni.</p>", []),
    };

    public static IReadOnlyList<Contact> Contacts() =>
    [
        new("c1", "Dziekanat Wydziału Ekonomii", "Obsługa studentów I i II stopnia", "dziekanat.ekonomia@vistula.edu.pl", "+48 22 457 23 00", "B-012", "pn–pt 9:00–15:00"),
        new("c2", "Dział Płatności", "Czesne i opłaty", "platnosci@vistula.edu.pl", "+48 22 457 23 40", "A-104", "pn–pt 10:00–14:00"),
        new("c3", "Pomoc techniczna USOS", "Dostęp do kont i systemów", "helpdesk@vistula.edu.pl", null, null, "pn–pt 8:00–16:00"),
    ];

    public static IReadOnlyList<Announcement> UsosAnnouncements(DateTimeOffset now) =>
    [
        new("ua1", SourceSystem.Usos, "Harmonogram sesji zimowej 2026/27", "<p>Sesja zimowa odbędzie się w dniach <strong>1–14 lutego 2027</strong>. Szczegółowy harmonogram egzaminów zostanie opublikowany do 15 grudnia.</p>", now.AddDays(-1), "Dziekanat", "Sesja", null),
        new("ua2", SourceSystem.Usos, "Dni wolne od zajęć – listopad", "<p>W dniach 2 i 11 listopada zajęcia nie odbywają się.</p>", now.AddDays(-4), "Rektorat", "Organizacja roku", null),
        new("ua3", SourceSystem.Usos, "Stypendia rektora – wnioski", "<p>Wnioski o stypendium rektora można składać w USOSweb do <b>20 listopada</b>.</p><ul><li>średnia powyżej 4,5,</li><li>osiągnięcia naukowe lub sportowe.</li></ul>", now.AddDays(-8), "Dział Spraw Studenckich", "Stypendia", "https://usosweb.vistula.edu.pl/fake/stypendia"),
    ];

    public static IReadOnlyList<Announcement> PlatonAnnouncements(DateTimeOffset now) =>
    [
        new("pa1", SourceSystem.Platon, "Programowanie obiektowe: termin kolokwium", "<p>Kolokwium 1 odbędzie się online w Platonie. Test można rozwiązywać także offline w aplikacji (po rozpoczęciu z połączeniem).</p>", now.AddHours(-20), "dr inż. Tomasz Lewandowski", "Programowanie obiektowe", "https://platon.vistula.edu.pl/mod/forum/discuss.php?d=1"),
        new("pa2", SourceSystem.Platon, "Bazy danych: materiały do laboratorium 4", "<p>W kursie pojawił się skrypt SQL do laboratorium 4. Pobierz go przed zajęciami.</p>", now.AddDays(-3), "mgr inż. Marek Zieliński", "Bazy danych", null),
        new("pa3", SourceSystem.Platon, "Język angielski: zmiana terminu prezentacji", "<p>Prezentacje przesunięte na <i>przyszły tydzień</i>.</p>", now.AddDays(-6), "mgr Emily Carter", "Język angielski B2", null),
    ];

    // ---------- Platon ----------

    private static readonly (string Id, string Short, string Full, string Category, double Progress)[] CourseList =
    [
        ("101", "PO-2026Z", "Programowanie obiektowe", "Informatyka", 62),
        ("102", "BD-2026Z", "Bazy danych", "Informatyka", 40),
        ("103", "MIK-2026Z", "Mikroekonomia", "Ekonomia", 75),
        ("104", "ZP-2026Z", "Zarządzanie projektami", "Zarządzanie", 20),
        ("105", "ANG-B2", "Język angielski B2", "Lektoraty", 88),
    ];

    public static IReadOnlyList<LmsCourse> PlatonCourses(DateTimeOffset now) => CourseList
        .Select(c => new LmsCourse(c.Id, c.Short, c.Full, c.Category, c.Progress, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2027, 2, 28, 0, 0, 0, TimeSpan.FromHours(1)), null, $"https://platon.vistula.edu.pl/course/view.php?id={c.Id}", null, now.AddDays(-1)))
        .ToList();

    private static LmsFile F(string name, string mime, long size, string course, DateTimeOffset modified)
        => new(name, $"https://platon.vistula.edu.pl/pluginfile.php/{course}/mod_resource/content/1/{Uri.EscapeDataString(name)}", mime, size, modified);

    public static IReadOnlyList<LmsSection> CourseContents(string courseId, DateTimeOffset now)
    {
        var course = CourseList.FirstOrDefault(c => c.Id == courseId);
        if (course.Id is null) return [];
        var mod = now.AddDays(-12);
        const string Pdf = "application/pdf";
        LmsModule M(string id, string section, string sectionName, string name, LmsModuleType type, string raw, string? url, string? desc, IReadOnlyList<LmsFile> files, bool? completed = null, string? instance = null)
            => new($"{courseId}{id}", courseId, $"{courseId}-{section}", sectionName, name, type, raw, url, desc, files, completed, true, instance);

        const string S0 = "Informacje ogólne", S1 = "Tydzień 1 – Wprowadzenie", S2 = "Tydzień 2 – Podstawy", S3 = "Zaliczenie";
        var modules0 = new List<LmsModule>
        {
            M("01", "0", S0, "Witamy w kursie – zasady i organizacja", LmsModuleType.Label, "label", null, "<p><strong>Witaj!</strong> Przed pierwszymi zajęciami przeczytaj sylabus i zasady zaliczenia.</p>", []),
            M("02", "0", S0, "Sylabus przedmiotu", LmsModuleType.Resource, "resource", $"https://platon.vistula.edu.pl/mod/resource/view.php?id={courseId}02", null, [F($"Sylabus_{course.Short}.pdf", Pdf, 218_000, courseId, mod)], true),
            M("03", "0", S0, "Zasady zaliczenia", LmsModuleType.Page, "page", $"https://platon.vistula.edu.pl/mod/page/view.php?id={courseId}03", "<p>Zaliczenie na podstawie: <b>kolokwium (40%)</b>, zadań (40%) i aktywności (20%). Obecność na laboratoriach jest obowiązkowa.</p>", [], true),
            M("04", "0", S0, "Forum ogłoszeń", LmsModuleType.Forum, "forum", $"https://platon.vistula.edu.pl/mod/forum/view.php?id={courseId}04", null, []),
        };
        var modules1 = new List<LmsModule>
        {
            M("11", "1", S1, "Wykład 1 – slajdy", LmsModuleType.Resource, "resource", $"https://platon.vistula.edu.pl/mod/resource/view.php?id={courseId}11", null, [F("Wyklad_01.pdf", Pdf, 1_480_000, courseId, mod)], true),
            M("12", "1", S1, "Materiały do ćwiczeń", LmsModuleType.Folder, "folder", $"https://platon.vistula.edu.pl/mod/folder/view.php?id={courseId}12", null,
                [F("Zestaw_1.pdf", Pdf, 340_000, courseId, mod), F("Zestaw_1_rozwiazania.pdf", Pdf, 410_000, courseId, mod), F("Dane_do_cwiczen.zip", "application/zip", 2_900_000, courseId, mod)]),
            M("13", "1", S1, "Dokumentacja Microsoft Learn", LmsModuleType.Url, "url", "https://learn.microsoft.com/pl-pl/dotnet/", "<p>Oficjalna dokumentacja – polecana jako lektura uzupełniająca.</p>", []),
            M("14", "1", S1, "Zadanie 1 – pierwsze rozwiązanie", LmsModuleType.Assign, "assign", $"https://platon.vistula.edu.pl/mod/assign/view.php?id={courseId}14", "<p>Prześlij rozwiązanie w formie pliku PDF lub archiwum.</p>", [], null, $"{courseId}001"),
        };
        var modules2 = new List<LmsModule>
        {
            M("21", "2", S2, "Wykład 2 – slajdy", LmsModuleType.Resource, "resource", $"https://platon.vistula.edu.pl/mod/resource/view.php?id={courseId}21", null, [F("Wyklad_02.pdf", Pdf, 1_760_000, courseId, mod.AddDays(7))]),
            M("22", "2", S2, "Nagranie wykładu 2 (Teams)", LmsModuleType.Url, "url", "https://web.microsoftstream.com/video/fake", null, []),
            M("23", "2", S2, "Kolokwium próbne", LmsModuleType.Quiz, "quiz", $"https://platon.vistula.edu.pl/mod/quiz/view.php?id={courseId}23", null, [], null, courseId == "101" ? "9001" : courseId == "102" ? "9002" : courseId == "103" ? "9003" : "9099"),
        };
        var modules3 = new List<LmsModule>
        {
            M("31", "3", S3, "Zadanie 2 – projekt końcowy", LmsModuleType.Assign, "assign", $"https://platon.vistula.edu.pl/mod/assign/view.php?id={courseId}31", null, [], null, $"{courseId}002"),
        };
        return
        [
            new LmsSection($"{courseId}-0", S0, null, modules0),
            new LmsSection($"{courseId}-1", S1, "<p>Podstawowe pojęcia i narzędzia.</p>", modules1),
            new LmsSection($"{courseId}-2", S2, null, modules2),
            new LmsSection($"{courseId}-3", S3, null, modules3),
        ];
    }

    public static IReadOnlyList<LmsAssignment> Assignments(DateTimeOffset now)
    {
        string Name(string courseId) => CourseList.First(c => c.Id == courseId).Full;
        return
        [
            new("101001", "101", Name("101"), "Zadanie 1 – pierwsze rozwiązanie", now.AddDays(-6), now.AddDays(-6), true, true, "9/10", "https://platon.vistula.edu.pl/mod/assign/view.php?id=10114"),
            new("101002", "101", Name("101"), "Zadanie 2 – projekt końcowy", now.Date.AddDays(2).AddHours(21), null, false, false, null, "https://platon.vistula.edu.pl/mod/assign/view.php?id=10131"),
            new("102001", "102", Name("102"), "Zadanie 1 – pierwsze rozwiązanie", now.Date.AddDays(4).AddHours(21), null, false, false, null, "https://platon.vistula.edu.pl/mod/assign/view.php?id=10214"),
            new("103001", "103", Name("103"), "Zadanie 1 – pierwsze rozwiązanie", now.AddDays(-2), now.AddDays(-2), true, false, null, "https://platon.vistula.edu.pl/mod/assign/view.php?id=10314"),
            new("104001", "104", Name("104"), "Zadanie 1 – pierwsze rozwiązanie", now.Date.AddDays(12).AddHours(21), null, false, false, null, "https://platon.vistula.edu.pl/mod/assign/view.php?id=10414"),
        ];
    }

    public static IReadOnlyList<LmsEvent> Events(DateTimeOffset now, int days)
    {
        var all = new List<LmsEvent>();
        foreach (var a in Assignments(now).Where(a => !a.Submitted && a.DueDate is not null))
            all.Add(new LmsEvent($"ev-{a.Id}", a.CourseId, a.CourseName, a.Name + " – termin", "due", "assign", a.DueDate!.Value, null, a.Url, "Dodaj pracę"));
        foreach (var q in Quizzes(now).Where(q => q.TimeClose is { } c && c > now))
            all.Add(new LmsEvent($"ev-q{q.Id}", q.CourseId, CourseList.First(c => c.Id == q.CourseId).Full, q.Name + " – zamknięcie", "close", "quiz", q.TimeClose!.Value, null, q.Url, "Rozpocznij test"));
        return all.Where(e => e.Start <= now.AddDays(days)).OrderBy(e => e.Start).ToList();
    }

    public static IReadOnlyList<LmsGradeItem> PlatonGrades(string courseId)
    {
        if (CourseList.All(c => c.Id != courseId)) return [];
        return
        [
            new($"{courseId}-gi1", courseId, "Zadanie 1 – pierwsze rozwiązanie", "mod", "9,00", 9, 10, "90,00 %", "<p>Dobra robota, drobne uwagi do nazewnictwa.</p>", DateTimeOffset.UtcNow.AddDays(-5)),
            new($"{courseId}-gi2", courseId, "Kolokwium próbne", "mod", "7,50", 7.5, 10, "75,00 %", null, DateTimeOffset.UtcNow.AddDays(-12)),
            new($"{courseId}-gi3", courseId, "Aktywność", "manual", "18,00", 18, 20, "90,00 %", null, null),
            new($"{courseId}-total", courseId, "Suma w kursie", "course", "34,50", 34.5, 40, "86,25 %", null, null),
        ];
    }

    public static IReadOnlyList<LmsQuiz> Quizzes(DateTimeOffset now) =>
    [
        new("9001", "10123", "101", "Kolokwium 1 – podstawy programowania obiektowego", "<p>Test obejmuje wykłady 1–2. Czas: 30 minut.</p>",
            now.AddDays(-2), now.AddDays(5), 1800, 2, true, true, "https://platon.vistula.edu.pl/mod/quiz/view.php?id=10123"),
        new("9002", "10223", "102", "Test z SQL – JOIN i agregacje (hasło: test)", null,
            now.AddDays(-1), now.AddDays(9), 1200, 1, false, true, "https://platon.vistula.edu.pl/mod/quiz/view.php?id=10223"),
        new("9003", "10323", "103", "Quiz próbny – elastyczność popytu", null,
            now.AddDays(-30), now.AddDays(-3), null, 0, true, true, "https://platon.vistula.edu.pl/mod/quiz/view.php?id=10323"),
    ];

    // ---------- Pytania testu (HTML w stylu Moodle) ----------

    public static IReadOnlyList<QuizQuestion> QuizQuestions(string quizId, string usage = "q42")
    {
        QuizQuestion Q(int slot, int page, string type, string html, params string[] fields)
            => new(slot, page, type, html, "1", "todo", "Jeszcze nie udzielono odpowiedzi", null, 2, fields);

        string Radio(int slot, params string[] options)
            => string.Concat(options.Select((o, i) =>
                $"""<div class="r{i % 2}"><input type="radio" name="{usage}:{slot}_answer" value="{i}" id="{usage}:{slot}_answer{i}"><label for="{usage}:{slot}_answer{i}">{o}</label></div>"""));

        string Head(int slot, string text) =>
            $"""<div class="que"><div class="content"><div class="formulation clearfix"><input type="hidden" name="{usage}:{slot}_:sequencecheck" value="1"><div class="qtext"><p>{text}</p></div>""";
        const string Tail = "</div></div></div>";

        return
        [
            Q(1, 0, "multichoice",
                Head(1, "Który modyfikator dostępu ogranicza widoczność składowej klasy wyłącznie do tej klasy?") +
                $"""<div class="ablock"><div class="prompt">Wybierz jedną odpowiedź:</div><div class="answer">{Radio(1, "public", "protected", "private", "internal")}</div></div>""" + Tail,
                $"{usage}:1_:sequencecheck", $"{usage}:1_answer"),
            Q(2, 0, "truefalse",
                Head(2, "Klasa abstrakcyjna może zawierać konstruktor.") +
                $"""<div class="ablock"><div class="prompt">Wybierz jedną odpowiedź:</div><div class="answer">{Radio(2, "Prawda", "Fałsz")}</div></div>""" + Tail,
                $"{usage}:2_:sequencecheck", $"{usage}:2_answer"),
            Q(3, 1, "shortanswer",
                Head(3, "Jak nazywa się mechanizm, w którym klasa pochodna zmienia zachowanie metody klasy bazowej? (jedno słowo)") +
                $"""<div class="ablock"><label for="{usage}:3_answer">Odpowiedź:</label> <input type="text" name="{usage}:3_answer" id="{usage}:3_answer" size="30"></div>""" + Tail,
                $"{usage}:3_:sequencecheck", $"{usage}:3_answer"),
            Q(4, 1, "multichoice",
                Head(4, "Które z poniższych są filarami programowania obiektowego? (zaznacz wszystkie poprawne)") +
                $"""<div class="ablock"><div class="prompt">Wybierz co najmniej jedną odpowiedź:</div><div class="answer">""" +
                string.Concat(new[] { "Hermetyzacja", "Polimorfizm", "Kompilacja", "Dziedziczenie" }.Select((o, i) =>
                    $"""<div class="r{i % 2}"><input type="hidden" name="{usage}:4_choice{i}" value="0"><input type="checkbox" name="{usage}:4_choice{i}" value="1" id="{usage}:4_choice{i}"><label for="{usage}:4_choice{i}">{o}</label></div>""")) +
                "</div></div>" + Tail,
                $"{usage}:4_:sequencecheck", $"{usage}:4_choice0", $"{usage}:4_choice1", $"{usage}:4_choice2", $"{usage}:4_choice3"),
            Q(5, 2, "essay",
                Head(5, "Opisz własnymi słowami różnicę między interfejsem a klasą abstrakcyjną.") +
                $"""<div class="ablock"><textarea name="{usage}:5_answer" id="{usage}:5_answer" rows="10" cols="60"></textarea><input type="hidden" name="{usage}:5_answerformat" value="0"></div>""" + Tail,
                $"{usage}:5_:sequencecheck", $"{usage}:5_answer", $"{usage}:5_answerformat"),
        ];
    }

    public static string ReviewHtml(QuizQuestion q) => q.Html + """<div class="feedback"><div class="generalfeedback">Przejrzyj odpowiedź w materiałach z wykładu.</div><div class="rightanswer">Poprawna odpowiedź zależy od typu pytania.</div></div>""";
}
