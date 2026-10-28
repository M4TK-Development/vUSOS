using Vusos.Core.Domain;

namespace Vusos.Core.Offline;

/// <summary>Cache odpowiedzi (JSON) z czasem pobrania. Klucz: użytkownik + system + metoda + argumenty.</summary>
public interface ICacheStore
{
    Task<CacheEntry?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string json, DateTimeOffset fetchedAt, CancellationToken ct = default);
    Task RemoveByPrefixAsync(string prefix, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

public sealed record CacheEntry(string Json, DateTimeOffset FetchedAt);

public enum DownloadState { Queued, Downloading, Done, Failed }

public sealed record MaterialRecord(
    string Id,
    string CourseId,
    string ModuleId,
    string FileName,
    string RemoteUrl,
    string? LocalPath,
    long? Size,
    string? MimeType,
    DateTimeOffset? RemoteModified,
    DownloadState State,
    string? Error,
    DateTimeOffset UpdatedAt);

public interface IMaterialStore
{
    Task<IReadOnlyList<MaterialRecord>> ListAsync(string? courseId = null, CancellationToken ct = default);
    Task<MaterialRecord?> GetByUrlAsync(string remoteUrl, CancellationToken ct = default);
    Task UpsertAsync(MaterialRecord record, CancellationToken ct = default);
    Task RemoveAsync(string id, CancellationToken ct = default);
}

/// <summary>Kolejka operacji wykonanych offline (odpowiedzi w testach), do wysłania po odzyskaniu sieci.</summary>
public sealed record PendingQuizAction(
    string Id,
    string AttemptId,
    string QuizId,
    IReadOnlyList<QuizAnswer> Answers,
    bool Finish,
    DateTimeOffset CreatedAt,
    int RetryCount,
    string? LastError);

public interface IQuizOfflineStore
{
    Task SaveAttemptDataAsync(QuizAttemptData data, CancellationToken ct = default);
    Task<QuizAttemptData?> GetAttemptDataAsync(string attemptId, CancellationToken ct = default);
    Task SaveLocalAnswersAsync(string attemptId, IReadOnlyList<QuizAnswer> answers, CancellationToken ct = default);
    Task<IReadOnlyList<QuizAnswer>> GetLocalAnswersAsync(string attemptId, CancellationToken ct = default);
    Task EnqueueAsync(PendingQuizAction action, CancellationToken ct = default);
    Task<IReadOnlyList<PendingQuizAction>> ListPendingAsync(CancellationToken ct = default);
    Task CompleteAsync(string actionId, CancellationToken ct = default);
    Task MarkFailedAsync(string actionId, string error, CancellationToken ct = default);
}
