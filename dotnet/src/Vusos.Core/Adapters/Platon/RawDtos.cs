using System.Globalization;
using System.Text.Json;

namespace Vusos.Core.Adapters.Platon;

// Łagodne DTO odpowiedzi Moodle 4.x (odpowiednik schematów zod z prototypu). Pola nieistotne są opcjonalne:
// wersje i wtyczki Moodle różnią się tym, co zwracają. Wymagane (bez wartości domyślnej) są tylko te, bez których
// mapowanie nie ma sensu — ich brak daje JsonException → AdapterUnavailableException.
// Nazwy właściwości są dopasowywane bez względu na wielkość liter (PropertyNameCaseInsensitive).

public sealed record RawSiteInfo(long UserId, string FullName, string SiteName, string? Release = null);

public sealed record RawCourse(
    long Id, string FullName, string ShortName,
    string? IdNumber = null, long? StartDate = null, long? EndDate = null, string? CourseImage = null,
    double? Progress = null, string? CourseCategory = null,
    // TODO(verify): nie występuje w odpowiedzi „timeline classification”; jest w core_enrol_get_users_courses.
    long? LastAccess = null);

public sealed record RawEnrolledCourses(IReadOnlyList<RawCourse> Courses, long? NextOffset = null);

public sealed record RawContentFile(
    string? Type = null, string? FileName = null, long? FileSize = null, string? FileUrl = null,
    long? TimeModified = null, string? MimeType = null);

public sealed record RawCompletionData(int? State = null);

public sealed record RawModule(
    long Id, string Name, string ModName,
    string? Url = null, string? Description = null, int? Visible = null, bool? UserVisible = null,
    int? Completion = null, RawCompletionData? CompletionData = null,
    IReadOnlyList<RawContentFile>? Contents = null, long? Instance = null);

public sealed record RawSection(
    long Id, string? Name = null, string? Summary = null, int? Section = null, IReadOnlyList<RawModule>? Modules = null);

public sealed record RawAssignment(long Id, long CmId, string Name, int? NoSubmissions = null, long? DueDate = null, long? CutoffDate = null);

public sealed record RawAssignmentCourse(long Id, string FullName, IReadOnlyList<RawAssignment> Assignments);

public sealed record RawAssignments(IReadOnlyList<RawAssignmentCourse> Courses);

public sealed record RawSubmission(string? Status = null);

public sealed record RawLastAttempt(RawSubmission? Submission = null, bool? Graded = null);

public sealed record RawFeedback(string? GradeForDisplay = null, long? GradedDate = null);

public sealed record RawSubmissionStatus(RawLastAttempt? LastAttempt = null, RawFeedback? Feedback = null);

public sealed record RawEventCourse(long Id, string? FullName = null, string? ShortName = null);

public sealed record RawEventAction(string? Name = null, string? Url = null);

public sealed record RawEvent(
    long Id, string Name, long TimeStart,
    string? ModuleName = null, string? EventType = null, long? TimeDuration = null, string? Url = null,
    RawEventCourse? Course = null, RawEventAction? Action = null);

public sealed record RawEvents(IReadOnlyList<RawEvent> Events);

public sealed record RawGradeItem(
    long Id,
    string? ItemName = null, string? ItemType = null, double? GradeRaw = null, long? GradeDateGraded = null,
    string? GradeFormatted = null, double? GradeMax = null, string? PercentageFormatted = null, string? Feedback = null);

public sealed record RawUserGrade(long CourseId, IReadOnlyList<RawGradeItem> GradeItems);

public sealed record RawGradeItems(IReadOnlyList<RawUserGrade> UserGrades);

public sealed record RawForum(long Id, long Course, string Type, string Name);

public sealed record RawDiscussion(
    long Id,
    long? Discussion = null, string? Name = null, string? Subject = null, string? Message = null,
    long? Created = null, long? Modified = null, long? TimeModified = null, string? UserFullName = null);

public sealed record RawDiscussions(IReadOnlyList<RawDiscussion> Discussions);

// ---------- mod_quiz ----------
// Pola, których typ Moodle zmienia między wersjami (liczba/napis: mark, sumgrades, allowofflineattempts...),
// czytamy jako JsonElement i konwertujemy przez JsonLoose.

public sealed record RawQuiz(
    long Id, long CourseModule, long Course, string Name,
    string? Intro = null, long? TimeOpen = null, long? TimeClose = null, long? TimeLimit = null, long? Attempts = null,
    JsonElement? AllowOfflineAttempts = null, JsonElement? HasQuestions = null,
    JsonElement? Grade = null, JsonElement? SumGrades = null);

public sealed record RawQuizzes(IReadOnlyList<RawQuiz> Quizzes);

public sealed record RawQuizAttempt(
    long Id, long Quiz, string State,
    long? Attempt = null, long? UniqueId = null, long? TimeStart = null, long? TimeFinish = null,
    JsonElement? SumGrades = null, long? CurrentPage = null);

public sealed record RawQuizAttempts(IReadOnlyList<RawQuizAttempt> Attempts);

public sealed record RawStartAttempt(RawQuizAttempt Attempt);

public sealed record RawQuestion(
    long Slot,
    long? Page = null, string? Type = null, string? Html = null, JsonElement? SequenceCheck = null,
    string? State = null, string? Status = null, JsonElement? Mark = null, JsonElement? MaxMark = null);

public sealed record RawAttemptData(RawQuizAttempt Attempt, long? NextPage = null, IReadOnlyList<RawQuestion>? Questions = null);

public sealed record RawSaveResult(JsonElement? Status = null);

public sealed record RawProcessResult(string State);

public sealed record RawAttemptReview(RawQuizAttempt Attempt, JsonElement? Grade = null, IReadOnlyList<RawQuestion>? Questions = null);

/// <summary>Konwersje wartości, których typ JSON bywa liczbą, napisem albo bool.</summary>
public static class JsonLoose
{
    public static double? Double(JsonElement? e)
    {
        if (e is not { } v) return null;
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                return v.GetDouble();
            case JsonValueKind.String:
                return double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            default:
                return null;
        }
    }

    public static bool Bool(JsonElement? e)
    {
        if (e is not { } v) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => v.GetDouble() != 0,
            JsonValueKind.String => v.GetString() is { } s && (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    public static string? String(JsonElement? e)
    {
        if (e is not { } v) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
    }
}

internal static class PlatonJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };
}
