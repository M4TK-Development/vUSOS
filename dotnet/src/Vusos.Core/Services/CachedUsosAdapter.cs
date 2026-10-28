using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Offline;
using Vusos.Core.Platform;

namespace Vusos.Core.Services;

/// <summary>Dekorator <see cref="IUsosAdapter"/>: cache stale-while-revalidate; zapisy bez cache + unieważnianie.</summary>
public sealed class CachedUsosAdapter : IUsosAdapter
{
    private const SourceSystem Sys = SourceSystem.Usos;
    private readonly IUsosAdapter _inner;
    private readonly SwrCache _swr;

    public CachedUsosAdapter(
        IUsosAdapter inner,
        string userScope,
        ICacheStore store,
        ISyncStatus sync,
        IConnectivity connectivity,
        IClock clock,
        CacheOptions options,
        ILogger? logger = null,
        Action<AdapterAuthException>? onAuthError = null)
    {
        _inner = inner;
        _swr = new SwrCache(userScope, Sys, store, sync, connectivity, clock, options, logger ?? NullLogger.Instance, onAuthError);
    }

    public Task<StudentProfile> GetProfileAsync(CancellationToken ct = default) =>
        _swr.GetAsync("GetProfile", [], c => _inner.GetProfileAsync(c), ct);

    public Task<IReadOnlyList<Term>> GetTermsAsync(CancellationToken ct = default) =>
        _swr.GetAsync("GetTerms", [], c => _inner.GetTermsAsync(c), ct);

    public Task<IReadOnlyList<CourseUnit>> GetCoursesAsync(string? termId = null, CancellationToken ct = default) =>
        _swr.GetAsync("GetCourses", [termId], c => _inner.GetCoursesAsync(termId, c), ct);

    public Task<IReadOnlyList<TimetableEntry>> GetTimetableAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        _swr.GetAsync("GetTimetable", [from, to], c => _inner.GetTimetableAsync(from, to, c), ct);

    public Task<IReadOnlyList<Grade>> GetGradesAsync(IReadOnlyList<string>? termIds = null, CancellationToken ct = default) =>
        _swr.GetAsync("GetGrades", [termIds], c => _inner.GetGradesAsync(termIds, c), ct);

    public Task<IReadOnlyList<Payment>> GetPaymentsAsync(CancellationToken ct = default) =>
        _swr.GetAsync("GetPayments", [], c => _inner.GetPaymentsAsync(c), ct);

    public Task<IReadOnlyList<MessageThread>> ListMessagesAsync(int limit = 50, int offset = 0, CancellationToken ct = default) =>
        _swr.GetAsync(CacheMethods.ListMessages, [limit, offset], c => _inner.ListMessagesAsync(limit, offset, c), ct);

    public Task<Message> GetMessageAsync(string id, CancellationToken ct = default) =>
        _swr.GetAsync("GetMessage", [id], c => _inner.GetMessageAsync(id, c), ct);

    public Task<string> SendMessageAsync(IReadOnlyList<string> to, string subject, string body, CancellationToken ct = default) =>
        _swr.WriteAsync(() => _inner.SendMessageAsync(to, subject, body, ct), [CacheMethods.ListMessages], ct);

    public Task<IReadOnlyList<Contact>> GetDeanOfficeContactsAsync(CancellationToken ct = default) =>
        _swr.GetAsync("GetDeanOfficeContacts", [], c => _inner.GetDeanOfficeContactsAsync(c), ct);

    public Task<IReadOnlyList<Announcement>> GetAnnouncementsAsync(int limit = 20, CancellationToken ct = default) =>
        _swr.GetAsync("GetAnnouncements", [limit], c => _inner.GetAnnouncementsAsync(limit, c), ct);
}

/// <summary>Nazwy metod w kluczach cache (używane też do unieważniania).</summary>
public static class CacheMethods
{
    public const string ListMessages = "ListMessages";
    public const string GetQuizzes = "GetQuizzes";
    public const string GetQuizAttempts = "GetQuizAttempts";
    public const string GetQuizAttemptReview = "GetQuizAttemptReview";
    public const string GetGrades = "GetGrades";
}
