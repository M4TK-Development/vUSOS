using Vusos.Core.Adapters.Usos;
using Vusos.Core.Domain;

namespace Vusos.Core.Tests.Usos;

/// <summary>Parsers run against hand-written fixtures; the selectors are unverified against the live USOSweb.</summary>
public class ParsersTests
{
    private const string Base = "https://usosweb.vistula.example/";
    private static readonly ParseOptions Opts = new(Base);
    private static string Iso(DateTimeOffset? d) => WarsawTime.ToIso(d!.Value);

    // ---------- plan ----------

    [Fact]
    public void ParsePlan_reads_the_day_column_table()
    {
        var entries = Parsers.ParsePlan(Fx.Text("plan-week.html"), Opts);
        Assert.Equal(5, entries.Count);
        Assert.Equal(entries.OrderBy(e => e.Start).Select(e => e.Id), entries.Select(e => e.Id));

        var alg = entries[0];
        Assert.Equal("2025-10-06T08:30:00+02:00", Iso(alg.Start));
        Assert.Equal("2025-10-06T10:00:00+02:00", Iso(alg.End));
        Assert.Equal(("INF-ALG", "Wykład", "A-214", "Budynek A", (int?)null), (alg.CourseId, alg.ClassType, alg.Room, alg.Building, alg.GroupNumber));
        Assert.Equal(new LangText("Algorytmy i struktury danych"), alg.CourseName);
        Assert.Equal(["prof. dr hab. Marek Nowicki"], alg.Lecturers);
        Assert.Equal($"{Base}kontroler.php?_action=katalog2/przedmioty/pokazPrzedmiot&prz_kod=INF-ALG", alg.Url);

        var mat = entries.Single(e => e.CourseId == "INF-MAT");
        Assert.Equal((2, "Ćwiczenia", "B-105"), (mat.GroupNumber, mat.ClassType, mat.Room));
    }

    [Fact]
    public void ParsePlan_handles_single_digit_hours_and_does_not_shift_columns_over_empty_cells()
    {
        var entries = Parsers.ParsePlan(Fx.Text("plan-week.html"), Opts);
        var bd = entries.Single(e => e.CourseId == "INF-BD");
        Assert.Equal(("2025-10-07T09:00:00+02:00", "2025-10-07T10:30:00+02:00"), (Iso(bd.Start), Iso(bd.End)));
        // Friday is column index 4: the empty Thursday cell must not shift it
        Assert.Equal("2025-10-10T10:00:00+02:00", Iso(entries.Single(e => e.CourseId == "INF-ANG").Start));
        Assert.Equal(entries.Count, entries.Select(e => e.Id).Distinct().Count());
    }

    [Fact]
    public void ParsePlan_reads_data_start_data_end_activities()
    {
        var entries = Parsers.ParsePlan(Fx.Text("plan-activities.html"));
        Assert.Equal(3, entries.Count);
        var lab = entries[1];
        Assert.Equal(("Laboratorium", 2, "INF-BD"), (lab.ClassType, lab.GroupNumber, lab.CourseId));
        Assert.Equal(["Katarzyna Wójcik", "Jan Zając"], lab.Lecturers);
        Assert.Equal((null, new LangText("Egzamin próbny")), (entries[2].CourseId, entries[2].CourseName));
    }

    [Fact]
    public void ParsePlan_reads_embedded_json_and_skips_broken_items()
    {
        var entries = Parsers.ParsePlan(Fx.Text("plan-json.html"));
        Assert.Equal(2, entries.Count);
        Assert.Equal(new LangText("Algorytmy i struktury danych", "Algorithms and data structures"), entries[0].CourseName);
        Assert.Equal("A-214", entries[0].Room);
        Assert.Equal("Ćwiczenia", entries[1].ClassType);
    }

    [Fact]
    public void ParsePlan_returns_empty_for_unknown_markup_and_the_login_page()
    {
        Assert.Empty(Parsers.ParsePlan("<html><body><p>nic</p></body></html>"));
        Assert.Empty(Parsers.ParsePlan(""));
        Assert.Empty(Parsers.ParsePlan(Fx.Text("login.html")));
    }

    // ---------- grades ----------

    private static readonly ParsedGrades ParsedGrades = Parsers.ParseGrades(Fx.Text("grades.html"));

    [Fact]
    public void ParseGrades_finds_terms_from_headings_and_ignores_unrelated_sections() =>
        Assert.Equal([new ParsedTerm("2025Z", "Semestr zimowy 2025/26"), new ParsedTerm("2024L", "Semestr letni 2024/25")], ParsedGrades.Terms);

    [Fact]
    public void ParseGrades_maps_rows_to_grades()
    {
        var grades = ParsedGrades.Grades;
        Assert.Equal(5, grades.Count);
        var g = grades[0];
        Assert.Equal(("2025Z", "INF-PROG1", "Laboratorium", 1, "4.5"), (g.TermId, g.CourseId, g.ClassType, g.ExamSession, g.ValueSymbol));
        Assert.Equal(new LangText("Programowanie obiektowe"), g.CourseName);
        Assert.Equal(new LangText("dobry plus", "good plus"), g.ValueDescription);
        Assert.Equal((true, true, "mgr inż. Katarzyna Wójcik"), (g.Passes, g.CountsIntoAverage, g.GradedBy));
        Assert.Equal("2025-11-12T00:00:00+01:00", Iso(g.DateModified));

        var zal = grades.Single(x => x.CourseId == "INF-ANG");
        Assert.Equal(("zal", false, true), (zal.ValueSymbol, zal.CountsIntoAverage, zal.Passes));
        Assert.Equal("2025-11-12T00:00:00+01:00", Iso(zal.DateModified)); // dd.MM.yyyy

        var retake = grades.Where(x => x.CourseId == "INF-ANM").ToList();
        Assert.Equal([(1, "2.0", (bool?)false), (2, "3.5", true)], retake.Select(x => (x.ExamSession ?? 0, x.ValueSymbol, x.Passes)));
        Assert.Equal("web:podstawy-programowania", grades.Single(x => x.CourseName.Pl == "Podstawy programowania").CourseId);
        Assert.Equal(grades.Count, grades.Select(x => x.Id).Distinct().Count());
    }

    [Fact]
    public void ParseGrades_returns_empty_results_for_unknown_markup()
    {
        var unknown = Parsers.ParseGrades("<table><tr><td>x</td></tr></table>");
        Assert.Empty(unknown.Terms);
        Assert.Empty(unknown.Grades);
        Assert.Empty(Parsers.ParseGrades("").Grades);
    }

    // ---------- payments ----------

    private static readonly DateOnly PayToday = new(2025, 11, 20);

    [Fact]
    public void ParsePayments_maps_rows_amounts_states_and_the_account_number()
    {
        var p = Parsers.ParsePayments(Fx.Text("payments.html"), PayToday);
        Assert.Equal(5, p.Count);
        Assert.Equal((1250m, 1250m, PaymentState.Paid, new DateOnly(2025, 10, 10), "tuition", 0m),
            (p[0].Amount, p[0].Paid, p[0].State, p[0].DueDate!.Value, p[0].Type, p[0].Interest!.Value));
        Assert.Equal((PaymentState.Overdue, new DateOnly(2025, 11, 10), 12.4m, 0m), (p[1].State, p[1].DueDate!.Value, p[1].Interest!.Value, p[1].Paid));
        Assert.Equal((PaymentState.PartiallyPaid, 500m, new DateOnly(2025, 12, 10)), (p[2].State, p[2].Paid, p[2].DueDate!.Value));
        Assert.Equal((PaymentState.Cancelled, 22m, "id_card", (decimal?)null), (p[3].State, p[3].Amount, p[3].Type, p[3].Interest));
        Assert.Equal((PaymentState.Unpaid, new DateOnly(2026, 1, 10)), (p[4].State, p[4].DueDate!.Value));
        Assert.All(p, x => Assert.Equal("12345678901234567890123456", x.AccountNumber));
        Assert.Equal(5, p.Select(x => x.Id).Distinct().Count());
    }

    [Fact]
    public void ParsePayments_flips_unpaid_to_overdue_after_the_deadline_and_tolerates_junk()
    {
        var late = Parsers.ParsePayments(Fx.Text("payments.html"), new DateOnly(2026, 1, 11));
        Assert.Equal(PaymentState.Overdue, late.Single(p => p.DueDate == new DateOnly(2026, 1, 10)).State);
        Assert.Empty(Parsers.ParsePayments("<p>nic</p>", PayToday));
    }

    // ---------- news / profile ----------

    [Fact]
    public void ParseNews_maps_items_newest_first()
    {
        var news = Parsers.ParseNews(Fx.Text("home.html"), Opts);
        Assert.Equal(["Zapisy na seminaria dyplomowe", "Zmiana godzin pracy dziekanatu", "Stypendia rektora - wnioski"], news.Select(n => n.Title));
        var n0 = news[0];
        Assert.Equal(("usos-web-news:9001", "Dziekanat", "Dydaktyka", SourceSystem.Usos), (n0.Id, n0.Author, n0.Category, n0.Source));
        Assert.Equal("2025-10-20T00:00:00+02:00", Iso(n0.PublishedAt));
        Assert.Equal($"{Base}kontroler.php?_action=home/aktualnosc&id=9001", n0.Url);
        Assert.Contains("<strong>27 października</strong>", n0.BodyHtml);
        Assert.Equal("2025-10-14T09:30:00+02:00", Iso(news[1].PublishedAt)); // <time datetime>
        Assert.Equal(("2025-10-01T00:00:00+02:00", null, null), (Iso(news[2].PublishedAt), news[2].Author, news[2].Url));
        Assert.Empty(Parsers.ParseNews("<div></div>"));
    }

    [Fact]
    public void ParseNews_never_hands_out_javascript_links()
    {
        var html = """<div class="news-item"><h3 class="title"><a href="javascript:alert(1)">Tytuł</a></h3></div>""";
        Assert.Null(Assert.Single(Parsers.ParseNews(html, Opts)).Url);
        Assert.Null(Assert.Single(Parsers.ParseNews(html)).Url);
    }

    [Fact]
    public void ParseProfile_reads_name_album_number_and_programmes()
    {
        var p = Parsers.ParseProfile(Fx.Text("home.html"))!;
        Assert.Equal(("61234", "Anna", "Nowak", "61234"), (p.Id, p.FirstName, p.LastName, p.StudentNumber));
        Assert.Equal([new Programme("INF-I-S", new("Informatyka"), null, "studia I stopnia (stacjonarne)")], p.Programmes);
        Assert.Null(Parsers.ParseProfile("<html></html>"));
    }

    // ---------- messages ----------

    [Fact]
    public void ParseMessages_reads_the_list_with_unread_detection_skipping_rows_without_an_id()
    {
        var list = Parsers.ParseMessages(Fx.Text("messages.html"), Opts);
        Assert.Equal([("5501", true, "Dziekanat"), ("5490", false, "Biuro ds. Studenckich")], list.Select(m => (m.Id, m.Unread, m.From)));
        Assert.Equal("Potwierdzenie złożenia wniosku", list[0].Subject);
        Assert.Equal("2025-10-21T13:05:00+02:00", Iso(list[0].Date));
        Assert.Empty(Parsers.ParseMessages("<table><tr><td>x</td></tr></table>"));
    }

    [Fact]
    public void ParseMessage_reads_a_single_message_with_attachments()
    {
        var m = Parsers.ParseMessage(Fx.Text("message.html"), "5501", Opts)!;
        Assert.Equal(("Potwierdzenie złożenia wniosku", "Dziekanat", "5501"), (m.Thread.Subject, m.Thread.From, m.Thread.Id));
        Assert.Equal(["Anna Nowak"], m.Thread.To);
        Assert.Equal("2025-10-21T13:05:00+02:00", Iso(m.Thread.Date));
        Assert.Contains("W/2025/114", m.BodyHtml);
        Assert.Equal([new Attachment("potwierdzenie.pdf", $"{Base}kontroler.php?_action=dla_stud/studia/komunikacja&pobierz=77", null)], m.Attachments);
        Assert.Null(Parsers.ParseMessage("<p>x</p>", "1"));
    }

    // ---------- login detection and term helpers ----------

    [Fact]
    public void Login_redirects_and_login_pages_are_recognised()
    {
        Assert.True(Parsers.IsLoginRedirect("https://usosweb.vistula.example/kontroler.php?_action=logowanie/index"));
        Assert.True(Parsers.IsLoginRedirect("https://login.microsoftonline.com/common/oauth2/authorize?x=1"));
        Assert.False(Parsers.IsLoginRedirect("/kontroler.php?_action=home/index"));
        Assert.True(Parsers.LooksLikeLoginPage(Fx.Text("login.html")));
        Assert.False(Parsers.LooksLikeLoginPage(Fx.Text("home.html")));
        Assert.True(Parsers.LooksLikeLoginPage("""<meta http-equiv="refresh" content="0;url=https://login.microsoftonline.com/x">Wyloguj"""));
    }

    [Fact]
    public void Term_ids_dates_and_current_term_are_derived_from_the_academic_calendar()
    {
        Assert.Equal("2025Z", Parsers.TermIdFromText("Semestr zimowy 2025/26 (2025Z)"));
        Assert.Equal("2024L", Parsers.TermIdFromText("Semestr letni 2024/25"));
        Assert.Null(Parsers.TermIdFromText("Informacje dodatkowe"));
        Assert.Equal("2025Z", Parsers.TermIdForDate(new(2025, 11, 20)));
        Assert.Equal("2025Z", Parsers.TermIdForDate(new(2026, 1, 10)));
        Assert.Equal("2025L", Parsers.TermIdForDate(new(2026, 4, 10)));
        var t = Parsers.TermFromId("2025Z", new(2025, 11, 20))!;
        Assert.Equal((new DateOnly(2025, 10, 1), new DateOnly(2026, 2, 15), true, "Semestr zimowy 2025/26"), (t.StartDate, t.EndDate, t.IsCurrent, t.Name.Pl));
        Assert.Equal(new DateOnly(2025, 2, 16), Parsers.TermFromId("2024L", new(2025, 11, 20))!.StartDate);
        Assert.Null(Parsers.TermFromId("bogus", new(2025, 11, 20)));
    }

    [Theory]
    [InlineData("10.11.2025", "2025-11-10")]
    [InlineData("2025-10-21 13:05", "2025-10-21 13:05:00")]
    [InlineData("14.10.2025 9:30", "2025-10-14 09:30:00")]
    [InlineData("2025-10-14T09:30:00", "2025-10-14 09:30:00")]
    public void NormalizeLocalDate_understands_polish_and_iso_formats(string input, string expected) =>
        Assert.Equal(expected, Parsers.NormalizeLocalDate(input));

    [Fact]
    public void ParseMoney_strips_currency_and_spaces()
    {
        Assert.Equal(1250m, Parsers.ParseMoney("1 250,00 zł"));
        Assert.Equal(12.4m, Parsers.ParseMoney("12,40 PLN"));
        Assert.Null(Parsers.ParseMoney(""));
    }
}
