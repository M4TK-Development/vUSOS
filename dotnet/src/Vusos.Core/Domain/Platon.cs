namespace Vusos.Core.Domain;

public sealed record LmsCourse(
    string Id,
    string ShortName,
    string FullName,
    string? Category,
    double? Progress,
    DateTimeOffset? StartDate,
    DateTimeOffset? EndDate,
    string? ImageUrl,
    string Url,
    string? UsosCourseId,
    DateTimeOffset? LastAccess);

public enum LmsModuleType { Resource, Folder, Url, Page, Book, Assign, Quiz, Forum, Label, Lesson, Other }

public sealed record LmsFile(string Name, string Url, string? MimeType, long? Size, DateTimeOffset? Modified);

public sealed record LmsModule(
    string Id,
    string CourseId,
    string SectionId,
    string SectionName,
    string Name,
    LmsModuleType Type,
    string RawType,
    string? Url,
    string? DescriptionHtml,
    IReadOnlyList<LmsFile> Files,
    bool? Completed,
    bool Visible,
    /// <summary>Id instancji modułu (np. quiz id, assign id) w tabeli danego modułu; różne od Id (cmid).</summary>
    string? InstanceId = null);

public sealed record LmsSection(string Id, string Name, string? SummaryHtml, IReadOnlyList<LmsModule> Modules);

public sealed record LmsAssignment(
    string Id,
    string CourseId,
    string CourseName,
    string Name,
    DateTimeOffset? DueDate,
    DateTimeOffset? CutoffDate,
    bool Submitted,
    bool Graded,
    string? Grade,
    string Url);

public sealed record LmsEvent(
    string Id,
    string? CourseId,
    string? CourseName,
    string Name,
    string Type,
    string? ModuleType,
    DateTimeOffset Start,
    DateTimeOffset? End,
    string? Url,
    string? ActionName);

public sealed record LmsGradeItem(
    string Id,
    string CourseId,
    string ItemName,
    string ItemType,
    string? GradeFormatted,
    double? GradeRaw,
    double? GradeMax,
    string? Percentage,
    string? FeedbackHtml,
    DateTimeOffset? DateGraded);

// ---------- Testy (mod_quiz) ----------

public sealed record LmsQuiz(
    string Id,
    string CourseModuleId,
    string CourseId,
    string Name,
    string? IntroHtml,
    DateTimeOffset? TimeOpen,
    DateTimeOffset? TimeClose,
    int? TimeLimitSeconds,
    int Attempts,
    /// <summary>Czy wykładowca zezwolił na podejścia offline (ustawienie quizu „allowofflineattempts”).</summary>
    bool AllowOfflineAttempts,
    bool HasQuestions,
    string Url);

public enum QuizAttemptState { InProgress, Overdue, Finished, Abandoned }

public sealed record QuizAttempt(
    string Id,
    string QuizId,
    int Attempt,
    QuizAttemptState State,
    DateTimeOffset? TimeStart,
    DateTimeOffset? TimeFinish,
    double? SumGrades,
    int? CurrentPage);

/// <summary>Jedno pytanie w podejściu. <see cref="Html"/> to formularz pytania wygenerowany przez Moodle;
/// nazwy pól (q{usageid}:{slot}_...) trzeba odesłać w <see cref="QuizAnswer"/>.</summary>
public sealed record QuizQuestion(
    int Slot,
    int Page,
    string Type,
    string Html,
    string? SequenceCheck,
    string State,
    string? Status,
    double? Mark,
    double? MaxMark,
    IReadOnlyList<string> FieldNames);

public sealed record QuizAnswer(string Name, string Value);

public sealed record QuizAttemptData(
    QuizAttempt Attempt,
    IReadOnlyList<QuizQuestion> Questions,
    IReadOnlyList<QuizAnswer> PreflightData);

public sealed record QuizAttemptReview(
    QuizAttempt Attempt,
    double? Grade,
    double? MaxGrade,
    IReadOnlyList<QuizQuestion> Questions);
