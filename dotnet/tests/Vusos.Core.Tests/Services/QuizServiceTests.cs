using Vusos.Core.Adapters;
using Vusos.Core.Domain;
using Vusos.Core.Services;

namespace Vusos.Core.Tests.Services;

public sealed class QuizServiceTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly FakeClock _clock = new();
    private readonly FakeConnectivity _net = new();
    private readonly SyncStatus _sync = new();
    private readonly FakePlatonAdapter _platon = new();
    private readonly QuizService _svc;

    private static readonly LmsQuiz OfflineQuiz = new("q1", "cm1", "c1", "Kolokwium", null, null, null, 1800, 3, true, true, "https://p/q1");
    private static readonly LmsQuiz OnlineOnlyQuiz = new("q2", "cm2", "c1", "Egzamin", null, null, null, 1800, 1, false, true, "https://p/q2");

    public QuizServiceTests()
    {
        _platon.Quizzes = [OfflineQuiz, OnlineOnlyQuiz];
        var cached = new CachedPlatonAdapter(_platon, "u", _t.Cache, _sync, _net, _clock, new CacheOptions { FreshFor = TimeSpan.Zero });
        _svc = new QuizService(new StubConnectionManager { Platon = cached }, _t.Quizzes, _net, _sync, _clock);
    }

    public void Dispose() => _t.Dispose();

    private static QuizAnswer A(string n, string v) => new(n, v);

    private async Task<QuizSession> OpenOnlineAsync(LmsQuiz quiz)
    {
        var s = await _svc.OpenAttemptAsync(quiz);
        // UI po otwarciu odświeża listę podejść; dzięki temu cache zna podejście także offline
        await _svc.GetAttemptsAsync(quiz.Id);
        return s;
    }

    [Fact]
    public async Task Open_Online_StartsNewAttempt_AndPersistsData()
    {
        var s = await _svc.OpenAttemptAsync(OfflineQuiz, [A("quizpassword", "pw")]);

        Assert.Equal(1, _platon.CallCount("StartQuizAttempt"));
        Assert.False(s.IsOffline);
        Assert.False(s.HasPendingSync);
        Assert.Equal("att-1", s.Data.Attempt.Id);
        Assert.Single(s.Data.Questions);
        Assert.Equal("pw", s.Data.PreflightData.Single().Value);
        var stored = await _t.Quizzes.GetAttemptDataAsync("att-1");
        Assert.Equal(s.Data.Questions[0].Html, stored!.Questions[0].Html);
    }

    [Fact]
    public async Task Open_Online_ResumesInProgressAttempt_WithoutStartingNew()
    {
        _platon.Attempts = [new QuizAttempt("att-9", "q1", 1, QuizAttemptState.InProgress, null, null, null, 0),
                            new QuizAttempt("att-3", "q1", 0, QuizAttemptState.Finished, null, null, 7, null)];
        _platon.AttemptData = new QuizAttemptData(_platon.Attempts[0], [], []);

        var s = await _svc.OpenAttemptAsync(OfflineQuiz);

        Assert.Equal(0, _platon.CallCount("StartQuizAttempt"));
        Assert.Equal("att-9", s.Data.Attempt.Id);
        Assert.NotNull(await _t.Quizzes.GetAttemptDataAsync("att-9"));
    }

    [Fact]
    public async Task Open_Offline_WithoutAttempt_RequiresConnection()
    {
        _net.SetOnline(false);
        var ex = await Assert.ThrowsAsync<AdapterUnavailableException>(() => _svc.OpenAttemptAsync(OfflineQuiz));
        Assert.Equal("Rozpoczęcie testu wymaga połączenia", ex.Message);
        Assert.Equal(0, _platon.CallCount("StartQuizAttempt"));
    }

    [Fact]
    public async Task Open_Offline_ResumesStoredAttempt_ForOfflineQuiz()
    {
        await OpenOnlineAsync(OfflineQuiz);
        await _svc.SaveAnswersAsync("att-1", [A("q1:1_answer", "x")]);
        _net.SetOnline(false);

        var s = await _svc.OpenAttemptAsync(OfflineQuiz);

        Assert.True(s.IsOffline);
        Assert.Equal("att-1", s.Data.Attempt.Id);
        Assert.Equal("x", s.LocalAnswers.Single().Value);
        Assert.Equal(1, _platon.CallCount("StartQuizAttempt"));
    }

    [Fact]
    public async Task Open_Offline_QuizWithoutOfflineSupport_IsRefused()
    {
        await OpenOnlineAsync(OnlineOnlyQuiz);
        _net.SetOnline(false);
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => _svc.OpenAttemptAsync(OnlineOnlyQuiz));
    }

    [Fact]
    public async Task Resume_LoadsStoredDataAndLocalAnswers()
    {
        await OpenOnlineAsync(OfflineQuiz);
        await _svc.SaveAnswersAsync("att-1", [A("a", "1"), A("b", "2")]);

        var s = await _svc.ResumeAttemptAsync("att-1");
        Assert.Equal(2, s.LocalAnswers.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.ResumeAttemptAsync("nope"));
    }

    [Fact]
    public async Task Save_Online_SendsAllLocalAnswers_AndQueuesNothing()
    {
        await OpenOnlineAsync(OfflineQuiz);
        await _svc.SaveAnswersAsync("att-1", [A("a", "1")]);
        var s = await _svc.SaveAnswersAsync("att-1", [A("b", "2")]);

        Assert.Equal(["save:att-1:1", "save:att-1:2"], _platon.QuizLog);
        Assert.False(s.HasPendingSync);
        Assert.Empty(await _t.Quizzes.ListPendingAsync());
        Assert.Equal(0, _sync.Get(SourceSystem.Platon).PendingActions);
    }

    [Fact]
    public async Task Save_Offline_QueuesLatestStateOnly_AndReportsPending()
    {
        await OpenOnlineAsync(OfflineQuiz);
        _net.SetOnline(false);

        await _svc.SaveAnswersAsync("att-1", [A("a", "1")]);
        _clock.Advance(TimeSpan.FromSeconds(5));
        var s = await _svc.SaveAnswersAsync("att-1", [A("b", "2")]);

        Assert.True(s.IsOffline);
        Assert.True(s.HasPendingSync);
        var pending = await _t.Quizzes.ListPendingAsync();
        var one = Assert.Single(pending);
        Assert.False(one.Finish);
        Assert.Equal(2, one.Answers.Count); // komplet odpowiedzi
        Assert.Empty(_platon.QuizLog);
        Assert.Equal(1, _sync.Get(SourceSystem.Platon).PendingActions);
    }

    [Fact]
    public async Task Save_Offline_ForQuizWithoutOfflineSupport_ThrowsButKeepsLocalAnswers()
    {
        await OpenOnlineAsync(OnlineOnlyQuiz);
        _net.SetOnline(false);

        await Assert.ThrowsAsync<AdapterUnavailableException>(() => _svc.SaveAnswersAsync("att-1", [A("a", "1")]));

        Assert.Empty(await _t.Quizzes.ListPendingAsync());
        Assert.Single(await _t.Quizzes.GetLocalAnswersAsync("att-1"));
    }

    [Fact]
    public async Task Save_OnlineButServerUnavailable_FallsBackToQueue()
    {
        await OpenOnlineAsync(OfflineQuiz);
        _platon.FailQuizWrite = _ => true;
        var s = await _svc.SaveAnswersAsync("att-1", [A("a", "1")]);
        Assert.True(s.HasPendingSync);
        Assert.Single(await _t.Quizzes.ListPendingAsync());
    }

    [Fact]
    public async Task OfflineQueue_SyncsInOrder_SaveThenFinish_WhenOnlineReturns()
    {
        await OpenOnlineAsync(OfflineQuiz);
        _net.SetOnline(false);
        await _svc.SaveAnswersAsync("att-1", [A("a", "1")]);
        _clock.Advance(TimeSpan.FromSeconds(10));
        var finished = await _svc.FinishAsync("att-1");

        Assert.True(finished.HasPendingSync);
        Assert.Equal(QuizAttemptState.Finished, finished.Data.Attempt.State);
        Assert.Equal(2, (await _t.Quizzes.ListPendingAsync()).Count);
        Assert.Equal(2, _sync.Get(SourceSystem.Platon).PendingActions);
        Assert.Empty(_platon.QuizLog);

        Assert.Equal(0, await _svc.SyncPendingAsync()); // nadal offline

        _net.SetOnline(true);
        var synced = await _svc.SyncPendingAsync();

        Assert.Equal(2, synced);
        Assert.Equal(["save:att-1:1", "process:att-1:finish:1"], _platon.QuizLog);
        Assert.Empty(await _t.Quizzes.ListPendingAsync());
        Assert.Equal(0, _sync.Get(SourceSystem.Platon).PendingActions);
        Assert.False((await _svc.ResumeAttemptAsync("att-1")).HasPendingSync);
        Assert.Equal(QuizAttemptState.Finished, (await _t.Quizzes.GetAttemptDataAsync("att-1"))!.Attempt.State);
    }

    [Fact]
    public async Task Sync_ProcessesAttemptsInCreationOrder()
    {
        var third = OfflineQuiz with { Id = "q3", CourseModuleId = "cm3", Name = "Kartkówka" };
        _platon.Quizzes.Add(third);
        await OpenOnlineAsync(OfflineQuiz);   // att-1
        await OpenOnlineAsync(third);         // att-2
        _net.SetOnline(false);

        await _svc.SaveAnswersAsync("att-2", [A("x", "1")]);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _svc.SaveAnswersAsync("att-1", [A("y", "1"), A("z", "2")]);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _svc.FinishAsync("att-2");
        _net.SetOnline(true);

        Assert.Equal(3, await _svc.SyncPendingAsync());
        Assert.Equal(["save:att-2:1", "save:att-1:2", "process:att-2:finish:1"], _platon.QuizLog);
    }

    [Fact]
    public async Task Sync_FailureBlocksFurtherActionsOfSameAttempt_AndIsRetriedLater()
    {
        await OpenOnlineAsync(OfflineQuiz);
        _net.SetOnline(false);
        await _svc.SaveAnswersAsync("att-1", [A("a", "1")]);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _svc.FinishAsync("att-1");
        _net.SetOnline(true);

        _platon.FailQuizWrite = op => op == "save";
        Assert.Equal(0, await _svc.SyncPendingAsync());
        Assert.Empty(_platon.QuizLog); // finish nie wyprzedził zapisu
        var pending = await _t.Quizzes.ListPendingAsync();
        Assert.Equal(2, pending.Count);
        Assert.Equal(1, pending[0].RetryCount);
        Assert.Equal("save failed", pending[0].LastError);
        Assert.Equal(0, pending[1].RetryCount);

        _platon.FailQuizWrite = null;
        Assert.Equal(2, await _svc.SyncPendingAsync());
        Assert.Equal(["save:att-1:1", "process:att-1:finish:1"], _platon.QuizLog);
    }

    [Fact]
    public async Task Sync_AuthFailure_StopsEverything_KeepsActions()
    {
        await OpenOnlineAsync(OfflineQuiz);
        _net.SetOnline(false);
        await _svc.SaveAnswersAsync("att-1", [A("a", "1")]);
        _net.SetOnline(true);
        _platon.Failure = new AdapterAuthException();

        Assert.Equal(0, await _svc.SyncPendingAsync());
        Assert.Single(await _t.Quizzes.ListPendingAsync());
    }

    [Fact]
    public async Task Sync_GivesUpOnActionAfterMaxRetries()
    {
        await OpenOnlineAsync(OfflineQuiz);
        _net.SetOnline(false);
        await _svc.SaveAnswersAsync("att-1", [A("a", "1")]);
        _net.SetOnline(true);
        _platon.FailQuizWrite = _ => true;

        for (var i = 0; i < QuizService.MaxRetries + 2; i++)
            await _svc.SyncPendingAsync();

        Assert.Equal(QuizService.MaxRetries, (await _t.Quizzes.ListPendingAsync()).Single().RetryCount);
        Assert.Equal(0, _sync.Get(SourceSystem.Platon).PendingActions);
        Assert.False((await _svc.ResumeAttemptAsync("att-1")).HasPendingSync);
    }

    [Fact]
    public async Task Finish_Online_ProcessesImmediately_AndDropsStalePending()
    {
        await OpenOnlineAsync(OfflineQuiz);
        _net.SetOnline(false);
        await _svc.SaveAnswersAsync("att-1", [A("a", "1")]);
        _net.SetOnline(true);

        var s = await _svc.FinishAsync("att-1");

        Assert.Equal(["process:att-1:finish:1"], _platon.QuizLog);
        Assert.False(s.HasPendingSync);
        Assert.Empty(await _t.Quizzes.ListPendingAsync());
        Assert.Equal(QuizAttemptState.Finished, s.Data.Attempt.State);
        // zakończenie unieważnia listę podejść w cache
        var attempts = await _svc.GetAttemptsAsync("q1");
        Assert.Equal(1, attempts.Count);
    }

    [Fact]
    public async Task Finish_Offline_ForQuizWithoutOfflineSupport_Throws()
    {
        await OpenOnlineAsync(OnlineOnlyQuiz);
        _net.SetOnline(false);
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => _svc.FinishAsync("att-1"));
        Assert.Empty(await _t.Quizzes.ListPendingAsync());
    }

    [Fact]
    public async Task Review_PassesThrough_AndListsAreCachedOffline()
    {
        await OpenOnlineAsync(OfflineQuiz);
        await _svc.GetQuizzesAsync();
        var review = await _svc.GetReviewAsync("att-1");
        Assert.Equal(5, review!.Grade);

        _net.SetOnline(false);
        Assert.Equal(5, (await _svc.GetReviewAsync("att-1"))!.Grade);
        Assert.Equal(2, (await _svc.GetQuizzesAsync()).Count);
        Assert.Single(await _svc.GetAttemptsAsync("q1"));
    }
}
