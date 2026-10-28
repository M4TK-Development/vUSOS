using Vusos.Core.Adapters;
using Vusos.Core.Adapters.Platon;
using Vusos.Core.Domain;

namespace Vusos.Core.Tests.Platon;

public class QuizFunctionsTests
{
    private const string Base = "https://platon.vistula.edu.pl/";

    private static FakeTransport NewTransport() => new FakeTransport()
        .OnFixture("mod_quiz_get_quizzes_by_courses", "quizzes_by_courses")
        .OnFixture("mod_quiz_get_user_attempts", "quiz_user_attempts")
        .OnFixture("mod_quiz_start_attempt", "quiz_start_attempt")
        .On("mod_quiz_get_attempt_data", args => Fx.Num(args["page"]) switch
        {
            0 => Fx.Fixture("quiz_attempt_data_p0"),
            1 => Fx.Fixture("quiz_attempt_data_p1"),
            _ => Fx.Fixture("quiz_attempt_data_p2")
        })
        .On("mod_quiz_save_attempt", """{"status":true,"warnings":[]}""")
        .On("mod_quiz_process_attempt", """{"state":"finished","warnings":[]}""")
        .OnFixture("mod_quiz_get_attempt_review", "quiz_attempt_review");

    private static (PlatonFunctions A, FakeTransport T) Setup(Action<FakeTransport>? customize = null)
    {
        var t = NewTransport();
        customize?.Invoke(t);
        return (new PlatonFunctions(t, new PlatonFunctionsOptions { BaseUrl = Base, Clock = new FixedClock(DateTimeOffset.Parse("2026-10-28T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture)) }), t);
    }

    private static Dictionary<string, string> Flat(RecordedCall c) =>
        MoodleArgsEncoder.Flatten(c.Args).ToDictionary(kv => kv.Key, kv => kv.Value);

    [Fact]
    public async Task GetQuizzes_maps_fields()
    {
        var (a, t) = Setup();
        var quizzes = await a.GetQuizzesAsync(["3017", "3022"]);
        Assert.Equal(["3017", "3022"], Flat(t.Calls[0]).Where(kv => kv.Key.StartsWith("courseids[")).Select(kv => kv.Value));

        Assert.Equal(2, quizzes.Count);
        var q = quizzes[0];
        Assert.Equal("9001", q.Id);
        Assert.Equal("410013", q.CourseModuleId);
        Assert.Equal("3017", q.CourseId);
        Assert.Equal("<p>Sześć pytań. x.png</p>", q.IntroHtml);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792000000), q.TimeOpen);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793275200), q.TimeClose);
        Assert.Equal(1200, q.TimeLimitSeconds);
        Assert.Equal(2, q.Attempts);
        Assert.True(q.AllowOfflineAttempts);
        Assert.True(q.HasQuestions);
        Assert.Equal($"{Base}mod/quiz/view.php?id=410013", q.Url);

        var open = quizzes[1];
        Assert.Null(open.TimeOpen);
        Assert.Null(open.TimeClose);
        Assert.Null(open.TimeLimitSeconds);
        Assert.Null(open.IntroHtml);
        Assert.Equal(0, open.Attempts);
        Assert.False(open.AllowOfflineAttempts);
        Assert.False(open.HasQuestions);
    }

    [Fact]
    public async Task GetQuizzes_without_course_ids_sends_no_courseids()
    {
        var (a, t) = Setup();
        await a.GetQuizzesAsync();
        Assert.Empty(t.Calls[0].Args);
    }

    [Fact]
    public async Task GetQuizAttempts_requests_all_statuses_without_previews_and_maps_states()
    {
        var (a, t) = Setup();
        var attempts = await a.GetQuizAttemptsAsync("9001");
        var flat = Flat(t.Calls[0]);
        Assert.Equal("9001", flat["quizid"]);
        Assert.Equal("all", flat["status"]);
        Assert.Equal("0", flat["includepreviews"]);

        Assert.Equal(2, attempts.Count);
        Assert.Equal(new QuizAttempt("50001", "9001", 1, QuizAttemptState.Finished,
            DateTimeOffset.FromUnixTimeSeconds(1793000000), DateTimeOffset.FromUnixTimeSeconds(1793000900), 5.5, 0), attempts[0]);
        Assert.Equal(QuizAttemptState.InProgress, attempts[1].State);
        Assert.Null(attempts[1].TimeFinish);
        Assert.Null(attempts[1].SumGrades);
        Assert.Equal(2, attempts[1].CurrentPage);
    }

    [Theory]
    [InlineData("inprogress", QuizAttemptState.InProgress)]
    [InlineData("overdue", QuizAttemptState.Overdue)]
    [InlineData("finished", QuizAttemptState.Finished)]
    [InlineData("abandoned", QuizAttemptState.Abandoned)]
    public void ParseAttemptState_maps_moodle_states(string raw, QuizAttemptState expected) =>
        Assert.Equal(expected, PlatonFunctions.ParseAttemptState(raw));

    [Fact]
    public void ParseAttemptState_rejects_unknown_states() =>
        Assert.Throws<AdapterUnavailableException>(() => PlatonFunctions.ParseAttemptState("weird"));

    [Fact]
    public async Task StartQuizAttempt_sends_preflight_and_forcenew()
    {
        var (a, t) = Setup();
        var attempt = await a.StartQuizAttemptAsync("9001", [new QuizAnswer("quizpassword", "sekret")], forceNew: true);
        var flat = Flat(t.Calls[0]);
        Assert.Equal("9001", flat["quizid"]);
        Assert.Equal("quizpassword", flat["preflightdata[0][name]"]);
        Assert.Equal("sekret", flat["preflightdata[0][value]"]);
        Assert.Equal("1", flat["forcenew"]);
        Assert.Equal("50017", attempt.Id);
        Assert.Equal(QuizAttemptState.InProgress, attempt.State);
    }

    [Fact]
    public async Task StartQuizAttempt_omits_empty_preflight()
    {
        var (a, t) = Setup();
        await a.StartQuizAttemptAsync("9001");
        var flat = Flat(t.Calls[0]);
        Assert.DoesNotContain(flat.Keys, k => k.StartsWith("preflightdata"));
        Assert.Equal("0", flat["forcenew"]);
    }

    [Fact]
    public async Task GetQuizAttemptData_iterates_pages_until_nextpage_is_minus_one()
    {
        var (a, t) = Setup();
        var preflight = new[] { new QuizAnswer("quizpassword", "sekret") };
        var data = await a.GetQuizAttemptDataAsync("50017", preflight);

        Assert.Equal([0L, 1L, 2L], t.CallsTo("mod_quiz_get_attempt_data").Select(c => Fx.Num(c.Args["page"])));
        Assert.All(t.CallsTo("mod_quiz_get_attempt_data"), c => Assert.Equal("quizpassword", Flat(c)["preflightdata[0][name]"]));
        Assert.Equal("50017", data.Attempt.Id);
        Assert.Equal(preflight, data.PreflightData);
        Assert.Equal([1, 2, 3, 4], data.Questions.Select(q => q.Slot));
        Assert.Equal([0, 0, 1, 2], data.Questions.Select(q => q.Page));
        Assert.Equal(["multichoice", "truefalse", "shortanswer", "multichoice"], data.Questions.Select(q => q.Type));

        var q1 = data.Questions[0];
        Assert.Equal("1", q1.SequenceCheck);
        Assert.Equal("todo", q1.State);
        Assert.Equal("Not yet answered", q1.Status);
        Assert.Equal(1.0, q1.MaxMark);
        Assert.Null(q1.Mark);
        Assert.Equal(["q50017:1_:sequencecheck", "q50017:1_answer"], q1.FieldNames);
        Assert.Equal(["q50017:3_:sequencecheck", "q50017:3_answer"], data.Questions[2].FieldNames);
        Assert.Equal("complete", data.Questions[2].State);
        Assert.Contains("Binary search", q1.Html);
    }

    [Fact]
    public async Task GetQuizAttemptData_does_not_loop_forever_on_a_bogus_nextpage()
    {
        var (a, t) = Setup(tr => tr.On("mod_quiz_get_attempt_data", _ => Fx.Fixture("quiz_attempt_data_p0"))); // nextpage zawsze 1
        var data = await a.GetQuizAttemptDataAsync("50017");
        Assert.Equal(2, t.CallsTo("mod_quiz_get_attempt_data").Count);
        Assert.NotEmpty(data.Questions);
    }

    [Fact]
    public void ExtractFieldNames_finds_inputs_selects_and_textareas_once_each()
    {
        const string html = """
            <input type="hidden" name="q1:1_:sequencecheck" value="1" />
            <input type="radio" name="q1:1_answer" value="0" id="a0" /><input type="radio" name="q1:1_answer" value="1" id="a1" />
            <select class="x" name='q1:2_choice0'><option value="1">a</option></select>
            <textarea id="t" name="q1:3_answer" rows="5"></textarea>
            <input type="submit" name="next" value="Next" /><input type="button" name="clear" /><input type="file" name="f" />
            <input name="q1:4_x&amp;y" type="text" />
            """;
        Assert.Equal(["q1:1_:sequencecheck", "q1:1_answer", "q1:2_choice0", "q1:3_answer", "q1:4_x&y"], PlatonFunctions.ExtractFieldNames(html));
        Assert.Empty(PlatonFunctions.ExtractFieldNames(null));
        Assert.Empty(PlatonFunctions.ExtractFieldNames("<p>nic</p>"));
    }

    [Fact]
    public async Task GetQuizAttemptData_skips_non_value_inputs()
    {
        var (a, _) = Setup();
        var data = await a.GetQuizAttemptDataAsync("50017");
        Assert.Equal(["q50017:4_answer", "q50017:4_extra", "q50017:4_notes"], data.Questions[3].FieldNames.Where(n => !n.Contains("sequencecheck")));
    }

    [Fact]
    public async Task SaveQuizAttempt_sends_name_value_pairs()
    {
        var (a, t) = Setup();
        await a.SaveQuizAttemptAsync("50017", [new QuizAnswer("q50017:1_answer", "1"), new QuizAnswer("q50017:3_answer", "quick sort")],
            [new QuizAnswer("quizpassword", "p")]);
        var flat = Flat(Assert.Single(t.CallsTo("mod_quiz_save_attempt")));
        Assert.Equal("50017", flat["attemptid"]);
        Assert.Equal("q50017:1_answer", flat["data[0][name]"]);
        Assert.Equal("quick sort", flat["data[1][value]"]);
        Assert.Equal("quizpassword", flat["preflightdata[0][name]"]);
    }

    [Fact]
    public async Task SaveQuizAttempt_skips_the_call_for_no_answers_and_fails_on_rejected_status()
    {
        var (a, t) = Setup();
        await a.SaveQuizAttemptAsync("50017", []);
        Assert.Empty(t.CallsTo("mod_quiz_save_attempt"));

        var (b, _) = Setup(tr => tr.On("mod_quiz_save_attempt", """{"status":false,"warnings":[]}"""));
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => b.SaveQuizAttemptAsync("50017", [new QuizAnswer("q50017:1_answer", "1")]));
    }

    [Theory]
    [InlineData(true, "finished", QuizAttemptState.Finished)]
    [InlineData(false, "inprogress", QuizAttemptState.InProgress)]
    [InlineData(true, "overdue", QuizAttemptState.Overdue)]
    public async Task ProcessQuizAttempt_maps_the_returned_state(bool finish, string raw, QuizAttemptState expected)
    {
        var (a, t) = Setup(tr => tr.On("mod_quiz_process_attempt", $$"""{"state":"{{raw}}","warnings":[]}"""));
        var state = await a.ProcessQuizAttemptAsync("50017", [new QuizAnswer("q50017:1_answer", "1")], finish);
        Assert.Equal(expected, state);
        var flat = Flat(t.Calls[^1]);
        Assert.Equal(finish ? "1" : "0", flat["finishattempt"]);
        Assert.Equal("0", flat["timeup"]);
        Assert.Equal("q50017:1_answer", flat["data[0][name]"]);
    }

    [Fact]
    public async Task GetQuizAttemptReview_reads_all_pages_and_resolves_the_max_grade_from_the_quiz()
    {
        var (a, t) = Setup();
        var review = await a.GetQuizAttemptReviewAsync("50017");
        var flat = Flat(t.CallsTo("mod_quiz_get_attempt_review")[0]);
        Assert.Equal("-1", flat["page"]);
        Assert.Equal(7.86, review.Grade);
        Assert.Equal(10.0, review.MaxGrade);
        Assert.Equal(QuizAttemptState.Finished, review.Attempt.State);
        Assert.Equal(2, review.Questions.Count);
        Assert.Equal("gradedright", review.Questions[0].State);
        Assert.Equal(1.0, review.Questions[0].Mark);
        Assert.Equal(0.0, review.Questions[1].Mark);
    }

    [Fact]
    public async Task GetQuizAttemptReview_survives_a_failing_quiz_lookup()
    {
        var (a, _) = Setup(t => t.On("mod_quiz_get_quizzes_by_courses", _ => throw new AdapterAuthException("accessexception")));
        var review = await a.GetQuizAttemptReviewAsync("50017");
        Assert.Equal(7.86, review.Grade);
        Assert.Null(review.MaxGrade);
    }

    [Fact]
    public async Task Quiz_calls_reject_non_numeric_ids()
    {
        var (a, _) = Setup();
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => a.GetQuizAttemptsAsync("x"));
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => a.StartQuizAttemptAsync("x"));
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => a.GetQuizzesAsync(["x"]));
    }
}
