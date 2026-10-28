using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Offline;
using Vusos.Core.Platform;

namespace Vusos.Core.Services;

/// <summary>
/// Materiały offline: kolejka w SQLite, pobieranie przez HttpClient ("platon") z adresem i nagłówkami z
/// <see cref="IPlatonAdapter.GetFileDownloadAsync"/>, plik tymczasowy <c>.part</c> (wznawianie Range) + rename.
/// Kolejka rusza dopiero po <see cref="ProcessQueueAsync"/> (UI / <see cref="BackgroundSyncCoordinator"/>).
/// </summary>
public sealed class MaterialService : IMaterialService
{
    public const string HttpClientName = "platon";
    public const int MaxAttempts = 3;

    private readonly IMaterialStore _store;
    private readonly IConnectionManager _connections;
    private readonly IHttpClientFactory _http;
    private readonly IAppPaths _paths;
    private readonly IConnectivity _connectivity;
    private readonly IClock _clock;
    private readonly CacheOptions _options;
    private readonly ILogger _logger;
    private readonly TimeSpan _retryBaseDelay;
    private readonly SimpleSubject<DownloadProgress> _progress = new();
    private readonly SemaphoreSlim _processLock = new(1, 1);

    public MaterialService(
        IMaterialStore store,
        IConnectionManager connections,
        IHttpClientFactory http,
        IAppPaths paths,
        IConnectivity connectivity,
        IClock clock,
        CacheOptions options,
        ILogger<MaterialService>? logger = null,
        TimeSpan? retryBaseDelay = null)
    {
        _store = store;
        _connections = connections;
        _http = http;
        _paths = paths;
        _connectivity = connectivity;
        _clock = clock;
        _options = options;
        _logger = logger ?? NullLogger<MaterialService>.Instance;
        _retryBaseDelay = retryBaseDelay ?? TimeSpan.FromSeconds(1);
    }

    public IObservable<DownloadProgress> Progress => _progress;

    public Task<IReadOnlyList<MaterialRecord>> ListAsync(string? courseId = null, CancellationToken ct = default) =>
        _store.ListAsync(courseId, ct);

    public Task<MaterialRecord?> FindAsync(string remoteUrl, CancellationToken ct = default) =>
        _store.GetByUrlAsync(remoteUrl, ct);

    public async Task<MaterialRecord> EnqueueAsync(string courseId, LmsModule module, LmsFile file, CancellationToken ct = default)
    {
        var (record, _) = await EnqueueCoreAsync(courseId, module, file, ct).ConfigureAwait(false);
        return record;
    }

    public async Task<int> EnqueueCourseAsync(string courseId, CancellationToken ct = default)
    {
        var platon = await _connections.GetPlatonAsync(ct).ConfigureAwait(false);
        var sections = await platon.GetCourseContentsAsync(courseId, ct).ConfigureAwait(false);
        var queued = 0;
        foreach (var module in sections.SelectMany(s => s.Modules))
        {
            foreach (var file in module.Files)
            {
                if (string.IsNullOrWhiteSpace(file.Url))
                    continue;
                var (_, changed) = await EnqueueCoreAsync(courseId, module, file, ct).ConfigureAwait(false);
                if (changed)
                    queued++;
            }
        }
        return queued;
    }

    public async Task RemoveAsync(string materialId, CancellationToken ct = default)
    {
        var record = (await _store.ListAsync(null, ct).ConfigureAwait(false)).FirstOrDefault(r => r.Id == materialId);
        if (record is null)
            return;

        foreach (var path in new[] { record.LocalPath, record.LocalPath is null ? null : record.LocalPath + ".part", PartPath(record) })
        {
            if (path is null)
                continue;
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Nie udało się usunąć pliku {Path}", path);
            }
        }
        await _store.RemoveAsync(materialId, ct).ConfigureAwait(false);
    }

    public async Task<long> GetTotalSizeAsync(CancellationToken ct = default)
    {
        var all = await _store.ListAsync(null, ct).ConfigureAwait(false);
        return all.Where(r => r.State == DownloadState.Done).Sum(r => r.Size ?? 0);
    }

    public async Task ProcessQueueAsync(CancellationToken ct = default)
    {
        await _processLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var handled = new HashSet<string>();
            IPlatonAdapter? platon = null;
            while (!ct.IsCancellationRequested)
            {
                if (!_connectivity.IsOnline)
                    return;
                if (_options.DownloadOnlyOnUnmetered && _connectivity.IsMetered)
                    return;

                var next = (await _store.ListAsync(null, ct).ConfigureAwait(false))
                    .Where(r => r.State is DownloadState.Queued or DownloadState.Downloading && !handled.Contains(r.Id))
                    .OrderBy(r => r.UpdatedAt)
                    .FirstOrDefault();
                if (next is null)
                    return;
                handled.Add(next.Id);

                try
                {
                    platon ??= await _connections.GetPlatonAsync(ct).ConfigureAwait(false);
                }
                catch (NotConnectedException)
                {
                    return; // bez połączenia z Platonem kolejka czeka
                }

                var stop = await DownloadWithRetryAsync(platon, next, ct).ConfigureAwait(false);
                if (stop)
                    return;
            }
        }
        finally
        {
            _processLock.Release();
        }
    }

    // ---------- kolejkowanie ----------

    private async Task<(MaterialRecord Record, bool Changed)> EnqueueCoreAsync(string courseId, LmsModule module, LmsFile file, CancellationToken ct)
    {
        var now = _clock.Now;
        var existing = await _store.GetByUrlAsync(file.Url, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            var outdated = existing.State == DownloadState.Done
                && file.Modified is not null
                && (existing.RemoteModified is null || file.Modified > existing.RemoteModified);
            var requeue = existing.State == DownloadState.Failed || outdated
                || (existing.State == DownloadState.Done && (existing.LocalPath is null || !File.Exists(existing.LocalPath)));
            if (!requeue)
                return (existing, false);

            var again = existing with
            {
                State = DownloadState.Queued,
                Error = null,
                RemoteModified = file.Modified ?? existing.RemoteModified,
                Size = file.Size ?? existing.Size,
                UpdatedAt = now,
            };
            await _store.UpsertAsync(again, ct).ConfigureAwait(false);
            _progress.OnNext(new DownloadProgress(again.Id, 0, again.Size, DownloadState.Queued, null));
            return (again, true);
        }

        var moduleId = module.Id;
        var fileName = SafeFileName(file.Name, file.Url);
        var siblings = await _store.ListAsync(courseId, ct).ConfigureAwait(false);
        if (siblings.Any(r => r.ModuleId == moduleId && string.Equals(r.FileName, fileName, StringComparison.OrdinalIgnoreCase)))
            fileName = $"{ShortHash(file.Url)[..6]}_{fileName}";

        var record = new MaterialRecord(
            Id: "m_" + ShortHash(file.Url),
            CourseId: courseId,
            ModuleId: moduleId,
            FileName: fileName,
            RemoteUrl: file.Url,
            LocalPath: null,
            Size: file.Size,
            MimeType: file.MimeType,
            RemoteModified: file.Modified,
            State: DownloadState.Queued,
            Error: null,
            UpdatedAt: now);
        await _store.UpsertAsync(record, ct).ConfigureAwait(false);
        _progress.OnNext(new DownloadProgress(record.Id, 0, record.Size, DownloadState.Queued, null));
        return (record, true);
    }

    // ---------- pobieranie ----------

    /// <summary>Zwraca true, gdy trzeba przerwać całą kolejkę (np. wygasła sesja).</summary>
    private async Task<bool> DownloadWithRetryAsync(IPlatonAdapter platon, MaterialRecord queued, CancellationToken ct)
    {
        // Rekord mógł zostać usunięty od czasu wylistowania kolejki.
        var record = await _store.GetByUrlAsync(queued.RemoteUrl, ct).ConfigureAwait(false);
        if (record is null)
            return false;

        string? lastError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                record = record with { State = DownloadState.Downloading, Error = null, UpdatedAt = _clock.Now };
                await _store.UpsertAsync(record, ct).ConfigureAwait(false);
                _progress.OnNext(new DownloadProgress(record.Id, 0, record.Size, DownloadState.Downloading, null));

                var done = await DownloadOnceAsync(platon, record, ct).ConfigureAwait(false);
                await _store.UpsertAsync(done, ct).ConfigureAwait(false);
                _progress.OnNext(new DownloadProgress(done.Id, done.Size ?? 0, done.Size, DownloadState.Done, null));
                return false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Zatrzymane przez wywołującego: zostaje w kolejce, plik .part pozwoli wznowić.
                await _store.UpsertAsync(record with { State = DownloadState.Queued, UpdatedAt = _clock.Now }, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (AdapterAuthException ex)
            {
                await FailAsync(record, ex.Message).ConfigureAwait(false);
                return true;
            }
            catch (DownloadHttpException ex) when (!ex.IsTransient)
            {
                await FailAsync(record, ex.Message).ConfigureAwait(false);
                return ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or AdapterUnavailableException or DownloadHttpException or TimeoutException or OperationCanceledException)
            {
                lastError = ex.Message;
                _logger.LogWarning(ex, "Pobieranie {Url} nieudane (próba {Attempt}/{Max})", record.RemoteUrl, attempt, MaxAttempts);
                if (attempt < MaxAttempts && _retryBaseDelay > TimeSpan.Zero)
                    await Task.Delay(TimeSpan.FromTicks(_retryBaseDelay.Ticks << (attempt - 1)), ct).ConfigureAwait(false);
            }
        }

        await FailAsync(record, lastError ?? "Nieznany błąd pobierania").ConfigureAwait(false);
        return false;
    }

    private async Task FailAsync(MaterialRecord record, string error)
    {
        var failed = record with { State = DownloadState.Failed, Error = error, UpdatedAt = _clock.Now };
        await _store.UpsertAsync(failed, CancellationToken.None).ConfigureAwait(false);
        _progress.OnNext(new DownloadProgress(failed.Id, 0, failed.Size, DownloadState.Failed, error));
    }

    private async Task<MaterialRecord> DownloadOnceAsync(IPlatonAdapter platon, MaterialRecord record, CancellationToken ct)
    {
        var dest = DestinationPath(record);
        var part = dest + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        var auth = await platon.GetFileDownloadAsync(record.RemoteUrl, ct).ConfigureAwait(false);
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, auth.Url);
        foreach (var (name, value) in auth.Headers)
            request.Headers.TryAddWithoutValidation(name, value);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        var client = _http.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(part); // zły fragment — następna próba od zera
            throw new DownloadHttpException(response.StatusCode, "Serwer odrzucił wznowienie pobierania", transient: true);
        }
        if (!response.IsSuccessStatusCode)
            throw new DownloadHttpException(response.StatusCode, $"Serwer zwrócił {(int)response.StatusCode} {response.ReasonPhrase}",
                transient: response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500);

        var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        var received = append ? existing : 0;
        var total = response.Content.Headers.ContentLength is { } len ? len + received : record.Size;

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                _progress.OnNext(new DownloadProgress(record.Id, received, total, DownloadState.Downloading, null));
            }
        }

        File.Move(part, dest, overwrite: true);
        return record with
        {
            LocalPath = dest,
            Size = received,
            MimeType = record.MimeType ?? response.Content.Headers.ContentType?.MediaType,
            State = DownloadState.Done,
            Error = null,
            UpdatedAt = _clock.Now,
        };
    }

    private string DestinationPath(MaterialRecord r) =>
        Path.Combine(_paths.MaterialsDirectory, SafeSegment(r.CourseId), SafeSegment(r.ModuleId), r.FileName);

    private string PartPath(MaterialRecord r) => DestinationPath(r) + ".part";

    // ---------- nazwy ----------

    private static string ShortHash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 8));

    private static string SafeSegment(string value)
    {
        var s = SanitizeChars(value);
        return s.Length == 0 || s.All(c => c == '.') ? "_" : s;
    }

    /// <summary>Bezpieczna nazwa pliku: bez separatorów ścieżek, znaków zakazanych i ".."; z fallbackiem z URL-a.</summary>
    internal static string SafeFileName(string? name, string url)
    {
        var candidate = name;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
            candidate = Uri.UnescapeDataString(path.Split('/').LastOrDefault() ?? "");
        }
        var s = SanitizeChars(candidate);
        if (s.Length == 0 || s.All(c => c == '.'))
            s = "plik";
        if (s.Length > 150)
        {
            var ext = Path.GetExtension(s);
            if (ext.Length > 20) ext = "";
            s = s[..(150 - ext.Length)] + ext;
        }
        return s;
    }

    private static string SanitizeChars(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(Array.IndexOf(invalid, c) >= 0 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
        var result = sb.ToString();
        while (result.Contains("..", StringComparison.Ordinal))
            result = result.Replace("..", "_", StringComparison.Ordinal);
        return result.Trim().TrimEnd('.', ' ');
    }

    private sealed class DownloadHttpException(HttpStatusCode statusCode, string message, bool transient) : Exception(message)
    {
        public HttpStatusCode StatusCode { get; } = statusCode;
        public bool IsTransient { get; } = transient;
    }
}
