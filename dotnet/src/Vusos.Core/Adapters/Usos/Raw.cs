// Lenient raw DTOs for USOS API JSON. Everything optional on purpose: field names come from the public USOS API
// documentation and have NOT been checked against the Vistula installation (see TODO(verify) markers).
// Mappers skip entries that fail to deserialize instead of throwing.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vusos.Core.Adapters.Usos;

/// <summary>Accepts a JSON string, number or boolean as string (USOS ids arrive as strings or numbers); null stays null.</summary>
public sealed class LenientStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => JsonElement.ParseValue(ref reader).GetRawText(),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => throw new JsonException("string, number or boolean expected"),
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

public sealed record RawProgramme
{
    [JsonPropertyName("id"), JsonConverter(typeof(LenientStringConverter))] public string? Id { get; init; }
    [JsonPropertyName("description")] public JsonElement? Description { get; init; }
    [JsonPropertyName("level_of_studies")] public JsonElement? LevelOfStudies { get; init; }
    [JsonPropertyName("mode_of_studies")] public JsonElement? ModeOfStudies { get; init; }
}

public sealed record RawStudentProgramme
{
    [JsonPropertyName("id"), JsonConverter(typeof(LenientStringConverter))] public string? Id { get; init; }
    [JsonPropertyName("programme")] public RawProgramme? Programme { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("stage")] public string? Stage { get; init; }
}

public sealed record RawUser
{
    [JsonPropertyName("id"), JsonConverter(typeof(LenientStringConverter))] public string? Id { get; init; }
    [JsonPropertyName("first_name")] public string? FirstName { get; init; }
    [JsonPropertyName("last_name")] public string? LastName { get; init; }
    [JsonPropertyName("email")] public string? Email { get; init; }
    [JsonPropertyName("student_number"), JsonConverter(typeof(LenientStringConverter))] public string? StudentNumber { get; init; }
    [JsonPropertyName("photo_urls")] public Dictionary<string, JsonElement>? PhotoUrls { get; init; }
    [JsonPropertyName("student_programmes")] public List<RawStudentProgramme>? StudentProgrammes { get; init; }
}

public sealed record RawTerm
{
    [JsonPropertyName("id"), JsonConverter(typeof(LenientStringConverter))] public string? Id { get; init; }
    [JsonPropertyName("name")] public JsonElement? Name { get; init; }
    [JsonPropertyName("start_date")] public string? StartDate { get; init; }
    [JsonPropertyName("end_date")] public string? EndDate { get; init; }
    [JsonPropertyName("finish_date")] public string? FinishDate { get; init; }
    [JsonPropertyName("is_active")] public bool? IsActive { get; init; }
}

public sealed record RawLecturer
{
    [JsonPropertyName("id"), JsonConverter(typeof(LenientStringConverter))] public string? Id { get; init; }
    [JsonPropertyName("first_name")] public string? FirstName { get; init; }
    [JsonPropertyName("last_name")] public string? LastName { get; init; }
}

public sealed record RawUserGroup
{
    [JsonPropertyName("course_unit_id"), JsonConverter(typeof(LenientStringConverter))] public string? CourseUnitId { get; init; }
    [JsonPropertyName("group_number")] public JsonElement? GroupNumber { get; init; }
    [JsonPropertyName("class_type")] public JsonElement? ClassType { get; init; }
    [JsonPropertyName("lecturers")] public List<RawLecturer>? Lecturers { get; init; }
}

public sealed record RawCourseEdition
{
    [JsonPropertyName("course_id"), JsonConverter(typeof(LenientStringConverter))] public string? CourseId { get; init; }
    [JsonPropertyName("course_name")] public JsonElement? CourseName { get; init; }
    [JsonPropertyName("term_id"), JsonConverter(typeof(LenientStringConverter))] public string? TermId { get; init; }
    [JsonPropertyName("user_groups")] public List<RawUserGroup>? UserGroups { get; init; }
}

public sealed record RawActivity
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("start_time")] public string? StartTime { get; init; }
    [JsonPropertyName("end_time")] public string? EndTime { get; init; }
    [JsonPropertyName("name")] public JsonElement? Name { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("unit_id"), JsonConverter(typeof(LenientStringConverter))] public string? UnitId { get; init; }
    [JsonPropertyName("course_id"), JsonConverter(typeof(LenientStringConverter))] public string? CourseId { get; init; }
    [JsonPropertyName("course_name")] public JsonElement? CourseName { get; init; }
    [JsonPropertyName("classtype_name")] public JsonElement? ClasstypeName { get; init; }
    [JsonPropertyName("group_number")] public JsonElement? GroupNumber { get; init; }
    [JsonPropertyName("room_number"), JsonConverter(typeof(LenientStringConverter))] public string? RoomNumber { get; init; }
    [JsonPropertyName("building_name")] public JsonElement? BuildingName { get; init; }
    /// <summary>Array of ids (strings or numbers).</summary>
    [JsonPropertyName("lecturer_ids")] public JsonElement? LecturerIds { get; init; }
}

public sealed record RawGrade
{
    [JsonPropertyName("value_symbol")] public string? ValueSymbol { get; init; }
    [JsonPropertyName("value_description")] public JsonElement? ValueDescription { get; init; }
    [JsonPropertyName("passes")] public bool? Passes { get; init; }
    /// <summary>Boolean or "Y"/"N"-like string.</summary>
    [JsonPropertyName("counts_into_average"), JsonConverter(typeof(LenientStringConverter))] public string? CountsIntoAverage { get; init; }
    [JsonPropertyName("exam_session_number")] public JsonElement? ExamSessionNumber { get; init; }
    [JsonPropertyName("date_modified")] public string? DateModified { get; init; }
    [JsonPropertyName("modification_author")] public JsonElement? ModificationAuthor { get; init; }
    [JsonPropertyName("comment")] public string? Comment { get; init; }
}

public sealed record RawPayment
{
    [JsonPropertyName("id"), JsonConverter(typeof(LenientStringConverter))] public string? Id { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("description")] public JsonElement? Description { get; init; }
    [JsonPropertyName("total_amount")] public JsonElement? TotalAmount { get; init; }
    [JsonPropertyName("saldo_amount")] public JsonElement? SaldoAmount { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("payment_deadline")] public string? PaymentDeadline { get; init; }
    [JsonPropertyName("interest")] public JsonElement? Interest { get; init; }
    [JsonPropertyName("account_number")] public string? AccountNumber { get; init; }
    [JsonPropertyName("currency")] public string? Currency { get; init; }
}

public sealed record RawAttachment
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("size")] public JsonElement? Size { get; init; }
}

public sealed record RawMailMessage
{
    [JsonPropertyName("id"), JsonConverter(typeof(LenientStringConverter))] public string? Id { get; init; }
    [JsonPropertyName("subject")] public string? Subject { get; init; }
    [JsonPropertyName("sender")] public JsonElement? Sender { get; init; }
    [JsonPropertyName("recipients")] public JsonElement? Recipients { get; init; }
    [JsonPropertyName("date")] public string? Date { get; init; }
    [JsonPropertyName("is_read")] public bool? IsRead { get; init; }
    [JsonPropertyName("preview")] public string? Preview { get; init; }
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("attachments")] public List<RawAttachment>? Attachments { get; init; }
}

public sealed record RawFaculty
{
    [JsonPropertyName("id"), JsonConverter(typeof(LenientStringConverter))] public string? Id { get; init; }
    [JsonPropertyName("name")] public JsonElement? Name { get; init; }
    [JsonPropertyName("postal_address")] public string? PostalAddress { get; init; }
    [JsonPropertyName("phone_numbers")] public List<string>? PhoneNumbers { get; init; }
    [JsonPropertyName("homepage_url")] public string? HomepageUrl { get; init; }
}

public sealed record RawNewsArticle
{
    [JsonPropertyName("id"), JsonConverter(typeof(LenientStringConverter))] public string? Id { get; init; }
    [JsonPropertyName("title")] public JsonElement? Title { get; init; }
    [JsonPropertyName("content")] public JsonElement? Content { get; init; }
    [JsonPropertyName("publication_date")] public string? PublicationDate { get; init; }
    [JsonPropertyName("author")] public JsonElement? Author { get; init; }
    [JsonPropertyName("category")] public JsonElement? Category { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(RawUser))]
[JsonSerializable(typeof(RawTerm))]
[JsonSerializable(typeof(RawCourseEdition))]
[JsonSerializable(typeof(RawActivity))]
[JsonSerializable(typeof(RawGrade))]
[JsonSerializable(typeof(RawPayment))]
[JsonSerializable(typeof(RawMailMessage))]
[JsonSerializable(typeof(RawFaculty))]
[JsonSerializable(typeof(RawNewsArticle))]
internal sealed partial class UsosJsonContext : JsonSerializerContext;

/// <summary>Deserializes one raw entry; invalid entries (wrong types, missing required fields) yield null.</summary>
internal static class RawReader
{
    public static RawUser? User(JsonElement e) =>
        Read(e, UsosJsonContext.Default.RawUser, r => r.Id is not null
            && (r.StudentProgrammes?.TrueForAll(p => p.Id is not null) ?? true));
    public static RawTerm? Term(JsonElement e) =>
        Read(e, UsosJsonContext.Default.RawTerm, r => r.Id is not null && r.StartDate is not null && r.EndDate is not null);
    public static RawCourseEdition? CourseEdition(JsonElement e) =>
        Read(e, UsosJsonContext.Default.RawCourseEdition, r => r.CourseId is not null
            && (r.UserGroups?.TrueForAll(g => g.CourseUnitId is not null) ?? true));
    public static RawActivity? Activity(JsonElement e) =>
        Read(e, UsosJsonContext.Default.RawActivity, r => r.StartTime is not null && r.EndTime is not null);
    public static RawGrade? Grade(JsonElement e) => Read(e, UsosJsonContext.Default.RawGrade, _ => true);
    public static RawPayment? Payment(JsonElement e) => Read(e, UsosJsonContext.Default.RawPayment, r => r.Id is not null);
    public static RawMailMessage? MailMessage(JsonElement e) => Read(e, UsosJsonContext.Default.RawMailMessage, r => r.Id is not null);
    public static RawFaculty? Faculty(JsonElement e) => Read(e, UsosJsonContext.Default.RawFaculty, r => r.Id is not null);
    public static RawNewsArticle? News(JsonElement e) => Read(e, UsosJsonContext.Default.RawNewsArticle, r => r.Id is not null);

    private static T? Read<T>(JsonElement e, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, Func<T, bool> valid)
        where T : class
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        try
        {
            var r = e.Deserialize(info);
            return r is not null && valid(r) ? r : null;
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
