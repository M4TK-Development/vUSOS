using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Offline;

namespace Vusos.Core.Services;

/// <summary>Stan połączenia z systemem dziedzinowym, przechowywany w ISecureStore.</summary>
public sealed record ConnectionInfo(SourceSystem System, ConnectionStatus Status, string Mode, DateTimeOffset? UpdatedAt, string? Error);

/// <summary>Zarządza poświadczeniami i udostępnia adaptery (owinięte w cache / offline).</summary>
public interface IConnectionManager
{
    Task<IReadOnlyDictionary<SourceSystem, ConnectionInfo>> GetStatusAsync(CancellationToken ct = default);
    Task ConnectUsosAsync(UsosCredentials credentials, CancellationToken ct = default);
    Task ConnectPlatonAsync(PlatonCredentials credentials, CancellationToken ct = default);
    Task DisconnectAsync(SourceSystem system, CancellationToken ct = default);
    /// <summary>Adapter USOS; rzuca <see cref="NotConnectedException"/> gdy brak poświadczeń.</summary>
    Task<IUsosAdapter> GetUsosAsync(CancellationToken ct = default);
    Task<IPlatonAdapter> GetPlatonAsync(CancellationToken ct = default);
    event EventHandler<SourceSystem>? Changed;
}

/// <summary>Co UI pokazuje w pasku stanu: skąd ostatnio przyszły dane i czy coś czeka na synchronizację.</summary>
public sealed record SyncState(SourceSystem System, DataOrigin LastOrigin, DateTimeOffset? LastLiveAt, DateTimeOffset? LastCacheAt, int PendingActions, string? LastError);

public interface ISyncStatus
{
    SyncState Get(SourceSystem system);
    void Report(SourceSystem system, DataOrigin origin, DateTimeOffset at, string? errorMessage = null);
    void SetPending(SourceSystem system, int pending);
    event EventHandler<SyncState>? Changed;
}

public sealed record DownloadProgress(string MaterialId, long BytesReceived, long? TotalBytes, DownloadState State, string? Error);

/// <summary>Materiały offline: kolejkowanie, pobieranie, otwieranie z dysku.</summary>
public interface IMaterialService
{
    Task<IReadOnlyList<MaterialRecord>> ListAsync(string? courseId = null, CancellationToken ct = default);
    Task<MaterialRecord?> FindAsync(string remoteUrl, CancellationToken ct = default);
    Task<MaterialRecord> EnqueueAsync(string courseId, LmsModule module, LmsFile file, CancellationToken ct = default);
    Task<int> EnqueueCourseAsync(string courseId, CancellationToken ct = default);
    Task RemoveAsync(string materialId, CancellationToken ct = default);
    Task<long> GetTotalSizeAsync(CancellationToken ct = default);
    /// <summary>Uruchamia przetwarzanie kolejki (idempotentne). Zwraca gdy kolejka pusta lub brak sieci.</summary>
    Task ProcessQueueAsync(CancellationToken ct = default);
    IObservable<DownloadProgress> Progress { get; }
}

public sealed record QuizSession(QuizAttemptData Data, IReadOnlyList<QuizAnswer> LocalAnswers, bool IsOffline, bool HasPendingSync);

/// <summary>Testy z obsługą offline: pobranie podejścia online, odpowiedzi lokalnie, synchronizacja po powrocie sieci.</summary>
public interface IQuizService
{
    Task<IReadOnlyList<LmsQuiz>> GetQuizzesAsync(string? courseId = null, CancellationToken ct = default);
    Task<IReadOnlyList<QuizAttempt>> GetAttemptsAsync(string quizId, CancellationToken ct = default);
    /// <summary>Rozpoczyna lub wznawia podejście; gdy online pobiera wszystkie pytania do pamięci lokalnej.</summary>
    Task<QuizSession> OpenAttemptAsync(LmsQuiz quiz, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default);
    Task<QuizSession> ResumeAttemptAsync(string attemptId, CancellationToken ct = default);
    /// <summary>Zapisuje odpowiedzi lokalnie; online dodatkowo wysyła save_attempt, offline kolejkuje.</summary>
    Task<QuizSession> SaveAnswersAsync(string attemptId, IReadOnlyList<QuizAnswer> answers, CancellationToken ct = default);
    /// <summary>Kończy podejście; offline kolejkuje process_attempt(finish) do synchronizacji.</summary>
    Task<QuizSession> FinishAsync(string attemptId, CancellationToken ct = default);
    Task<int> SyncPendingAsync(CancellationToken ct = default);
    Task<QuizAttemptReview?> GetReviewAsync(string attemptId, CancellationToken ct = default);
}

public interface IDashboardService
{
    Task<DashboardData> GetAsync(CancellationToken ct = default);
}

/// <summary>Opcje cache: TTL „świeżości” (po nim próbujemy sieć) i maks. wiek danych z cache.</summary>
public sealed class CacheOptions
{
    public TimeSpan FreshFor { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan ServeStaleUpTo { get; set; } = TimeSpan.FromDays(90);
    public TimeSpan LiveTimeout { get; set; } = TimeSpan.FromSeconds(12);
    public bool DownloadOnlyOnUnmetered { get; set; } = false;
}

/// <summary>Konfiguracja instalacji uczelni.</summary>
public sealed class UniversityOptions
{
    public string Name { get; set; } = "Akademia Finansów i Biznesu Vistula";
    public string UsosApiBaseUrl { get; set; } = "https://usosapps.vistula.edu.pl/";
    public string UsoswebBaseUrl { get; set; } = "https://usosweb.vistula.edu.pl/";
    public string PlatonBaseUrl { get; set; } = "https://platon.vistula.edu.pl/";
    public string NativeScheme { get; set; } = "vusos";
    /// <summary>Adres Vusos.Server (podpisywanie OAuth USOS). Pusty = podpisywanie lokalne consumer key/secret z konfiguracji.</summary>
    public string? SigningServerUrl { get; set; }
    /// <summary>Opcjonalny X-Vusos-App-Key wymagany przez Vusos.Server.</summary>
    public string? SigningServerAppKey { get; set; }
    public string? UsosConsumerKey { get; set; }
    public string? UsosConsumerSecret { get; set; }
    public string[] AllowedEmailDomains { get; set; } = ["vistula.edu.pl", "stu.vistula.edu.pl"];
    public string? EntraClientId { get; set; }
    public string EntraAuthority { get; set; } = "https://login.microsoftonline.com/organizations";
}
