namespace Vusos.Core.Domain;

public sealed record Programme(string Id, LangText Name, string? Status, string? Stage);

public sealed record StudentProfile(
    string Id,
    string FirstName,
    string LastName,
    string? Email,
    string? StudentNumber,
    string? PhotoUrl,
    IReadOnlyList<Programme> Programmes)
{
    public string FullName => $"{FirstName} {LastName}";
}

public sealed record Term(string Id, LangText Name, DateOnly StartDate, DateOnly EndDate, bool IsCurrent);

public sealed record CourseUnit(
    string Id,
    string CourseId,
    LangText CourseName,
    string ClassType,
    string TermId,
    int? GroupNumber,
    IReadOnlyList<string> Lecturers,
    decimal? Ects,
    string? PlatonCourseId);

public sealed record TimetableEntry(
    string Id,
    SourceSystem Source,
    DateTimeOffset Start,
    DateTimeOffset End,
    string? CourseId,
    LangText CourseName,
    string? ClassType,
    int? GroupNumber,
    string? Room,
    string? Building,
    IReadOnlyList<string> Lecturers,
    string? Url);

public sealed record Grade(
    string Id,
    string TermId,
    string CourseId,
    LangText CourseName,
    string? ClassType,
    int? ExamSession,
    string ValueSymbol,
    LangText? ValueDescription,
    bool? Passes,
    bool CountsIntoAverage,
    DateTimeOffset? DateModified,
    string? GradedBy,
    string? Comment);

public sealed record TermGrades(Term Term, IReadOnlyList<Grade> Grades, decimal? Average);

public enum PaymentState { Unpaid, PartiallyPaid, Paid, Overdue, Cancelled }

public sealed record Payment(
    string Id,
    string Type,
    LangText Description,
    decimal Amount,
    decimal Paid,
    string Currency,
    DateOnly? DueDate,
    PaymentState State,
    decimal? Interest,
    string? AccountNumber)
{
    public decimal Outstanding => Math.Max(0, Amount - Paid);
}

public sealed record PaymentsSummary(IReadOnlyList<Payment> Payments, decimal TotalDue, decimal TotalOverdue, string Currency);

public sealed record MessageThread(
    string Id,
    SourceSystem Source,
    string Subject,
    string From,
    IReadOnlyList<string> To,
    DateTimeOffset Date,
    string Preview,
    bool Unread,
    string? Url);

public sealed record Attachment(string Name, string Url, long? Size);

public sealed record Message(
    MessageThread Thread,
    string BodyHtml,
    IReadOnlyList<Attachment> Attachments);

public sealed record Contact(string Id, string Name, string Role, string? Email, string? Phone, string? Room, string? Hours);

public sealed record Announcement(
    string Id,
    SourceSystem Source,
    string Title,
    string BodyHtml,
    DateTimeOffset PublishedAt,
    string? Author,
    string? Category,
    string? Url);
