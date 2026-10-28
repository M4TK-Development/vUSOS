using System.Net;
using System.Net.Http.Headers;
using Vusos.Core.Domain;
using Vusos.Core.Offline;
using Vusos.Core.Services;

namespace Vusos.Core.Tests.Services;

public sealed class MaterialServiceTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly FakeClock _clock = new();
    private readonly FakeConnectivity _net = new();
    private readonly FakePlatonAdapter _platon = new();
    private readonly CacheOptions _opts = new();
    private readonly List<HttpRequestMessage> _requests = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _respond;
    private readonly MaterialService _svc;

    public MaterialServiceTests()
    {
        var payload = new byte[300_000];
        new Random(1).NextBytes(payload);
        Payload = payload;
        _respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
        _platon.Download = new Adapters.AuthenticatedDownload(new Uri("https://platon.example/webservice/pluginfile.php/1/a.pdf?token=T"),
            new Dictionary<string, string> { ["X-Test"] = "yes" });
        var factory = new FakeHttpClientFactory(new HttpClient(new Handler(this)));
        _svc = new MaterialService(_t.Materials, new StubConnectionManager { Platon = _platon }, factory, _t.Paths, _net, _clock, _opts,
            retryBaseDelay: TimeSpan.Zero);
    }

    private byte[] Payload { get; }

    public void Dispose() => _t.Dispose();

    private static LmsModule Module(string id, params LmsFile[] files) =>
        new(id, "c1", "s1", "Sekcja", "Moduł " + id, LmsModuleType.Resource, "resource", null, null, files, null, true);

    private static LmsFile File_(string name, string url = "https://platon.example/pluginfile.php/1/a.pdf", DateTimeOffset? modified = null) =>
        new(name, url, "application/pdf", null, modified);

    [Fact]
    public async Task Download_StreamsToCoursePath_WithProgress_AndMarksDone()
    {
        var events = new List<DownloadProgress>();
        using var sub = _svc.Progress.Subscribe(new Collector(events));

        var rec = await _svc.EnqueueAsync("c1", Module("m1"), File_("wykład 1.pdf"));
        Assert.Equal(DownloadState.Queued, rec.State);
        Assert.Equal(rec, await _svc.FindAsync(rec.RemoteUrl));

        await _svc.ProcessQueueAsync();

        var done = (await _svc.ListAsync("c1")).Single();
        Assert.Equal(DownloadState.Done, done.State);
        var expectedPath = Path.Combine(_t.Paths.MaterialsDirectory, "c1", "m1", "wykład 1.pdf");
        Assert.Equal(expectedPath, done.LocalPath);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(expectedPath));
        Assert.False(File.Exists(expectedPath + ".part"));
        Assert.Equal(Payload.Length, done.Size);
        Assert.Equal(Payload.Length, await _svc.GetTotalSizeAsync());

        // nagłówki z adaptera dotarły do żądania
        var req = Assert.Single(_requests);
        Assert.Equal("https://platon.example/webservice/pluginfile.php/1/a.pdf?token=T", req.RequestUri!.ToString());
        Assert.Equal("yes", req.Headers.GetValues("X-Test").Single());

        Assert.Equal(DownloadState.Queued, events.First().State);
        Assert.Contains(events, e => e.State == DownloadState.Downloading && e.BytesReceived > 0 && e.TotalBytes == Payload.Length);
        var downloading = events.Where(e => e.State == DownloadState.Downloading).ToList();
        Assert.True(downloading.Count > 2);
        Assert.Equal(downloading.OrderBy(e => e.BytesReceived).Select(e => e.BytesReceived), downloading.Select(e => e.BytesReceived));
        Assert.Equal(DownloadState.Done, events.Last().State);
        Assert.Equal(Payload.Length, events.Last().BytesReceived);
        Assert.All(events, e => Assert.Equal(rec.Id, e.MaterialId));
    }

    [Fact]
    public async Task Enqueue_IsIdempotentPerUrl_AndRequeuesWhenRemoteIsNewer()
    {
        var t0 = _clock.Now;
        var first = await _svc.EnqueueAsync("c1", Module("m1"), File_("a.pdf", modified: t0));
        var again = await _svc.EnqueueAsync("c1", Module("m1"), File_("a.pdf", modified: t0));
        Assert.Equal(first.Id, again.Id);
        Assert.Single(await _svc.ListAsync());

        await _svc.ProcessQueueAsync();
        Assert.Equal(DownloadState.Done, (await _svc.ListAsync()).Single().State);

        var same = await _svc.EnqueueAsync("c1", Module("m1"), File_("a.pdf", modified: t0));
        Assert.Equal(DownloadState.Done, same.State);

        var newer = await _svc.EnqueueAsync("c1", Module("m1"), File_("a.pdf", modified: t0.AddDays(1)));
        Assert.Equal(DownloadState.Queued, newer.State);
    }

    [Fact]
    public async Task MeteredNetwork_BlocksDownloads_WhenConfigured()
    {
        _opts.DownloadOnlyOnUnmetered = true;
        _net.IsMetered = true;
        await _svc.EnqueueAsync("c1", Module("m1"), File_("a.pdf"));

        await _svc.ProcessQueueAsync();
        Assert.Empty(_requests);
        Assert.Equal(DownloadState.Queued, (await _svc.ListAsync()).Single().State);

        _net.IsMetered = false;
        await _svc.ProcessQueueAsync();
        Assert.Single(_requests);
        Assert.Equal(DownloadState.Done, (await _svc.ListAsync()).Single().State);
    }

    [Fact]
    public async Task MeteredNetwork_IsFine_WhenOptionDisabled()
    {
        _net.IsMetered = true;
        await _svc.EnqueueAsync("c1", Module("m1"), File_("a.pdf"));
        await _svc.ProcessQueueAsync();
        Assert.Equal(DownloadState.Done, (await _svc.ListAsync()).Single().State);
    }

    [Fact]
    public async Task Offline_LeavesQueueUntouched()
    {
        await _svc.EnqueueAsync("c1", Module("m1"), File_("a.pdf"));
        _net.SetOnline(false);
        await _svc.ProcessQueueAsync();
        Assert.Empty(_requests);
        Assert.Equal(DownloadState.Queued, (await _svc.ListAsync()).Single().State);
    }

    [Fact]
    public async Task TransientFailures_AreRetried_UpToThreeTimes()
    {
        var n = 0;
        _respond = _ => ++n < 3
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
        await _svc.EnqueueAsync("c1", Module("m1"), File_("a.pdf"));

        await _svc.ProcessQueueAsync();

        Assert.Equal(3, _requests.Count);
        Assert.Equal(DownloadState.Done, (await _svc.ListAsync()).Single().State);
    }

    [Fact]
    public async Task PersistentFailure_MarksFailedAfterThreeAttempts_AndContinuesWithNextFile()
    {
        _respond = r => r.RequestUri!.AbsolutePath.EndsWith("bad.pdf", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
        // adapter zwraca adres równy URL-owi pliku
        var svc = new MaterialService(_t.Materials, new StubConnectionManager { Platon = new UrlEchoPlaton() },
            new FakeHttpClientFactory(new HttpClient(new Handler(this))), _t.Paths, _net, _clock, _opts, retryBaseDelay: TimeSpan.Zero);
        var events = new List<DownloadProgress>();
        using var sub = svc.Progress.Subscribe(new Collector(events));

        var bad = await svc.EnqueueAsync("c1", Module("m1"), File_("bad.pdf", "https://platon.example/pluginfile.php/bad.pdf"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await svc.EnqueueAsync("c1", Module("m1"), File_("ok.pdf", "https://platon.example/pluginfile.php/ok.pdf"));

        await svc.ProcessQueueAsync();

        var all = await svc.ListAsync();
        var failed = all.Single(r => r.Id == bad.Id);
        Assert.Equal(DownloadState.Failed, failed.State);
        Assert.Contains("502", failed.Error);
        Assert.Equal(3, _requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("bad.pdf", StringComparison.Ordinal)));
        Assert.Equal(DownloadState.Done, all.Single(r => r.Id != bad.Id).State);
        Assert.Contains(events, e => e.State == DownloadState.Failed && e.Error != null);
    }

    [Fact]
    public async Task NotFound_IsNotRetried()
    {
        _respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        await _svc.EnqueueAsync("c1", Module("m1"), File_("a.pdf"));
        await _svc.ProcessQueueAsync();
        Assert.Single(_requests);
        Assert.Equal(DownloadState.Failed, (await _svc.ListAsync()).Single().State);
    }

    [Fact]
    public async Task Failed_CanBeRequeuedByEnqueueAgain()
    {
        _respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        await _svc.EnqueueAsync("c1", Module("m1"), File_("a.pdf"));
        await _svc.ProcessQueueAsync();
        _respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
        var again = await _svc.EnqueueAsync("c1", Module("m1"), File_("a.pdf"));
        Assert.Equal(DownloadState.Queued, again.State);
        await _svc.ProcessQueueAsync();
        Assert.Equal(DownloadState.Done, (await _svc.ListAsync()).Single().State);
    }

    [Fact]
    public async Task EnqueueCourse_QueuesAllModuleFiles()
    {
        _platon.Contents["c1"] =
        [
            new LmsSection("s1", "Wstęp", null, [Module("m1", File_("a.pdf", "https://p/a"), File_("b.pdf", "https://p/b")), Module("m2")]),
            new LmsSection("s2", "Dalej", null, [Module("m3", File_("c.pdf", "https://p/c"))]),
        ];

        Assert.Equal(3, await _svc.EnqueueCourseAsync("c1"));
        Assert.Equal(0, await _svc.EnqueueCourseAsync("c1")); // idempotentne
        var list = await _svc.ListAsync("c1");
        Assert.Equal(3, list.Count);
        Assert.All(list, r => Assert.Equal(DownloadState.Queued, r.State));
        Assert.Equal(["m1", "m1", "m3"], list.Select(r => r.ModuleId));

        await _svc.ProcessQueueAsync();
        Assert.All(await _svc.ListAsync("c1"), r => Assert.Equal(DownloadState.Done, r.State));
        Assert.Equal(3L * Payload.Length, await _svc.GetTotalSizeAsync());
    }

    [Fact]
    public async Task Remove_DeletesFileAndRecord()
    {
        var rec = await _svc.EnqueueAsync("c1", Module("m1"), File_("a.pdf"));
        await _svc.ProcessQueueAsync();
        var path = (await _svc.ListAsync()).Single().LocalPath!;
        Assert.True(File.Exists(path));

        await _svc.RemoveAsync(rec.Id);

        Assert.False(File.Exists(path));
        Assert.Empty(await _svc.ListAsync());
        Assert.Equal(0, await _svc.GetTotalSizeAsync());
        await _svc.RemoveAsync("does-not-exist"); // bez wyjątku
    }

    [Fact]
    public async Task HostileFileNames_StayInsideMaterialsDirectory()
    {
        var rec = await _svc.EnqueueAsync("c1", Module("m1"), File_("../../evil/..\\x:y?.pdf", "https://p/evil"));
        Assert.DoesNotContain("..", rec.FileName, StringComparison.Ordinal);
        Assert.DoesNotContain('/', rec.FileName);
        Assert.DoesNotContain('\\', rec.FileName);
        await _svc.ProcessQueueAsync();
        var path = Path.GetFullPath((await _svc.ListAsync()).Single().LocalPath!);
        Assert.StartsWith(Path.GetFullPath(_t.Paths.MaterialsDirectory), path, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(_t.Paths.MaterialsDirectory, "c1", "m1"), Path.GetDirectoryName(path));
    }

    [Fact]
    public async Task SameNameInSameModule_GetsDistinctFiles()
    {
        await _svc.EnqueueAsync("c1", Module("m1"), File_("a.pdf", "https://p/1"));
        await _svc.EnqueueAsync("c1", Module("m1"), File_("a.pdf", "https://p/2"));
        await _svc.ProcessQueueAsync();
        var paths = (await _svc.ListAsync()).Select(r => r.LocalPath).ToList();
        Assert.Equal(2, paths.Distinct().Count());
    }

    [Fact]
    public async Task PartialFile_IsResumedWithRange()
    {
        var rec = await _svc.EnqueueAsync("c1", Module("m1"), File_("a.pdf"));
        var dir = Path.Combine(_t.Paths.MaterialsDirectory, "c1", "m1");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, "a.pdf.part"), Payload[..1000]);
        _respond = r =>
        {
            var from = r.Headers.Range!.Ranges.Single().From!.Value;
            var resp = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Payload[(int)from..]) };
            resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, Payload.Length - 1, Payload.Length);
            return resp;
        };

        await _svc.ProcessQueueAsync();

        var done = (await _svc.ListAsync()).Single();
        Assert.Equal(DownloadState.Done, done.State);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(done.LocalPath!));
        Assert.Equal(1000, _requests.Single().Headers.Range!.Ranges.Single().From);
        Assert.Equal(rec.Id, done.Id);
    }

    [Fact]
    public async Task ConcurrentProcessCalls_DownloadEachFileOnce()
    {
        await _svc.EnqueueAsync("c1", Module("m1"), File_("a.pdf", "https://p/1"));
        await _svc.EnqueueAsync("c1", Module("m1"), File_("b.pdf", "https://p/2"));
        await Task.WhenAll(_svc.ProcessQueueAsync(), _svc.ProcessQueueAsync(), _svc.ProcessQueueAsync());
        Assert.Equal(2, _requests.Count);
    }

    // ---- pomocnicze ----

    private sealed class Collector(List<DownloadProgress> sink) : IObserver<DownloadProgress>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(DownloadProgress value) { lock (sink) sink.Add(value); }
    }

    private sealed class Handler(MaterialServiceTests owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (owner._requests) owner._requests.Add(request);
            return Task.FromResult(owner._respond(request));
        }
    }

    private sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => name == "platon" ? client : throw new InvalidOperationException("zły klient: " + name);
    }

    /// <summary>Adapter, który zwraca adres równy URL-owi pliku.</summary>
    private sealed class UrlEchoPlaton : FakePlatonAdapterBase
    {
        public override Task<Adapters.AuthenticatedDownload> GetFileDownloadAsync(string url, CancellationToken ct = default) =>
            Task.FromResult(new Adapters.AuthenticatedDownload(new Uri(url), new Dictionary<string, string>()));
    }

    private abstract class FakePlatonAdapterBase : Adapters.IPlatonAdapter
    {
        public virtual Task<Adapters.PlatonSiteInfo> GetSiteInfoAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public virtual Task<IReadOnlyList<LmsCourse>> GetCoursesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public virtual Task<IReadOnlyList<LmsSection>> GetCourseContentsAsync(string courseId, CancellationToken ct = default) => throw new NotSupportedException();
        public virtual Task<IReadOnlyList<LmsAssignment>> GetAssignmentsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public virtual Task<IReadOnlyList<LmsEvent>> GetUpcomingEventsAsync(int days = 14, CancellationToken ct = default) => throw new NotSupportedException();
        public virtual Task<IReadOnlyList<LmsGradeItem>> GetGradesAsync(string courseId, CancellationToken ct = default) => throw new NotSupportedException();
        public virtual Task<IReadOnlyList<Announcement>> GetAnnouncementsAsync(int limit = 20, CancellationToken ct = default) => throw new NotSupportedException();
        public virtual Task<Adapters.AuthenticatedDownload> GetFileDownloadAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
        public virtual Task<IReadOnlyList<LmsQuiz>> GetQuizzesAsync(IReadOnlyList<string>? courseIds = null, CancellationToken ct = default) => throw new NotSupportedException();
        public virtual Task<IReadOnlyList<QuizAttempt>> GetQuizAttemptsAsync(string quizId, CancellationToken ct = default) => throw new NotSupportedException();
        public virtual Task<QuizAttempt> StartQuizAttemptAsync(string quizId, IReadOnlyList<QuizAnswer>? preflight = null, bool forceNew = false, CancellationToken ct = default) => throw new NotSupportedException();
        public virtual Task<QuizAttemptData> GetQuizAttemptDataAsync(string attemptId, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default) => throw new NotSupportedException();
        public virtual Task SaveQuizAttemptAsync(string attemptId, IReadOnlyList<QuizAnswer> answers, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default) => throw new NotSupportedException();
        public virtual Task<QuizAttemptState> ProcessQuizAttemptAsync(string attemptId, IReadOnlyList<QuizAnswer> answers, bool finish, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default) => throw new NotSupportedException();
        public virtual Task<QuizAttemptReview> GetQuizAttemptReviewAsync(string attemptId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
