namespace Vusos.Core.Domain;

public sealed record DashboardData(
    StudentProfile? Profile,
    IReadOnlyList<TimetableEntry> TodayClasses,
    IReadOnlyList<LmsEvent> UpcomingDeadlines,
    IReadOnlyList<Grade> RecentGrades,
    IReadOnlyList<Payment> PaymentsDue,
    int UnreadMessages,
    IReadOnlyList<Announcement> Announcements,
    IReadOnlyDictionary<SourceSystem, ConnectionStatus> Connections,
    IReadOnlyDictionary<SourceSystem, DataOrigin> Origins);
