using Vusos.Core.Domain;
using Vusos.Core.Services;

namespace Vusos.Core.Tests.Services;

public class BackgroundSyncCoordinatorTests
{
    private readonly FakeConnectivity _net = new();
    private readonly CountingQuiz _quiz = new();
    private readonly CountingMaterials _materials = new();

    [Fact]
    public async Task OnlineChanged_TriggersQuizSyncThenMaterialQueue()
    {
        using var c = new BackgroundSyncCoordinator(_net, _quiz, _materials);
        _net.SetOnline(false);
        c.Start();
        Assert.True(c.IsStarted);
        _net.SetOnline(false);
        _net.SetOnline(true);
        await c.Running;

        Assert.Equal(1, _quiz.Syncs);
        Assert.Equal(1, _materials.Runs);
    }

    [Fact]
    public async Task Start_WhenOnline_RunsImmediately_AndStopUnsubscribes()
    {
        var c = new BackgroundSyncCoordinator(_net, _quiz, _materials);
        c.Start();
        c.Start(); // idempotentne
        await c.Running;
        Assert.Equal(1, _quiz.Syncs);

        c.Stop();
        Assert.False(c.IsStarted);
        _net.SetOnline(true);
        await c.Running;
        Assert.Equal(1, _quiz.Syncs);
    }

    [Fact]
    public async Task Errors_AreSwallowed_AndMaterialsStillRun()
    {
        _quiz.Throw = true;
        using var c = new BackgroundSyncCoordinator(_net, _quiz, _materials);
        c.Start();
        await c.Running;
        Assert.Equal(1, _materials.Runs);
    }

    private sealed class CountingQuiz : IQuizService
    {
        public int Syncs;
        public bool Throw;
        public Task<int> SyncPendingAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Syncs);
            return Throw ? throw new InvalidOperationException("boom") : Task.FromResult(0);
        }
        public Task<IReadOnlyList<LmsQuiz>> GetQuizzesAsync(string? courseId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<QuizAttempt>> GetAttemptsAsync(string quizId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<QuizSession> OpenAttemptAsync(LmsQuiz quiz, IReadOnlyList<QuizAnswer>? preflight = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<QuizSession> ResumeAttemptAsync(string attemptId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<QuizSession> SaveAnswersAsync(string attemptId, IReadOnlyList<QuizAnswer> answers, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<QuizSession> FinishAsync(string attemptId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<QuizAttemptReview?> GetReviewAsync(string attemptId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class CountingMaterials : IMaterialService
    {
        public int Runs;
        public Task ProcessQueueAsync(CancellationToken ct = default) { Interlocked.Increment(ref Runs); return Task.CompletedTask; }
        public IObservable<DownloadProgress> Progress => throw new NotSupportedException();
        public Task<IReadOnlyList<Offline.MaterialRecord>> ListAsync(string? courseId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Offline.MaterialRecord?> FindAsync(string remoteUrl, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Offline.MaterialRecord> EnqueueAsync(string courseId, LmsModule module, LmsFile file, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> EnqueueCourseAsync(string courseId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(string materialId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> GetTotalSizeAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
}
