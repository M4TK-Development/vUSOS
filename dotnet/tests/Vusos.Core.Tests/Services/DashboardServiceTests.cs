using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Services;

namespace Vusos.Core.Tests.Services;

public class DashboardServiceTests
{
    private readonly FakeClock _clock = new() { Now = new DateTimeOffset(2026, 10, 28, 23, 30, 0, TimeSpan.Zero) }; // 00:30 następnego dnia w Warszawie
    private readonly SyncStatus _sync = new();
    private readonly FakeUsosAdapter _usos = new();
    private readonly FakePlatonAdapter _platon = new();
    private readonly StubConnectionManager _cm = new();

    private DashboardService Create(bool usos = true, bool platon = true)
    {
        _cm.Usos = usos ? _usos : null;
        _cm.Platon = platon ? _platon : null;
        return new DashboardService(_cm, _sync, _clock);
    }

    private static readonly DateTimeOffset T = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private static Grade G(string id, int daysAgo) =>
        new(id, "t", "c", LangText.Of("Przedmiot " + id), null, 1, "5", null, true, true, T.AddDays(-daysAgo), null, null);

    private static Payment P(string id, PaymentState state, int dueDay) =>
        new(id, "czesne", LangText.Of(id), 100, state == PaymentState.Paid ? 100 : 0, "PLN", new DateOnly(2026, 11, dueDay), state, null, null);

    private static Announcement N(string id, SourceSystem s, int daysAgo) =>
        new(id, s, "Tytuł " + id, "<p/>", T.AddDays(-daysAgo), null, null, null);

    private void Seed()
    {
        _usos.Timetable =
        [
            new TimetableEntry("late", SourceSystem.Usos, T.AddHours(6), T.AddHours(7), null, LangText.Of("B"), null, null, null, null, [], null),
            new TimetableEntry("early", SourceSystem.Usos, T.AddHours(1), T.AddHours(2), null, LangText.Of("A"), null, null, null, null, [], null),
        ];
        _usos.Grades = Enumerable.Range(1, 7).Select(i => G("g" + i, 20 - i * 2)).ToList(); // g7 najnowsza
        _usos.Payments = [P("paid", PaymentState.Paid, 1), P("overdue", PaymentState.Overdue, 2), P("unpaid-late", PaymentState.Unpaid, 20),
                          P("cancelled", PaymentState.Cancelled, 3), P("partial", PaymentState.PartiallyPaid, 10)];
        _usos.Messages = [new MessageThread("1", SourceSystem.Usos, "a", "x", [], T, "", true, null),
                          new MessageThread("2", SourceSystem.Usos, "b", "x", [], T, "", false, null),
                          new MessageThread("3", SourceSystem.Usos, "c", "x", [], T, "", true, null)];
        _usos.Announcements = [N("u1", SourceSystem.Usos, 1), N("u2", SourceSystem.Usos, 5), N("u3", SourceSystem.Usos, 9)];
        _platon.Announcements = [N("p1", SourceSystem.Platon, 2), N("p2", SourceSystem.Platon, 3), N("p3", SourceSystem.Platon, 4)];
        _platon.Events =
        [
            new LmsEvent("e2", "c", "C", "Później", "due", null, T.AddDays(3), null, null, null),
            new LmsEvent("e1", "c", "C", "Wcześniej", "due", null, T.AddDays(1), null, null, null),
        ];
    }

    [Fact]
    public async Task Aggregates_AllParts()
    {
        Seed();
        _sync.Report(SourceSystem.Platon, DataOrigin.Cache, T);

        var d = await Create().GetAsync();

        Assert.Equal("Jan Kowalski", d.Profile!.FullName);
        Assert.Equal(["early", "late"], d.TodayClasses.Select(c => c.Id));
        Assert.Equal(["e1", "e2"], d.UpcomingDeadlines.Select(e => e.Id));
        Assert.Equal(["g7", "g6", "g5", "g4", "g3"], d.RecentGrades.Select(g => g.Id));
        Assert.Equal(["overdue", "partial", "unpaid-late"], d.PaymentsDue.Select(p => p.Id));
        Assert.Equal(2, d.UnreadMessages);
        Assert.Equal(["u1", "p1", "p2", "p3", "u2"], d.Announcements.Select(a => a.Id));
        Assert.Equal(ConnectionStatus.Connected, d.Connections[SourceSystem.Usos]);
        Assert.Equal(ConnectionStatus.Connected, d.Connections[SourceSystem.Platon]);
        Assert.Equal(DataOrigin.Cache, d.Origins[SourceSystem.Platon]);
        Assert.Equal(DataOrigin.Live, d.Origins[SourceSystem.Usos]);
    }

    [Fact]
    public async Task TodayIsComputedInWarsawTime()
    {
        Seed();
        await Create().GetAsync();
        // 23:30 UTC 28.10 to już 29.10 w Warszawie (CET)
        Assert.Contains("GetTimetable:2026-10-29", _usos.Calls);
    }

    [Fact]
    public async Task FailingUsos_YieldsEmptyUsosParts_AndKeepsPlatonParts()
    {
        Seed();
        _usos.Failure = new AdapterUnavailableException("USOS leży");

        var d = await Create().GetAsync();

        Assert.Null(d.Profile);
        Assert.Empty(d.TodayClasses);
        Assert.Empty(d.RecentGrades);
        Assert.Empty(d.PaymentsDue);
        Assert.Equal(0, d.UnreadMessages);
        Assert.Equal(["p1", "p2", "p3"], d.Announcements.Select(a => a.Id));
        Assert.Equal(2, d.UpcomingDeadlines.Count);
    }

    [Fact]
    public async Task FailingPlaton_WithAuthError_DoesNotThrow()
    {
        Seed();
        _platon.Failure = new AdapterAuthException();
        var d = await Create().GetAsync();
        Assert.Empty(d.UpcomingDeadlines);
        Assert.Equal(["u1", "u2", "u3"], d.Announcements.Select(a => a.Id));
        Assert.NotNull(d.Profile);
    }

    [Fact]
    public async Task UnexpectedExceptions_AreAlsoSwallowedPerPart()
    {
        Seed();
        _usos.Failure = new InvalidOperationException("błąd parsera");
        var d = await Create().GetAsync();
        Assert.Null(d.Profile);
        Assert.Equal(2, d.UpcomingDeadlines.Count);
    }

    [Fact]
    public async Task DisconnectedSystem_IsSkipped_WithoutCallingAdapter()
    {
        Seed();
        var d = await Create(platon: false).GetAsync();

        Assert.Equal(ConnectionStatus.Disconnected, d.Connections[SourceSystem.Platon]);
        Assert.Empty(d.UpcomingDeadlines);
        Assert.Equal(0, _platon.Calls.Count);
        Assert.NotNull(d.Profile);

        var none = await Create(usos: false, platon: false).GetAsync();
        Assert.Null(none.Profile);
        Assert.Empty(none.Announcements);
        Assert.All(none.Connections.Values, s => Assert.Equal(ConnectionStatus.Disconnected, s));
    }

    [Fact]
    public async Task Cancellation_IsPropagated()
    {
        Seed();
        _usos.Hang = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().GetAsync(cts.Token));
    }
}
