using Vusos.Core.Domain;

namespace Vusos.Core.Adapters;

/// <summary>USOS: dziekanat, oceny, plan, płatności, komunikacja, ogłoszenia.</summary>
public interface IUsosAdapter
{
    Task<StudentProfile> GetProfileAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Term>> GetTermsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CourseUnit>> GetCoursesAsync(string? termId = null, CancellationToken ct = default);
    Task<IReadOnlyList<TimetableEntry>> GetTimetableAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
    Task<IReadOnlyList<Grade>> GetGradesAsync(IReadOnlyList<string>? termIds = null, CancellationToken ct = default);
    Task<IReadOnlyList<Payment>> GetPaymentsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MessageThread>> ListMessagesAsync(int limit = 50, int offset = 0, CancellationToken ct = default);
    Task<Message> GetMessageAsync(string id, CancellationToken ct = default);
    Task<string> SendMessageAsync(IReadOnlyList<string> to, string subject, string body, CancellationToken ct = default);
    Task<IReadOnlyList<Contact>> GetDeanOfficeContactsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Announcement>> GetAnnouncementsAsync(int limit = 20, CancellationToken ct = default);
}
