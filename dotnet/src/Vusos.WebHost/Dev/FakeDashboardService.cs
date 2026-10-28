using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Services;
using Vusos.UI.Util;

namespace Vusos.WebHost.Dev;

public sealed class FakeDashboardService(IConnectionManager connections, ISyncStatus sync) : IDashboardService
{
    public async Task<DashboardData> GetAsync(CancellationToken ct = default)
    {
        var status = await connections.GetStatusAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var today = Fmt.DateOf(now);

        StudentProfile? profile = null;
        IReadOnlyList<TimetableEntry> classes = [];
        IReadOnlyList<Grade> grades = [];
        IReadOnlyList<Payment> payments = [];
        IReadOnlyList<Announcement> announcements = [];
        var unread = 0;
        IReadOnlyList<LmsEvent> deadlines = [];
        var errors = new List<Exception>();

        async Task Try(Func<Task> work)
        {
            try { await work(); }
            catch (NotConnectedException) { }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { errors.Add(e); }
        }

        await Try(async () =>
        {
            var usos = await connections.GetUsosAsync(ct);
            profile = await usos.GetProfileAsync(ct);
            classes = await usos.GetTimetableAsync(today, today, ct);
            grades = (await usos.GetGradesAsync(null, ct)).OrderByDescending(g => g.DateModified).Take(5).ToList();
            payments = (await usos.GetPaymentsAsync(ct)).Where(p => p.State is PaymentState.Unpaid or PaymentState.PartiallyPaid or PaymentState.Overdue).ToList();
            unread = (await usos.ListMessagesAsync(50, 0, ct)).Count(m => m.Unread);
            announcements = await usos.GetAnnouncementsAsync(10, ct);
        });
        await Try(async () =>
        {
            var platon = await connections.GetPlatonAsync(ct);
            deadlines = await platon.GetUpcomingEventsAsync(7, ct);
            announcements = [.. announcements, .. await platon.GetAnnouncementsAsync(10, ct)];
        });

        // tak jak w Core: gdy WSZYSTKO zawiodło i nic nie mamy – oddaj błąd; częściowe dane są OK
        if (errors.Count > 0 && profile is null && deadlines.Count == 0) throw errors[0];

        return new DashboardData(profile, classes, deadlines, grades, payments, unread, announcements,
            status.ToDictionary(kv => kv.Key, kv => kv.Value.Status),
            new Dictionary<SourceSystem, DataOrigin>
            {
                [SourceSystem.Usos] = sync.Get(SourceSystem.Usos).LastOrigin,
                [SourceSystem.Platon] = sync.Get(SourceSystem.Platon).LastOrigin,
            });
    }
}
