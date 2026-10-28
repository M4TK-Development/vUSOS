using Vusos.Core.Domain;
using Vusos.Core.Offline;
using Vusos.Core.Tests.Services;

namespace Vusos.Core.Tests.Offline;

public sealed class SqliteStoresTests : IDisposable
{
    private readonly TestDb _t = new();
    public void Dispose() => _t.Dispose();

    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 9, 30, 0, TimeSpan.FromHours(1));

    [Fact]
    public async Task Migrations_AreIdempotent_AndCreateSchemaVersion()
    {
        await _t.Cache.SetAsync("k", "{}", T0);
        // druga instancja na tym samym pliku ponownie uruchamia migracje
        using var second = SqliteDatabase.FromPath(_t.Db.DatabasePath);
        var version = await second.RunAsync(async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT MAX(version) FROM schema_version";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        });
        Assert.Equal(1, version);
        Assert.NotNull(await new SqliteCacheStore(second).GetAsync("k"));

        var mode = await second.RunAsync(async c =>
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode";
            return (string?)await cmd.ExecuteScalarAsync();
        });
        Assert.Equal("wal", mode);
    }

    [Fact]
    public async Task Cache_Roundtrip_PreservesJsonAndInstant()
    {
        Assert.Null(await _t.Cache.GetAsync("missing"));
        await _t.Cache.SetAsync("u:Usos:GetPayments:abc", "{\"x\":\"zażółć\"}", T0);
        var e = await _t.Cache.GetAsync("u:Usos:GetPayments:abc");
        Assert.NotNull(e);
        Assert.Equal("{\"x\":\"zażółć\"}", e.Json);
        Assert.Equal(T0, e.FetchedAt);

        await _t.Cache.SetAsync("u:Usos:GetPayments:abc", "[]", T0.AddMinutes(5));
        e = await _t.Cache.GetAsync("u:Usos:GetPayments:abc");
        Assert.Equal("[]", e!.Json);
        Assert.Equal(T0.AddMinutes(5), e.FetchedAt);
    }

    [Fact]
    public async Task Cache_RemoveByPrefix_RemovesOnlyMatching_IncludingWildcardChars()
    {
        await _t.Cache.SetAsync("a:Usos:m:1", "1", T0);
        await _t.Cache.SetAsync("a:Usos:m:2", "2", T0);
        await _t.Cache.SetAsync("a:Platon:m:1", "3", T0);
        await _t.Cache.SetAsync("a_Usos:x", "4", T0);
        await _t.Cache.SetAsync("abUsos:x", "5", T0);

        await _t.Cache.RemoveByPrefixAsync("a:Usos:");
        Assert.Null(await _t.Cache.GetAsync("a:Usos:m:1"));
        Assert.Null(await _t.Cache.GetAsync("a:Usos:m:2"));
        Assert.NotNull(await _t.Cache.GetAsync("a:Platon:m:1"));

        await _t.Cache.RemoveByPrefixAsync("a_"); // '_' nie jest wildcardem
        Assert.Null(await _t.Cache.GetAsync("a_Usos:x"));
        Assert.NotNull(await _t.Cache.GetAsync("abUsos:x"));

        await _t.Cache.ClearAsync();
        Assert.Null(await _t.Cache.GetAsync("a:Platon:m:1"));
    }

    [Fact]
    public async Task Cache_ConcurrentWrites_AreSafe()
    {
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => _t.Cache.SetAsync($"k{i % 5}", $"v{i}", T0.AddSeconds(i))));
        for (var i = 0; i < 5; i++)
            Assert.NotNull(await _t.Cache.GetAsync($"k{i}"));
    }

    [Fact]
    public async Task Materials_UpsertListGetRemove()
    {
        var r = new MaterialRecord("m1", "c1", "mod1", "a.pdf", "https://p/a.pdf", null, 100, "application/pdf", T0, DownloadState.Queued, null, T0);
        var r2 = r with { Id = "m2", CourseId = "c2", FileName = "b.pdf", RemoteUrl = "https://p/b.pdf", RemoteModified = null, Size = null, MimeType = null };
        await _t.Materials.UpsertAsync(r);
        await _t.Materials.UpsertAsync(r2);

        Assert.Equal(2, (await _t.Materials.ListAsync()).Count);
        Assert.Equal([r], await _t.Materials.ListAsync("c1"));
        Assert.Equal(r2, await _t.Materials.GetByUrlAsync("https://p/b.pdf"));
        Assert.Null(await _t.Materials.GetByUrlAsync("https://p/none"));

        var done = r with { State = DownloadState.Done, LocalPath = "/x/a.pdf", Size = 123, UpdatedAt = T0.AddMinutes(1) };
        await _t.Materials.UpsertAsync(done);
        Assert.Equal(done, (await _t.Materials.ListAsync("c1")).Single());

        await _t.Materials.RemoveAsync("m1");
        Assert.Empty(await _t.Materials.ListAsync("c1"));
    }

    [Fact]
    public async Task Quiz_AttemptDataAndLocalAnswers_Roundtrip()
    {
        var attempt = new QuizAttempt("a1", "q1", 1, QuizAttemptState.InProgress, T0, null, null, 0);
        var data = new QuizAttemptData(attempt,
            [new QuizQuestion(1, 0, "multichoice", "<div>Pytanie „ą”</div>", "1", "todo", null, null, 2.5, ["q9:1_answer", "q9:1:_sequencecheck"])],
            [new QuizAnswer("quizpassword", "tajne")]);
        await _t.Quizzes.SaveAttemptDataAsync(data);

        var loaded = await _t.Quizzes.GetAttemptDataAsync("a1");
        Assert.NotNull(loaded);
        Assert.Equal(attempt, loaded.Attempt);
        Assert.Equal(data.Questions[0].FieldNames, loaded.Questions[0].FieldNames);
        Assert.Equal("<div>Pytanie „ą”</div>", loaded.Questions[0].Html);
        Assert.Equal("tajne", loaded.PreflightData.Single().Value);
        Assert.Null(await _t.Quizzes.GetAttemptDataAsync("zz"));

        await _t.Quizzes.SaveLocalAnswersAsync("a1", [new QuizAnswer("q9:1_answer", "0"), new QuizAnswer("q9:2_answer", "x")]);
        await _t.Quizzes.SaveLocalAnswersAsync("a1", [new QuizAnswer("q9:1_answer", "2")]);
        var answers = await _t.Quizzes.GetLocalAnswersAsync("a1");
        Assert.Equal([new QuizAnswer("q9:1_answer", "2"), new QuizAnswer("q9:2_answer", "x")], answers);
        Assert.Empty(await _t.Quizzes.GetLocalAnswersAsync("other"));
    }

    [Fact]
    public async Task Quiz_PendingQueue_KeepsOrder_AndTracksFailures()
    {
        var a = new PendingQuizAction("p1", "a1", "q1", [new QuizAnswer("n", "v")], false, T0, 0, null);
        var b = new PendingQuizAction("p2", "a1", "q1", [new QuizAnswer("n", "v2")], true, T0, 0, null); // ten sam czas
        var c = new PendingQuizAction("p0", "a2", "q2", [], false, T0.AddMinutes(-1), 0, null);
        await _t.Quizzes.EnqueueAsync(a);
        await _t.Quizzes.EnqueueAsync(b);
        await _t.Quizzes.EnqueueAsync(c);

        var list = await _t.Quizzes.ListPendingAsync();
        Assert.Equal(["p0", "p1", "p2"], list.Select(x => x.Id));
        Assert.True(list[2].Finish);
        Assert.Equal("v2", list[2].Answers.Single().Value);

        await _t.Quizzes.MarkFailedAsync("p1", "boom");
        await _t.Quizzes.MarkFailedAsync("p1", "boom2");
        var failed = (await _t.Quizzes.ListPendingAsync()).Single(x => x.Id == "p1");
        Assert.Equal(2, failed.RetryCount);
        Assert.Equal("boom2", failed.LastError);

        await _t.Quizzes.CompleteAsync("p1");
        Assert.Equal(["p0", "p2"], (await _t.Quizzes.ListPendingAsync()).Select(x => x.Id));
    }

    [Fact]
    public async Task ClearAll_RemovesEveryTable_ButKeepsSchema()
    {
        await _t.Cache.SetAsync("k", "v", T0);
        await _t.Materials.UpsertAsync(new MaterialRecord("m", "c", "x", "f", "u", null, null, null, null, DownloadState.Queued, null, T0));
        await _t.Quizzes.EnqueueAsync(new PendingQuizAction("p", "a", "q", [], false, T0, 0, null));
        await _t.Quizzes.SaveLocalAnswersAsync("a", [new QuizAnswer("n", "v")]);
        await _t.Db.SetKvAsync("k", "v");

        await _t.Db.ClearAllAsync();

        Assert.Null(await _t.Cache.GetAsync("k"));
        Assert.Empty(await _t.Materials.ListAsync());
        Assert.Empty(await _t.Quizzes.ListPendingAsync());
        Assert.Empty(await _t.Quizzes.GetLocalAnswersAsync("a"));
        Assert.Null(await _t.Db.GetKvAsync("k"));

        await _t.Cache.SetAsync("k2", "v", T0); // nadal działa
        Assert.NotNull(await _t.Cache.GetAsync("k2"));
    }

    [Fact]
    public async Task Kv_SetGetOverwrite()
    {
        Assert.Null(await _t.Db.GetKvAsync("a"));
        await _t.Db.SetKvAsync("a", "1");
        await _t.Db.SetKvAsync("a", "2");
        Assert.Equal("2", await _t.Db.GetKvAsync("a"));
    }
}
