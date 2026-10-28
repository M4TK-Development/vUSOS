using Vusos.Core.Adapters;
using Vusos.Core.Adapters.Usos;
using Vusos.Core.Domain;

namespace Vusos.Core.Tests.Usos;

public class UsosMockAdapterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2025-11-24T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static UsosMockAdapter Make(DateTimeOffset? now = null) => new(new FixedClock(now ?? Now));

    [Fact]
    public async Task Every_method_returns_data()
    {
        var a = Make();
        Assert.Equal("Anna", (await a.GetProfileAsync()).FirstName);
        Assert.NotEmpty(await a.GetTermsAsync());
        Assert.NotEmpty(await a.GetCoursesAsync());
        Assert.NotEmpty(await a.GetTimetableAsync(new(2025, 11, 24), new(2025, 11, 30)));
        Assert.NotEmpty(await a.GetGradesAsync());
        Assert.NotEmpty(await a.GetPaymentsAsync());
        Assert.NotEmpty(await a.ListMessagesAsync());
        Assert.NotEmpty(await a.GetDeanOfficeContactsAsync());
        Assert.NotEmpty(await a.GetAnnouncementsAsync());
    }

    [Fact]
    public async Task Catalogue_has_the_requested_shape()
    {
        var a = Make();
        var terms = await a.GetTermsAsync();
        Assert.Equal([("2025Z", true), ("2024L", false), ("2024Z", false)], terms.Select(t => (t.Id, t.IsCurrent)));

        var courses = await a.GetCoursesAsync();
        Assert.Equal(6, courses.Select(c => c.CourseId).Distinct().Count());
        Assert.Equal(new HashSet<string> { "Wykład", "Ćwiczenia", "Laboratorium", "Lektorat" }, courses.Select(c => c.ClassType).ToHashSet());
        Assert.All(courses, c => Assert.Equal("2025Z", c.TermId));
        var past = await a.GetCoursesAsync("2024Z");
        Assert.NotEmpty(past);
        Assert.All(past, c => Assert.Equal("2024Z", c.TermId));

        Assert.Equal(5, (await a.GetAnnouncementsAsync()).Count);
        Assert.Equal(2, (await a.GetAnnouncementsAsync(2)).Count);
        Assert.Equal(5, (await a.GetDeanOfficeContactsAsync()).Count);
    }

    [Fact]
    public async Task Profile_and_contacts_use_fictional_example_addresses()
    {
        var a = Make();
        Assert.EndsWith(".example", (await a.GetProfileAsync()).Email);
        Assert.All(await a.GetDeanOfficeContactsAsync(), c => Assert.EndsWith(".example", c.Email));
    }

    [Fact]
    public async Task Output_is_deterministic_for_the_same_clock_and_ids_do_not_depend_on_it()
    {
        var (x, y) = (Make(), Make());
        Assert.Equal(await x.GetPaymentsAsync(), await y.GetPaymentsAsync());
        Assert.Equal(await x.GetGradesAsync(), await y.GetGradesAsync());
        static IEnumerable<(string, DateTimeOffset, string?, string)> Shape(IEnumerable<TimetableEntry> e) =>
            e.Select(t => (t.Id, t.Start, t.Room, string.Join(',', t.Lecturers)));
        Assert.Equal(Shape(await x.GetTimetableAsync(new(2025, 11, 24), new(2025, 12, 7))), Shape(await y.GetTimetableAsync(new(2025, 11, 24), new(2025, 12, 7))));
        Assert.Equal(
            ["mock-msg-1001", "mock-msg-1002", "mock-msg-1003", "mock-msg-1004", "mock-msg-1005", "mock-msg-1006"],
            (await x.ListMessagesAsync()).Select(m => m.Id));
        var later = Make(DateTimeOffset.Parse("2026-03-01T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal((await x.GetPaymentsAsync()).Select(p => p.Id), (await later.GetPaymentsAsync()).Select(p => p.Id));
    }

    [Fact]
    public async Task Timetable_covers_every_weekday_of_the_range_and_no_weekends()
    {
        var from = new DateOnly(2025, 11, 17);
        var to = new DateOnly(2025, 12, 14);
        var entries = await Make().GetTimetableAsync(from, to);
        foreach (var d in WarsawTime.EachDay(from, to))
        {
            var onDay = entries.Where(e => WarsawTime.LocalDateOf(e.Start) == d).ToList();
            if (d.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday) Assert.NotEmpty(onDay);
            else Assert.Empty(onDay);
        }
        Assert.Equal(entries.OrderBy(e => e.Start).Select(e => e.Id), entries.Select(e => e.Id));
        Assert.Equal(entries.Count, entries.Select(e => e.Id).Distinct().Count());
        Assert.All(entries, e => Assert.InRange(WarsawTime.LocalDateOf(e.Start), from, to));
    }

    [Fact]
    public async Task Timetable_has_classes_today_relative_to_the_clock_and_for_arbitrary_dates()
    {
        var a = Make();
        var today = WarsawTime.DateOf(Now);
        Assert.Equal(DayOfWeek.Monday, today.DayOfWeek);
        Assert.NotEmpty(await a.GetTimetableAsync(today, today));
        Assert.True((await a.GetTimetableAsync(new(2027, 5, 3), new(2027, 5, 7))).Count >= 5);
    }

    [Fact]
    public async Task Timetable_uses_the_right_utc_offset_across_the_dst_change_and_valid_intervals()
    {
        var entries = await Make().GetTimetableAsync(new(2026, 3, 25), new(2026, 4, 2));
        Assert.Equal(TimeSpan.FromHours(1), entries.First(e => WarsawTime.LocalDateOf(e.Start) == new DateOnly(2026, 3, 27)).Start.Offset);
        Assert.Equal(TimeSpan.FromHours(2), entries.First(e => WarsawTime.LocalDateOf(e.Start) == new DateOnly(2026, 3, 31)).Start.Offset);
        Assert.All(entries, e => Assert.True(e.End > e.Start));
    }

    [Fact]
    public async Task Timetable_handles_single_day_and_reversed_ranges()
    {
        var a = Make();
        Assert.Equal(3, (await a.GetTimetableAsync(new(2025, 11, 26), new(2025, 11, 26))).Count);
        Assert.Empty(await a.GetTimetableAsync(new(2025, 11, 27), new(2025, 11, 26)));
    }

    [Fact]
    public async Task Grades_use_the_2_to_5_scale_include_a_retake_and_a_sensible_average()
    {
        var a = Make();
        var grades = await a.GetGradesAsync();
        Assert.Equal(15, grades.Count);
        foreach (var t in new[] { "2025Z", "2024L", "2024Z" }) Assert.True(grades.Count(g => g.TermId == t) >= 4);
        var numeric = grades.Select(g => Mappers.NumericGrade(g.ValueSymbol)).OfType<decimal>().ToList();
        Assert.All(numeric, n => Assert.InRange(n, 2m, 5m));
        Assert.Contains(grades, g => g.Passes == false);
        Assert.InRange(Mappers.ComputeAverage(grades.Where(g => g.TermId == "2024Z"))!.Value, 2m, 5m);

        // the retake: 2.0 in session 1, 3.5 in session 2 -> average counts only the latest
        var am2 = grades.Where(g => g.CourseId == "INF-AM2").ToList();
        Assert.Equal(["2.0", "3.5"], am2.Select(g => g.ValueSymbol).Order());
        Assert.Equal(4.0m, Mappers.ComputeAverage(grades.Where(g => g.TermId == "2024L"))); // (3.5 + 4.0 + 5.0 + 3.5) / 4
        Assert.All(await a.GetGradesAsync(["2025Z"]), g => Assert.Equal("2025Z", g.TermId));
        Assert.True(grades.Zip(grades.Skip(1)).All(p => p.First.DateModified >= p.Second.DateModified));
    }

    [Fact]
    public async Task Payments_have_exactly_one_overdue_installment_and_upcoming_ones_dated_relative_to_now()
    {
        var payments = await Make().GetPaymentsAsync();
        var today = WarsawTime.DateOf(Now);
        Assert.Equal(6, payments.Count);
        var overdue = Assert.Single(payments, p => p.State == PaymentState.Overdue);
        Assert.True(overdue.DueDate < today);
        Assert.True(overdue.Interest > 0);
        var upcoming = payments.Where(p => p.State == PaymentState.Unpaid).ToList();
        Assert.NotEmpty(upcoming);
        Assert.All(upcoming, p => Assert.True(p.DueDate > today));
        Assert.All(payments.Where(p => p.State == PaymentState.Paid), p => Assert.Equal(p.Amount, p.Paid));
        Assert.Equal(1250m, overdue.Outstanding);
    }

    [Fact]
    public async Task Messages_from_two_offices_with_two_unread_and_paging()
    {
        var a = Make();
        var list = await a.ListMessagesAsync();
        Assert.Equal(6, list.Count);
        Assert.Equal(new HashSet<string> { "Dziekanat", "Biuro ds. Studenckich" }, list.Select(m => m.From).ToHashSet());
        Assert.Equal(2, list.Count(m => m.Unread));
        Assert.Equal(["mock-msg-1002", "mock-msg-1003"], (await a.ListMessagesAsync(2, 1)).Select(m => m.Id));
        Assert.All(list, m => Assert.True(m.Date <= Now));
    }

    [Fact]
    public async Task Opening_a_message_marks_it_read_and_sending_appends_an_escaped_message()
    {
        var a = Make();
        var first = (await a.ListMessagesAsync())[0];
        var full = await a.GetMessageAsync(first.Id);
        Assert.Contains("<p>", full.BodyHtml);
        Assert.False(full.Thread.Unread);
        Assert.Equal(1, (await a.ListMessagesAsync()).Count(m => m.Unread));

        var id = await a.SendMessageAsync(["dziekanat.informatyka@vistula.example"], "Pytanie", "Treść <b>");
        Assert.Equal("mock-msg-sent-1", id);
        var after = await a.ListMessagesAsync();
        Assert.Equal(7, after.Count);
        Assert.Equal(id, after[0].Id);
        Assert.Equal("<p>Treść &lt;b></p>", (await a.GetMessageAsync(id)).BodyHtml);
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => a.GetMessageAsync("nope"));
    }

    [Fact]
    public async Task Announcements_are_newest_first_with_stable_ids()
    {
        var news = await Make().GetAnnouncementsAsync();
        Assert.Equal(["mock-news-1", "mock-news-2", "mock-news-3", "mock-news-4", "mock-news-5"], news.Select(n => n.Id));
        Assert.True(news.Zip(news.Skip(1)).All(p => p.First.PublishedAt > p.Second.PublishedAt));
    }
}
