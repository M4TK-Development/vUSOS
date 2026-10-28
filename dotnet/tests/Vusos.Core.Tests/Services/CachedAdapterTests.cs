using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Services;

namespace Vusos.Core.Tests.Services;

public sealed class CachedAdapterTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly FakeClock _clock = new();
    private readonly FakeConnectivity _net = new();
    private readonly SyncStatus _sync = new();
    private readonly CacheOptions _opts = new()
    {
        FreshFor = TimeSpan.FromMinutes(2),
        ServeStaleUpTo = TimeSpan.FromDays(90),
        LiveTimeout = TimeSpan.FromMilliseconds(200),
    };
    private readonly FakeUsosAdapter _usos = new();
    private readonly FakePlatonAdapter _platon = new();
    private readonly CachedUsosAdapter _cachedUsos;
    private readonly CachedPlatonAdapter _cachedPlaton;

    public CachedAdapterTests()
    {
        _cachedUsos = new CachedUsosAdapter(_usos, "u1", _t.Cache, _sync, _net, _clock, _opts);
        _cachedPlaton = new CachedPlatonAdapter(_platon, "u1", _t.Cache, _sync, _net, _clock, _opts);
        _usos.Payments = [Pay("p1")];
    }

    public void Dispose() => _t.Dispose();

    private static Payment Pay(string id) => new(id, "czesne", LangText.Of("Czesne"), 100, 0, "PLN", new DateOnly(2026, 11, 1), PaymentState.Unpaid, null, null);

    [Fact]
    public async Task ColdMiss_GoesLive_StoresAndReportsLive()
    {
        var result = await _cachedUsos.GetPaymentsAsync();
        Assert.Equal("p1", result.Single().Id);
        Assert.Equal(1, _usos.CallCount("GetPayments"));
        var st = _sync.Get(SourceSystem.Usos);
        Assert.Equal(DataOrigin.Live, st.LastOrigin);
        Assert.Equal(_clock.Now, st.LastLiveAt);
        Assert.Null(st.LastError);
    }

    [Fact]
    public async Task FreshHit_ReturnsCache_WithoutCallingInner()
    {
        await _cachedUsos.GetPaymentsAsync();
        _usos.Payments = [Pay("p2")];
        _clock.Advance(TimeSpan.FromSeconds(90));

        var result = await _cachedUsos.GetPaymentsAsync();

        Assert.Equal("p1", result.Single().Id);
        Assert.Equal(1, _usos.CallCount("GetPayments"));
    }

    [Fact]
    public async Task Stale_LiveSuccess_RefreshesCache()
    {
        await _cachedUsos.GetPaymentsAsync();
        _usos.Payments = [Pay("p2")];
        _clock.Advance(TimeSpan.FromMinutes(3));

        Assert.Equal("p2", (await _cachedUsos.GetPaymentsAsync()).Single().Id);
        Assert.Equal(2, _usos.CallCount("GetPayments"));

        // nowy wpis jest świeży
        _usos.Payments = [Pay("p3")];
        Assert.Equal("p2", (await _cachedUsos.GetPaymentsAsync()).Single().Id);
        Assert.Equal(DataOrigin.Live, _sync.Get(SourceSystem.Usos).LastOrigin);
    }

    [Fact]
    public async Task Stale_LiveUnavailable_ServesCache_WithCacheOrigin()
    {
        await _cachedUsos.GetPaymentsAsync();
        var fetched = _clock.Now;
        _clock.Advance(TimeSpan.FromHours(1));
        _usos.Failure = new AdapterUnavailableException("USOS 503");

        var result = await _cachedUsos.GetPaymentsAsync();

        Assert.Equal("p1", result.Single().Id);
        var st = _sync.Get(SourceSystem.Usos);
        Assert.Equal(DataOrigin.Cache, st.LastOrigin);
        Assert.Equal(fetched, st.LastCacheAt);
        Assert.Equal("USOS 503", st.LastError);
    }

    [Fact]
    public async Task Stale_LiveTimeout_ServesCache()
    {
        await _cachedUsos.GetPaymentsAsync();
        _clock.Advance(TimeSpan.FromHours(1));
        _usos.Hang = true;

        var result = await _cachedUsos.GetPaymentsAsync();

        Assert.Equal("p1", result.Single().Id);
        Assert.Equal(DataOrigin.Cache, _sync.Get(SourceSystem.Usos).LastOrigin);
        Assert.Contains("limit czasu", _sync.Get(SourceSystem.Usos).LastError);
    }

    [Fact]
    public async Task Timeout_WithoutCache_ThrowsUnavailable()
    {
        _usos.Hang = true;
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => _cachedUsos.GetPaymentsAsync());
    }

    [Fact]
    public async Task CallerCancellation_IsNotTreatedAsTimeout()
    {
        await _cachedUsos.GetPaymentsAsync();
        _clock.Advance(TimeSpan.FromHours(1));
        _usos.Hang = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _cachedUsos.GetPaymentsAsync(cts.Token));
    }

    [Fact]
    public async Task Offline_ServesCache_WithoutCallingInner_EvenWhenFreshExpired()
    {
        await _cachedUsos.GetPaymentsAsync();
        _clock.Advance(TimeSpan.FromHours(5));
        _net.SetOnline(false);

        var result = await _cachedUsos.GetPaymentsAsync();

        Assert.Equal("p1", result.Single().Id);
        Assert.Equal(1, _usos.CallCount("GetPayments"));
        Assert.Equal(DataOrigin.Cache, _sync.Get(SourceSystem.Usos).LastOrigin);
    }

    [Fact]
    public async Task Offline_WithoutCache_ThrowsUnavailable()
    {
        _net.SetOnline(false);
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => _cachedUsos.GetPaymentsAsync());
        Assert.Equal(0, _usos.CallCount("GetPayments"));
    }

    [Fact]
    public async Task AuthError_Online_IsRethrown_EvenWithCache()
    {
        await _cachedUsos.GetPaymentsAsync();
        _clock.Advance(TimeSpan.FromHours(1));
        _usos.Failure = new AdapterAuthException();
        AdapterAuthException? seen = null;
        var decorated = new CachedUsosAdapter(_usos, "u1", _t.Cache, _sync, _net, _clock, _opts, null, ex => seen = ex);

        await Assert.ThrowsAsync<AdapterAuthException>(() => decorated.GetPaymentsAsync());
        Assert.NotNull(seen);
    }

    [Fact]
    public async Task AuthError_WhileOffline_ServesCache()
    {
        await _cachedUsos.GetPaymentsAsync();
        _clock.Advance(TimeSpan.FromHours(1));
        _usos.Failure = new AdapterAuthException();
        _net.SetOnline(false);

        Assert.Equal("p1", (await _cachedUsos.GetPaymentsAsync()).Single().Id);
        Assert.Equal(1, _usos.CallCount("GetPayments"));
    }

    [Fact]
    public async Task ServeStaleUpTo_Exceeded_RethrowsLiveError()
    {
        await _cachedUsos.GetPaymentsAsync();
        _clock.Advance(TimeSpan.FromDays(91));
        _usos.Failure = new AdapterUnavailableException("down");

        var ex = await Assert.ThrowsAsync<AdapterUnavailableException>(() => _cachedUsos.GetPaymentsAsync());
        Assert.Equal("down", ex.Message);

        _net.SetOnline(false);
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => _cachedUsos.GetPaymentsAsync());
    }

    [Fact]
    public async Task NonTransientErrors_AreNotSwallowed()
    {
        await _cachedUsos.GetPaymentsAsync();
        _clock.Advance(TimeSpan.FromHours(1));
        _usos.Failure = new AdapterNotSupportedException("brak modułu");
        await Assert.ThrowsAsync<AdapterNotSupportedException>(() => _cachedUsos.GetPaymentsAsync());
    }

    [Fact]
    public async Task DifferentArguments_UseDifferentKeys()
    {
        _usos.Messages = [new MessageThread("m1", SourceSystem.Usos, "S", "A", [], _clock.Now, "", true, null)];
        await _cachedUsos.ListMessagesAsync(10, 0);
        await _cachedUsos.ListMessagesAsync(10, 10);
        await _cachedUsos.ListMessagesAsync(10, 0);
        Assert.Equal(2, _usos.CallCount("ListMessages"));
    }

    [Fact]
    public async Task SendMessage_IsNeverCached_AndInvalidatesMessageList()
    {
        _usos.Messages = [new MessageThread("m1", SourceSystem.Usos, "S", "A", [], _clock.Now, "", true, null)];
        await _cachedUsos.ListMessagesAsync();
        await _cachedUsos.GetPaymentsAsync();
        await _cachedUsos.ListMessagesAsync(); // z cache
        Assert.Equal(1, _usos.CallCount("ListMessages"));

        await _cachedUsos.SendMessageAsync(["x"], "s", "b");
        await _cachedUsos.SendMessageAsync(["x"], "s", "b");
        Assert.Equal(2, _usos.CallCount("SendMessage"));

        await _cachedUsos.ListMessagesAsync(); // unieważnione → sieć
        Assert.Equal(2, _usos.CallCount("ListMessages"));
        await _cachedUsos.GetPaymentsAsync(); // inne metody nietknięte
        Assert.Equal(1, _usos.CallCount("GetPayments"));
    }

    [Fact]
    public async Task FailedWrite_DoesNotInvalidate()
    {
        _usos.Messages = [new MessageThread("m1", SourceSystem.Usos, "S", "A", [], _clock.Now, "", true, null)];
        await _cachedUsos.ListMessagesAsync();
        _usos.Failure = new AdapterUnavailableException("down");
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => _cachedUsos.SendMessageAsync(["x"], "s", "b"));
        _usos.Failure = null;
        _net.SetOnline(false);
        Assert.Single(await _cachedUsos.ListMessagesAsync());
    }

    // ---- Platon ----

    [Fact]
    public async Task Platon_QuizWrites_InvalidateAttemptsAndNeverCache()
    {
        _platon.Attempts = [new QuizAttempt("a1", "q1", 1, QuizAttemptState.Finished, null, null, null, null)];
        await _cachedPlaton.GetQuizAttemptsAsync("q1");
        await _cachedPlaton.GetQuizAttemptsAsync("q1");
        Assert.Equal(1, _platon.CallCount("GetQuizAttempts"));

        var started = await _cachedPlaton.StartQuizAttemptAsync("q1");
        Assert.Equal(2, (await _cachedPlaton.GetQuizAttemptsAsync("q1")).Count);
        Assert.Equal(2, _platon.CallCount("GetQuizAttempts"));
        Assert.Equal("att-2", started.Id);

        await _cachedPlaton.SaveQuizAttemptAsync(started.Id, [new QuizAnswer("a", "b")]);
        await _cachedPlaton.GetQuizAttemptsAsync("q1");
        Assert.Equal(3, _platon.CallCount("GetQuizAttempts"));

        await _cachedPlaton.ProcessQuizAttemptAsync(started.Id, [], true);
        await _cachedPlaton.GetQuizAttemptsAsync("q1");
        Assert.Equal(4, _platon.CallCount("GetQuizAttempts"));

        await _cachedPlaton.GetQuizAttemptDataAsync(started.Id);
        await _cachedPlaton.GetQuizAttemptDataAsync(started.Id);
        Assert.Equal(2, _platon.CallCount("GetQuizAttemptData"));
        await _cachedPlaton.GetFileDownloadAsync("u");
        await _cachedPlaton.GetFileDownloadAsync("u");
        Assert.Equal(2, _platon.CallCount("GetFileDownload"));
    }

    [Fact]
    public async Task Platon_Reads_ReportAgainstPlatonSystem()
    {
        _platon.Courses = [new LmsCourse("1", "S", "Full", null, null, null, null, null, "u", null, null)];
        await _cachedPlaton.GetCoursesAsync();
        Assert.Equal(DataOrigin.Live, _sync.Get(SourceSystem.Platon).LastOrigin);
        Assert.Null(_sync.Get(SourceSystem.Usos).LastLiveAt);

        _clock.Advance(TimeSpan.FromHours(1));
        _platon.Failure = new AdapterUnavailableException("moodle down");
        Assert.Single(await _cachedPlaton.GetCoursesAsync());
        Assert.Equal(DataOrigin.Cache, _sync.Get(SourceSystem.Platon).LastOrigin);
    }

    [Fact]
    public async Task UserScopes_DoNotShareCache()
    {
        await _cachedUsos.GetPaymentsAsync();
        var other = new CachedUsosAdapter(_usos, "u2", _t.Cache, _sync, _net, _clock, _opts);
        await other.GetPaymentsAsync();
        Assert.Equal(2, _usos.CallCount("GetPayments"));
    }
}
