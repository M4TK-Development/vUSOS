using Vusos.Core.Adapters;
using Vusos.Core.Adapters.Usos;
using Vusos.Core.Domain;

namespace Vusos.Core.Tests.Usos;

public class MappersTests
{
    private static string Iso(DateTimeOffset? d) => WarsawTime.ToIso(d!.Value);

    [Fact]
    public void ToLangText_accepts_strings_lang_objects_and_junk()
    {
        Assert.Equal(new LangText("a"), Mappers.ToLangText(Fx.Parse("\"a\"")));
        Assert.Equal(new LangText("a", "b"), Mappers.ToLangText(Fx.Parse("""{"pl":"a","en":"b"}""")));
        Assert.Equal(new LangText("only en", "only en"), Mappers.ToLangText(Fx.Parse("""{"en":"only en"}""")));
        Assert.Equal(new LangText("fb"), Mappers.ToLangText(null, "fb"));
        Assert.Equal(new LangText("fb"), Mappers.ToLangText(Fx.Parse("42"), "fb"));
    }

    [Theory]
    [InlineData("1 250,00", 1250.00)]
    [InlineData("-1250,50", -1250.5)]
    [InlineData(" 4.5 ", 4.5)]
    [InlineData("1 000", 1000)]
    public void ToNumber_parses_polish_and_plain_numbers(string text, double expected) =>
        Assert.Equal((decimal)expected, Mappers.ToNumber(text));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    public void ToNumber_returns_null_for_blank_or_garbage(string text) => Assert.Null(Mappers.ToNumber(text));

    [Fact]
    public void MapProfile_maps_users_user()
    {
        var p = Mappers.MapProfile(Fx.Json("user.json"));
        Assert.Equal("1234567", p.Id);
        Assert.Equal("Anna", p.FirstName);
        Assert.Equal("Nowak", p.LastName);
        Assert.Equal("61234", p.StudentNumber);
        Assert.Equal("anna.nowak@stu.vistula.example", p.Email);
        Assert.Equal("https://usosapps.vistula.example/photos/1234567_200.jpg", p.PhotoUrl);
        Assert.Equal(
            [new Programme("98765", new("Informatyka", "Computer Science"), "active", "Studia I stopnia, stacjonarne")],
            p.Programmes);
    }

    [Fact]
    public void MapProfile_nulls_invalid_email_and_photo_and_rejects_garbage()
    {
        var p = Mappers.MapProfile(Fx.Parse("""{"id":1,"first_name":"A","last_name":"B","email":"nope","photo_urls":{"50x50":"not a url"}}"""));
        Assert.Null(p.Email);
        Assert.Null(p.PhotoUrl);
        Assert.Equal("1", p.Id);
        var ex = Assert.Throws<AdapterUnavailableException>(() => Mappers.MapProfile(Fx.Parse("""{"nothing":true}""")));
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public void MapTerms_sorts_newest_first_skips_incomplete_and_flags_current_via_finish_date()
    {
        var raw = Fx.Json("courses-user.json").GetProperty("terms");
        var terms = Mappers.MapTerms(raw, new DateOnly(2026, 2, 10));
        Assert.Equal(["2025Z", "2024L"], terms.Select(t => t.Id));
        Assert.True(terms[0].IsCurrent); // after end_date, before finish_date
        Assert.False(terms[1].IsCurrent);
        Assert.Equal(new DateOnly(2025, 10, 1), terms[0].StartDate);
        Assert.Equal(new DateOnly(2026, 1, 25), terms[0].EndDate);
        Assert.True(Mappers.MapTerms(raw, new DateOnly(2025, 11, 1))[0].IsCurrent);
        Assert.False(Mappers.MapTerms(raw, new DateOnly(2026, 3, 1))[0].IsCurrent);
    }

    [Fact]
    public void MapCourseUnits_flattens_a_dict_of_terms_into_one_unit_per_group()
    {
        var units = Mappers.MapCourseUnits(Fx.Json("courses-user.json"));
        var alg = units.Where(u => u.CourseId == "INF-ALG").ToList();
        Assert.Equal([("5201", "Wykład", (int?)1), ("5202", "Ćwiczenia", 1)], alg.Select(u => (u.Id, u.ClassType, u.GroupNumber)));
        Assert.Equal(["Tomasz Lis", "Jan Zając"], alg[1].Lecturers);
        Assert.Equal("2025Z", alg[0].TermId);
        Assert.Equal(new LangText("Algorytmy i struktury danych", "Algorithms and data structures"), alg[0].CourseName);
    }

    [Fact]
    public void MapCourseUnits_emits_placeholder_for_editions_without_groups_and_takes_term_from_dict_key()
    {
        var units = Mappers.MapCourseUnits(Fx.Json("courses-user.json"));
        var ang = Assert.Single(units, u => u.CourseId == "INF-ANG");
        Assert.Equal(("INF-ANG@2025Z", "", (int?)null), (ang.Id, ang.ClassType, ang.GroupNumber));
        Assert.Equal("2024L", units.Single(u => u.CourseId == "INF-AM2").TermId);
    }

    [Fact]
    public void MapCourseUnits_filters_by_term_tolerates_flat_list_and_rejects_non_objects()
    {
        var raw = Fx.Json("courses-user.json");
        Assert.Equal(["INF-AM2"], Mappers.MapCourseUnits(raw, "2024L").Select(u => u.CourseId));
        var flat = Fx.Parse("""{"course_editions":[{"course_id":"X","course_name":"X","term_id":"2025Z","user_groups":[]}]}""");
        Assert.Single(Mappers.MapCourseUnits(flat, "2025Z"));
        Assert.Throws<AdapterUnavailableException>(() => Mappers.MapCourseUnits(Fx.Parse("\"garbage\"")));
    }

    [Fact]
    public void MapActivities_converts_to_warsaw_offset_resolves_lecturers_and_skips_junk()
    {
        var raw = Fx.Json("tt-user.json");
        Assert.Equal(["101", "102"], Mappers.CollectLecturerIds(Mappers.Items(raw)));
        var names = new Dictionary<string, string> { ["101"] = "Marek Nowicki", ["102"] = "Tomasz Lis" };
        var entries = Mappers.MapActivities(Mappers.Items(raw), names);

        Assert.Equal(2, entries.Count);
        var e = entries[0];
        Assert.Equal(SourceSystem.Usos, e.Source);
        Assert.Equal("2025-10-06T08:30:00+02:00", Iso(e.Start));
        Assert.Equal("2025-10-06T10:00:00+02:00", Iso(e.End));
        Assert.Equal("INF-ALG", e.CourseId);
        Assert.Equal(new LangText("Algorytmy i struktury danych", "Algorithms and data structures"), e.CourseName);
        Assert.Equal(("Wykład", 1, "A-214", "Budynek A"), (e.ClassType, e.GroupNumber, e.Room, e.Building));
        Assert.Equal(["Marek Nowicki", "Tomasz Lis"], e.Lecturers);
        Assert.Equal("usos-tt:5201:20251006083000", e.Id);

        var exam = entries[1];
        Assert.Equal((null, null, null, null), (exam.CourseId, exam.ClassType, exam.Room, exam.Building));
        Assert.Equal(new LangText("Egzamin próbny"), exam.CourseName);
        Assert.Equal(2, entries.Select(x => x.Id).Distinct().Count());
    }

    [Fact]
    public void MapActivities_leaves_lecturers_empty_when_names_are_unresolved() =>
        Assert.Empty(Mappers.MapActivities(Mappers.Items(Fx.Json("tt-user.json")))[0].Lecturers);

    private static IReadOnlyList<Grade> Grades() => Mappers.MapGrades(Fx.Json("grades-terms2.json"), new GradeMapContext(
        new Dictionary<string, LangText> { ["INF-ALG"] = new("Algorytmy i struktury danych") },
        new Dictionary<string, string> { ["5202"] = "Ćwiczenia" }));

    [Fact]
    public void MapGrades_flattens_unit_and_course_grades_skipping_nulls()
    {
        var grades = Grades();
        Assert.Equal(5, grades.Count);
        Assert.Equal(5, grades.Select(g => g.Id).Distinct().Count());
    }

    [Fact]
    public void MapGrades_maps_fields()
    {
        var grades = Grades();
        var unit = grades.Single(g => g.CourseId == "INF-ALG" && g.ClassType == "Ćwiczenia");
        Assert.Equal("2025Z", unit.TermId);
        Assert.Equal(new LangText("Algorytmy i struktury danych"), unit.CourseName);
        Assert.Equal((1, "4.0", true, true), (unit.ExamSession, unit.ValueSymbol, unit.Passes, unit.CountsIntoAverage));
        Assert.Equal(new LangText("dobry", "good"), unit.ValueDescription);
        Assert.Equal("2025-11-19T12:05:00+01:00", Iso(unit.DateModified));
        Assert.Equal(("Tomasz Lis", "Kolokwium 1: 78%"), (unit.GradedBy, unit.Comment));
        Assert.Equal("usos-grade:2025Z:INF-ALG:5202:1", unit.Id);

        var courseGrade = grades.Single(g => g.CourseId == "INF-ALG" && g.ClassType is null);
        Assert.True(courseGrade.CountsIntoAverage); // "Y"

        var am2 = grades.Single(g => g.CourseId == "INF-AM2" && g.ExamSession == 1);
        Assert.Equal(false, am2.Passes);
        Assert.Equal(new LangText("INF-AM2"), am2.CourseName); // not in the name index -> falls back to the id
    }

    [Fact]
    public void MapGrades_handles_list_shaped_sessions_and_non_counting_pass_fail_grades()
    {
        var zal = Grades().Single(g => g.CourseId == "INF-ANG2");
        Assert.Equal(("zal", false, 1), (zal.ValueSymbol, zal.CountsIntoAverage, zal.ExamSession));
    }

    [Fact]
    public void MapGrades_returns_empty_for_non_object_input()
    {
        Assert.Empty(Mappers.MapGrades(Fx.Parse("null")));
        Assert.Empty(Mappers.MapGrades(Fx.Parse("[]")));
        Assert.Empty(Mappers.MapGrades(default));
    }

    [Fact]
    public void Grade_symbols_are_normalised_and_described()
    {
        Assert.Equal("4.5", Mappers.NormalizeGradeSymbol("4,5"));
        Assert.Equal("5.0", Mappers.NormalizeGradeSymbol("5"));
        Assert.Equal("zal", Mappers.NormalizeGradeSymbol("ZAL"));
        Assert.Equal("nzal", Mappers.NormalizeGradeSymbol("Niezaliczone"));
        Assert.Equal("ZW", Mappers.NormalizeGradeSymbol(" ZW "));
        Assert.Null(Mappers.NumericGrade("5!"));
        var fail = Mappers.DescribeGrade("2.0");
        Assert.Equal((false, "niedostateczny"), (fail.Passes, fail.Description?.Pl));
        Assert.True(Mappers.DescribeGrade("3.0").Passes);
        Assert.Equal(new LangText("zaliczony", "passed"), Mappers.DescribeGrade("zal").Description);
        Assert.Null(Mappers.DescribeGrade("ZW").Passes);
    }

    [Fact]
    public void ComputeAverage_counts_only_numeric_counted_grades_and_the_latest_session()
    {
        // 2025Z: INF-ALG unit 4.0 + course 3.5 (different class-type keys); 2024L: AM2 latest = 3.5; zal ignored
        Assert.Equal(3.67m, Mappers.ComputeAverage(Grades()));
        Assert.Null(Mappers.ComputeAverage([]));
    }

    private static readonly DateOnly PaymentsToday = new(2025, 11, 20);

    [Fact]
    public void MapPayments_skips_junk_and_keeps_order()
    {
        var payments = Mappers.MapPayments(Mappers.Items(Fx.Json("payments.json")), PaymentsToday);
        Assert.Equal(["7001", "7002", "7003", "7004", "7005", "7006"], payments.Select(p => p.Id));
    }

    [Fact]
    public void MapPayments_derives_state_paid_amount_and_overdue_from_the_deadline()
    {
        var by = Mappers.MapPayments(Mappers.Items(Fx.Json("payments.json")), PaymentsToday).ToDictionary(p => p.Id);

        Assert.Equal((PaymentState.Paid, 1250m, 1250m, "61109010140000071219812874"), (by["7001"].State, by["7001"].Paid, by["7001"].Amount, by["7001"].AccountNumber));
        Assert.Equal((PaymentState.Overdue, 0m, 4.73m, new DateOnly(2025, 11, 10)), (by["7002"].State, by["7002"].Paid, by["7002"].Interest, by["7002"].DueDate));
        Assert.Equal((PaymentState.PartiallyPaid, 500m), (by["7003"].State, by["7003"].Paid)); // future deadline, partly paid
        Assert.Equal((1250m, "Czesne - rata 4/5", PaymentState.Unpaid), (by["7004"].Amount, by["7004"].Description.Pl, by["7004"].State));
        Assert.Equal(PaymentState.Cancelled, by["7005"].State);
        Assert.Equal((100m, PaymentState.Paid, (DateOnly?)null, "PLN"), (by["7006"].Amount, by["7006"].State, by["7006"].DueDate, by["7006"].Currency));
        Assert.Equal(1250m - 0m, by["7002"].Outstanding);
    }

    private static List<System.Text.Json.JsonElement> Mail() => Fx.Json("mail-messages.json").GetProperty("messages").EnumerateArray().ToList();

    [Fact]
    public void MapMessageThread_maps_sender_recipients_date_and_unread()
    {
        var list = Mail();
        var t = Mappers.MapMessageThread(list[0])!;
        Assert.Equal(("5501", SourceSystem.Usos, "Dziekanat Informatyki", true), (t.Id, t.Source, t.From, t.Unread));
        Assert.Equal(["Anna Nowak"], t.To);
        Assert.Equal("2025-10-21T13:05:00+02:00", WarsawTime.ToIso(t.Date));
        Assert.Equal("Twój wniosek został zarejestrowany.", t.Preview);

        var second = Mappers.MapMessageThread(list[1])!;
        Assert.Equal(("Biuro ds. Studenckich", false, "Sesja zimowa rozpocznie się..."), (second.From, second.Unread, second.Preview));
        Assert.Null(Mappers.MapMessageThread(Fx.Parse("""{"nope":1}""")));
    }

    [Fact]
    public void MapMessage_maps_body_and_attachments_and_rejects_garbage()
    {
        var m = Mappers.MapMessage(Mail()[0]);
        Assert.Equal("<p>Twój wniosek został zarejestrowany.</p>", m.BodyHtml);
        Assert.Equal([new Attachment("potwierdzenie.pdf", "https://usosapps.vistula.example/att/1", 20480)], m.Attachments);
        Assert.Throws<AdapterUnavailableException>(() => Mappers.MapMessage(Fx.Parse("{}")));
    }

    [Fact]
    public void MapFacultyContacts_returns_one_dean_office_contact_per_faculty()
    {
        var c = Assert.Single(Mappers.MapFacultyContacts(Fx.Json("faculty.json")));
        Assert.Equal(("usos-fac:WI", "Wydział Informatyki", "Dziekanat", "+48 22 000 01 01", (string?)null), (c.Id, c.Name, c.Role, c.Phone, c.Email));
        Assert.Equal("ul. Stokłosy 3, 02-787 Warszawa", c.Room);
        Assert.Empty(Mappers.MapFacultyContacts(Fx.Parse("\"x\"")));
    }

    [Fact]
    public void MapNews_sorts_newest_first_and_handles_lang_text_and_plain_strings()
    {
        var news = Mappers.MapNews(Mappers.Items(Fx.Json("news.json")));
        Assert.Equal(["usos-news:9001", "usos-news:9002"], news.Select(n => n.Id));
        var n0 = news[0];
        Assert.Equal(("Zapisy na seminaria", "<p>Zapisy od poniedziałku.</p>", "Jan Kowalski", "Dydaktyka"), (n0.Title, n0.BodyHtml, n0.Author, n0.Category));
        Assert.Equal("2025-10-20T10:00:00+02:00", WarsawTime.ToIso(n0.PublishedAt));
        Assert.Equal("https://usosweb.vistula.example/news/9001", n0.Url);
        Assert.Equal(((string?)null, (string?)null), (news[1].Author, news[1].Category));
        Assert.Equal("2025-10-14T00:00:00+02:00", WarsawTime.ToIso(news[1].PublishedAt));
    }
}
