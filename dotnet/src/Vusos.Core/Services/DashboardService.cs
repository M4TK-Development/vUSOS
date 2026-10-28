using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Platform;

namespace Vusos.Core.Services;

/// <summary>
/// Agregacja pulpitu w stylu Promise.allSettled: każda część osobno, błąd jednej (lub brak połączenia z systemem)
/// daje pustą część, nigdy wyjątek. Sieć/cache obsługują dekoratory adapterów.
/// </summary>
public sealed class DashboardService : IDashboardService
{
    private static readonly string[] WarsawZoneIds = ["Europe/Warsaw", "Central European Standard Time"];

    private readonly IConnectionManager _connections;
    private readonly ISyncStatus _sync;
    private readonly IClock _clock;
    private readonly ILogger _logger;

    public DashboardService(IConnectionManager connections, ISyncStatus sync, IClock clock, ILogger<DashboardService>? logger = null)
    {
        _connections = connections;
        _sync = sync;
        _clock = clock;
        _logger = logger ?? NullLogger<DashboardService>.Instance;
    }

    public async Task<DashboardData> GetAsync(CancellationToken ct = default)
    {
        IReadOnlyDictionary<SourceSystem, ConnectionInfo> status;
        try
        {
            status = await _connections.GetStatusAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Nie udało się odczytać stanu połączeń");
            status = new Dictionary<SourceSystem, ConnectionInfo>();
        }

        bool Active(SourceSystem s) => status.TryGetValue(s, out var i) && i.Status != ConnectionStatus.Disconnected;

        var usosActive = Active(SourceSystem.Usos);
        var platonActive = Active(SourceSystem.Platon);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.Now, Warsaw()).DateTime);

        var profile = Part("profil", usosActive, ct, async c => (StudentProfile?)await (await Usos(c)).GetProfileAsync(c), null);
        var classes = Part("zajęcia", usosActive, ct, async c => await (await Usos(c)).GetTimetableAsync(today, today, c),
            (IReadOnlyList<TimetableEntry>)[]);
        var events = Part("terminy", platonActive, ct, async c => await (await Platon(c)).GetUpcomingEventsAsync(7, c),
            (IReadOnlyList<LmsEvent>)[]);
        var grades = Part("oceny", usosActive, ct, async c => await (await Usos(c)).GetGradesAsync(null, c),
            (IReadOnlyList<Grade>)[]);
        var payments = Part("płatności", usosActive, ct, async c => await (await Usos(c)).GetPaymentsAsync(c),
            (IReadOnlyList<Payment>)[]);
        var messages = Part("wiadomości", usosActive, ct, async c => await (await Usos(c)).ListMessagesAsync(50, 0, c),
            (IReadOnlyList<MessageThread>)[]);
        var usosNews = Part("ogłoszenia USOS", usosActive, ct, async c => await (await Usos(c)).GetAnnouncementsAsync(5, c),
            (IReadOnlyList<Announcement>)[]);
        var platonNews = Part("ogłoszenia Platon", platonActive, ct, async c => await (await Platon(c)).GetAnnouncementsAsync(5, c),
            (IReadOnlyList<Announcement>)[]);

        await Task.WhenAll(profile, classes, events, grades, payments, messages, usosNews, platonNews).ConfigureAwait(false);

        var connections = new Dictionary<SourceSystem, ConnectionStatus>
        {
            [SourceSystem.Usos] = status.TryGetValue(SourceSystem.Usos, out var u) ? u.Status : ConnectionStatus.Disconnected,
            [SourceSystem.Platon] = status.TryGetValue(SourceSystem.Platon, out var p) ? p.Status : ConnectionStatus.Disconnected,
        };
        var origins = new Dictionary<SourceSystem, DataOrigin>
        {
            [SourceSystem.Usos] = _sync.Get(SourceSystem.Usos).LastOrigin,
            [SourceSystem.Platon] = _sync.Get(SourceSystem.Platon).LastOrigin,
        };

        return new DashboardData(
            Profile: profile.Result,
            TodayClasses: classes.Result.OrderBy(c => c.Start).ToList(),
            UpcomingDeadlines: events.Result.OrderBy(e => e.Start).ToList(),
            RecentGrades: grades.Result.OrderByDescending(g => g.DateModified ?? DateTimeOffset.MinValue).Take(5).ToList(),
            PaymentsDue: payments.Result
                .Where(x => x.State is PaymentState.Unpaid or PaymentState.PartiallyPaid or PaymentState.Overdue)
                .OrderBy(x => x.DueDate ?? DateOnly.MaxValue).ToList(),
            UnreadMessages: messages.Result.Count(m => m.Unread),
            Announcements: usosNews.Result.Concat(platonNews.Result).OrderByDescending(a => a.PublishedAt).Take(5).ToList(),
            Connections: connections,
            Origins: origins);

        async Task<IUsosAdapter> Usos(CancellationToken c) => await _connections.GetUsosAsync(c).ConfigureAwait(false);
        async Task<IPlatonAdapter> Platon(CancellationToken c) => await _connections.GetPlatonAsync(c).ConfigureAwait(false);
    }

    private async Task<T> Part<T>(string name, bool active, CancellationToken ct, Func<CancellationToken, Task<T>> load, T fallback)
    {
        if (!active)
            return fallback;
        try
        {
            return await load(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Pulpit: część „{Part}” niedostępna", name);
            return fallback;
        }
    }

    private static TimeZoneInfo Warsaw()
    {
        foreach (var id in WarsawZoneIds)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Local;
    }
}
