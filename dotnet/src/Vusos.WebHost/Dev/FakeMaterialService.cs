using System.Collections.Concurrent;
using System.Text;
using Vusos.Core.Domain;
using Vusos.Core.Offline;
using Vusos.Core.Platform;
using Vusos.Core.Services;

namespace Vusos.WebHost.Dev;

/// <summary>Minimalny IObservable bez System.Reactive.</summary>
public sealed class FakeSubject<T> : IObservable<T>
{
    private readonly List<IObserver<T>> _observers = [];
    private readonly object _lock = new();

    public IDisposable Subscribe(IObserver<T> observer)
    {
        lock (_lock) _observers.Add(observer);
        return new Unsubscriber(() => { lock (_lock) _observers.Remove(observer); });
    }

    public void OnNext(T value)
    {
        IObserver<T>[] copy;
        lock (_lock) copy = [.. _observers];
        foreach (var o in copy) o.OnNext(value);
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

public sealed class FakeMaterialService(IAppPaths paths, FakeBackend backend) : IMaterialService
{
    private readonly ConcurrentDictionary<string, MaterialRecord> _records = new();
    private readonly FakeSubject<DownloadProgress> _progress = new();
    private readonly SemaphoreSlim _queueLock = new(1, 1);

    public IObservable<DownloadProgress> Progress => _progress;

    public Task<IReadOnlyList<MaterialRecord>> ListAsync(string? courseId = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MaterialRecord>>(_records.Values
            .Where(r => courseId is null || r.CourseId == courseId)
            .OrderBy(r => r.FileName).ToList());

    public Task<MaterialRecord?> FindAsync(string remoteUrl, CancellationToken ct = default)
        => Task.FromResult(_records.Values.FirstOrDefault(r => r.RemoteUrl == remoteUrl));

    public Task<MaterialRecord> EnqueueAsync(string courseId, LmsModule module, LmsFile file, CancellationToken ct = default)
    {
        var id = $"{module.Id}:{file.Name}";
        var record = _records.AddOrUpdate(id,
            _ => New(courseId, module.Id, file, id),
            (_, cur) => cur.State == DownloadState.Done ? cur : cur with { State = DownloadState.Queued, Error = null, UpdatedAt = DateTimeOffset.UtcNow });
        return Task.FromResult(record);
    }

    private static MaterialRecord New(string courseId, string moduleId, LmsFile file, string id)
        => new(id, courseId, moduleId, file.Name, file.Url, null, file.Size, file.MimeType, file.Modified, DownloadState.Queued, null, DateTimeOffset.UtcNow);

    public Task<int> EnqueueCourseAsync(string courseId, CancellationToken ct = default)
    {
        var count = 0;
        foreach (var module in FakeData.CourseContents(courseId, DateTimeOffset.UtcNow).SelectMany(s => s.Modules))
            foreach (var file in module.Files)
            {
                var id = $"{module.Id}:{file.Name}";
                if (_records.TryGetValue(id, out var existing) && existing.State == DownloadState.Done) continue;
                _records[id] = New(courseId, module.Id, file, id);
                count++;
            }
        return Task.FromResult(count);
    }

    public Task RemoveAsync(string materialId, CancellationToken ct = default)
    {
        if (_records.TryRemove(materialId, out var r) && r.LocalPath is { } p && File.Exists(p))
            File.Delete(p);
        return Task.CompletedTask;
    }

    public Task<long> GetTotalSizeAsync(CancellationToken ct = default)
        => Task.FromResult(_records.Values.Where(r => r.State == DownloadState.Done).Sum(r => r.Size ?? 0));

    public async Task ProcessQueueAsync(CancellationToken ct = default)
    {
        if (!backend.Connectivity.IsOnline) return;
        if (!await _queueLock.WaitAsync(0, ct)) return;
        try
        {
            foreach (var queued in _records.Values.Where(r => r.State == DownloadState.Queued).OrderBy(r => r.UpdatedAt).ToList())
            {
                if (!backend.Connectivity.IsOnline) break;
                await DownloadAsync(queued, ct);
            }
        }
        finally { _queueLock.Release(); }
    }

    private async Task DownloadAsync(MaterialRecord record, CancellationToken ct)
    {
        var content = BuildContent(record.FileName);
        var total = (long)content.Length;
        _records[record.Id] = record with { State = DownloadState.Downloading, UpdatedAt = DateTimeOffset.UtcNow };
        for (var step = 1; step <= 5; step++)
        {
            await Task.Delay(180, ct);
            _progress.OnNext(new DownloadProgress(record.Id, total * step / 5, total, DownloadState.Downloading, null));
        }

        var dir = Path.Combine(paths.MaterialsDirectory, Safe(record.CourseId), Safe(record.ModuleId));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Safe(record.FileName));
        await File.WriteAllBytesAsync(path, content, ct);
        _records[record.Id] = record with { State = DownloadState.Done, LocalPath = path, Size = total, UpdatedAt = DateTimeOffset.UtcNow };
        _progress.OnNext(new DownloadProgress(record.Id, total, total, DownloadState.Done, null));
    }

    private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c));

    /// <summary>Plik PDF jest poprawnym (minimalnym) PDF-em, resztę zapisujemy jako tekst.</summary>
    private static byte[] BuildContent(string fileName)
    {
        var text = $"vUSOS – plik demonstracyjny: {fileName}";
        if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return Encoding.UTF8.GetBytes(text + "\n");

        var stream = $"BT /F1 18 Tf 72 720 Td ({new string(text.Where(c => c < 128).ToArray()).Replace("(", "[").Replace(")", "]")}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
