using System.Globalization;
using Vusos.Core.Adapters;
using Vusos.Core.Adapters.Platon;
using Vusos.Core.Domain;

namespace Vusos.Core.Tests.Platon;

public class PlatonMockAdapterTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T10:00:00Z", CultureInfo.InvariantCulture);

    private static PlatonMockAdapter Mock(FixedClock? clock = null) => new(clock ?? new FixedClock(Now));

    private static IReadOnlyList<LmsModule> AllModules(IEnumerable<LmsSection> sections) => sections.SelectMany(s => s.Modules).ToList();

    // ---------- schemat ----------

    [Fact]
    public async Task Courses_have_no_empty_required_strings_and_unique_ids()
    {
        var courses = await Mock().GetCoursesAsync();
        Assert.Equal(6, courses.Count);
        Assert.Contains(courses, c => c.ShortName == "INF-ALG-2025Z");
        Assert.Equal(courses.Count, courses.Select(c => c.Id).Distinct().Count());
        Assert.All(courses, c =>
        {
            Assert.False(string.IsNullOrEmpty(c.Id));
            Assert.False(string.IsNullOrEmpty(c.ShortName));
            Assert.False(string.IsNullOrEmpty(c.FullName));
            Assert.False(string.IsNullOrEmpty(c.Url));
            Assert.True(Uri.IsWellFormedUriString(c.Url, UriKind.Absolute));
            Assert.StartsWith("data:image/svg+xml", c.ImageUrl);
        });
    }

    [Fact]
    public async Task Contents_have_3_to_5_sections_stable_unique_module_ids_and_every_module_type()
    {
        var a = Mock();
        var ids = new HashSet<string>();
        var types = new HashSet<LmsModuleType>();
        foreach (var c in await a.GetCoursesAsync())
        {
            var sections = await a.GetCourseContentsAsync(c.Id);
            Assert.InRange(sections.Count, 3, 5);
            Assert.Equal("Wprowadzenie", sections[0].Name);
            Assert.Equal(sections.Count, sections.Select(s => s.Id).Distinct().Count());
            foreach (var s in sections)
            {
                Assert.False(string.IsNullOrEmpty(s.Id));
                Assert.False(string.IsNullOrEmpty(s.Name));
                foreach (var m in s.Modules)
                {
                    Assert.True(ids.Add(m.Id), $"duplicate module id {m.Id}");
                    Assert.Equal(c.Id, m.CourseId);
                    Assert.Equal(s.Id, m.SectionId);
                    Assert.False(string.IsNullOrEmpty(m.Name));
                    Assert.False(string.IsNullOrEmpty(m.RawType));
                    types.Add(m.Type);
                    Assert.All(m.Files, f =>
                    {
                        Assert.False(string.IsNullOrEmpty(f.Name));
                        Assert.False(string.IsNullOrEmpty(f.Url));
                    });
                }
            }
        }
        foreach (var t in new[] { LmsModuleType.Resource, LmsModuleType.Folder, LmsModuleType.Url, LmsModuleType.Assign, LmsModuleType.Quiz, LmsModuleType.Forum })
            Assert.Contains(t, types);
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => a.GetCourseContentsAsync("nope"));
    }

    [Fact]
    public async Task Assignments_are_sorted_unique_and_cover_due_soon_overdue_and_graded()
    {
        var list = await Mock().GetAssignmentsAsync();
        Assert.Equal(9, list.Count);
        Assert.Equal(list.Count, list.Select(a => a.Id).Distinct().Count());
        Assert.Equal(list.OrderBy(a => a.DueDate).Select(a => a.Id), list.Select(a => a.Id));
        Assert.All(list, a =>
        {
            Assert.False(string.IsNullOrEmpty(a.Id));
            Assert.False(string.IsNullOrEmpty(a.Name));
            Assert.False(string.IsNullOrEmpty(a.CourseName));
            Assert.NotNull(a.DueDate);
        });

        Assert.True(list.Count(a => !a.Submitted && a.DueDate > Now && a.DueDate < Now.AddDays(7)) >= 2);
        Assert.Single(list, a => !a.Submitted && a.DueDate < Now);
        var graded = Assert.Single(list, a => a.Submitted && a.Graded);
        Assert.Equal("9,00 / 10,00", graded.Grade);
    }

    [Fact]
    public async Task Module_assignments_and_quizzes_expose_instance_ids_matching_the_lists()
    {
        var a = Mock();
        var assignmentIds = (await a.GetAssignmentsAsync()).Select(x => x.Id).ToHashSet();
        var quizIds = (await a.GetQuizzesAsync()).Select(x => x.Id).ToHashSet();
        var modules = new List<LmsModule>();
        foreach (var c in await a.GetCoursesAsync()) modules.AddRange(AllModules(await a.GetCourseContentsAsync(c.Id)));
        Assert.Equal(assignmentIds, modules.Where(m => m.Type == LmsModuleType.Assign).Select(m => m.InstanceId!).ToHashSet());
        Assert.Equal(quizIds, modules.Where(m => m.Type == LmsModuleType.Quiz).Select(m => m.InstanceId!).ToHashSet());
        var quizCmids = (await a.GetQuizzesAsync()).Select(q => q.CourseModuleId).ToHashSet();
        Assert.Equal(quizCmids, modules.Where(m => m.Type == LmsModuleType.Quiz).Select(m => m.Id).ToHashSet());
    }

    [Fact]
    public async Task Events_fall_within_the_requested_window_and_are_sorted()
    {
        var a = Mock();
        var ev = await a.GetUpcomingEventsAsync(14);
        Assert.True(ev.Count >= 5);
        Assert.All(ev, e =>
        {
            Assert.InRange(e.Start, Now, Now.AddDays(14));
            Assert.False(string.IsNullOrEmpty(e.Name));
        });
        Assert.Equal(ev.OrderBy(e => e.Start).Select(e => e.Id), ev.Select(e => e.Id));
        Assert.Equal(ev.Count, ev.Select(e => e.Id).Distinct().Count());
        Assert.True((await a.GetUpcomingEventsAsync(3)).Count < ev.Count);
    }

    [Fact]
    public async Task Grades_exist_per_course_and_announcements_are_four_platon_items()
    {
        var a = Mock();
        foreach (var c in await a.GetCoursesAsync())
        {
            var items = await a.GetGradesAsync(c.Id);
            Assert.Contains(items, i => i.ItemType == "course");
            Assert.Equal(items.Count, items.Select(i => i.Id).Distinct().Count());
            Assert.All(items, i => Assert.False(string.IsNullOrEmpty(i.ItemName)));
        }
        Assert.Empty(await a.GetGradesAsync("unknown"));

        var ann = await a.GetAnnouncementsAsync();
        Assert.Equal(4, ann.Count);
        Assert.All(ann, x =>
        {
            Assert.Equal(SourceSystem.Platon, x.Source);
            Assert.False(string.IsNullOrEmpty(x.Title));
            Assert.False(string.IsNullOrEmpty(x.BodyHtml));
        });
        Assert.Equal(ann.Count, ann.Select(x => x.Id).Distinct().Count());
        Assert.Equal(ann.OrderByDescending(x => x.PublishedAt).Select(x => x.Id), ann.Select(x => x.Id));
        Assert.Equal(2, (await a.GetAnnouncementsAsync(2)).Count);
    }

    [Fact]
    public async Task Is_deterministic_and_returns_file_urls_unchanged()
    {
        var a = Mock();
        var b = Mock();
        Assert.Equal(await a.GetAssignmentsAsync(), await b.GetAssignmentsAsync());
        var contentsA = await a.GetCourseContentsAsync("3101");
        var contentsB = await b.GetCourseContentsAsync("3101");
        static string Fingerprint(IEnumerable<LmsSection> sections) =>
            string.Join("|", sections.Select(s => $"{s.Id}:{s.Name}:{string.Join(",", s.Modules.Select(m => m.Id))}"));
        Assert.Equal(Fingerprint(contentsA), Fingerprint(contentsB));
        var d = await a.GetFileDownloadAsync("https://x/y.pdf");
        Assert.Equal("https://x/y.pdf", d.Url.ToString());
        Assert.Empty(d.Headers);
        Assert.Equal(48213, (await a.GetSiteInfoAsync()).UserId);
    }

    [Fact]
    public async Task Dates_move_with_the_clock()
    {
        var later = new PlatonMockAdapter(new FixedClock(Now.AddDays(100)));
        var first = (await Mock().GetAssignmentsAsync())[0].DueDate!.Value;
        var shifted = (await later.GetAssignmentsAsync())[0].DueDate!.Value;
        Assert.Equal(TimeSpan.FromDays(100), shifted - first);
    }

    // ---------- quizy ----------

    private static async Task<(PlatonMockAdapter A, LmsQuiz Quiz)> OfflineQuiz(FixedClock? clock = null)
    {
        var a = Mock(clock);
        var quiz = Assert.Single(await a.GetQuizzesAsync(), q => q.AllowOfflineAttempts);
        return (a, quiz);
    }

    private static Dictionary<int, QuizQuestion> BySlot(QuizAttemptData d) => d.Questions.ToDictionary(q => q.Slot);

    [Fact]
    public async Task Two_quizzes_exist_one_allows_offline_and_filtering_by_course_works()
    {
        var a = Mock();
        var quizzes = await a.GetQuizzesAsync();
        Assert.Equal(2, quizzes.Count);
        Assert.Single(quizzes, q => q.AllowOfflineAttempts);
        Assert.All(quizzes, q =>
        {
            Assert.True(q.HasQuestions);
            Assert.NotNull(q.TimeClose);
            Assert.True(q.TimeClose > Now);
            Assert.False(string.IsNullOrEmpty(q.Name));
        });
        var one = await a.GetQuizzesAsync([quizzes[0].CourseId]);
        Assert.All(one, q => Assert.Equal(quizzes[0].CourseId, q.CourseId));
        Assert.Empty(await a.GetQuizzesAsync(["nope"]));
    }

    [Fact]
    public async Task Quiz_round_trip_start_save_finish_review()
    {
        var (a, quiz) = await OfflineQuiz();
        Assert.Empty(await a.GetQuizAttemptsAsync(quiz.Id));

        var attempt = await a.StartQuizAttemptAsync(quiz.Id);
        Assert.Equal(QuizAttemptState.InProgress, attempt.State);
        Assert.Equal(1, attempt.Attempt);
        Assert.Equal(quiz.Id, attempt.QuizId);
        Assert.Equal(Now, attempt.TimeStart);
        Assert.Null(attempt.TimeFinish);

        var data = await a.GetQuizAttemptDataAsync(attempt.Id, [new QuizAnswer("quizpassword", "x")]);
        Assert.Equal(6, data.Questions.Count);
        Assert.Equal([0, 1, 2], data.Questions.Select(q => q.Page).Distinct().Order());
        Assert.Equal([1, 2, 3, 4, 5, 6], data.Questions.Select(q => q.Slot));
        Assert.Equal(["quizpassword"], data.PreflightData.Select(p => p.Name));
        Assert.All(data.Questions, q =>
        {
            Assert.Equal("todo", q.State);
            Assert.Null(q.Mark);
            Assert.Equal("1", q.SequenceCheck);
            Assert.Contains($"q{attempt.Id}:{q.Slot}_answer", q.FieldNames);
            Assert.Contains($"q{attempt.Id}:{q.Slot}_:sequencecheck", q.FieldNames);
        });

        // HTML pytań jak z Moodle: radia, prawda/fałsz i pole tekstowe
        var q1 = BySlot(data)[1];
        Assert.Equal("multichoice", q1.Type);
        Assert.Contains($"<input type=\"radio\" name=\"q{attempt.Id}:1_answer\" value=\"1\"", q1.Html);
        Assert.Equal("truefalse", BySlot(data)[2].Type);
        var sa = BySlot(data)[4];
        Assert.Equal("shortanswer", sa.Type);
        Assert.Contains($"<input type=\"text\" name=\"q{attempt.Id}:4_answer\"", sa.Html);

        // częściowy zapis: wciąż w trakcie, odpowiedź widoczna w kolejnym pobraniu
        await a.SaveQuizAttemptAsync(attempt.Id, [new QuizAnswer($"q{attempt.Id}:1_answer", "1")]);
        var afterSave = await a.GetQuizAttemptDataAsync(attempt.Id);
        var saved = BySlot(afterSave)[1];
        Assert.Equal("complete", saved.State);
        Assert.Equal("2", saved.SequenceCheck);
        Assert.Contains("value=\"1\" id=\"q" + attempt.Id + ":1_answer1\" aria-labelledby=\"q" + attempt.Id + ":1_answer1_label\" checked=\"checked\"", saved.Html);
        Assert.Equal("todo", BySlot(afterSave)[2].State);

        // przed zakończeniem przegląd nie jest dostępny
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => a.GetQuizAttemptReviewAsync(attempt.Id));

        // zakończ z poprawnymi odpowiedziami na wszystko
        var state = await a.ProcessQuizAttemptAsync(attempt.Id,
        [
            new QuizAnswer($"q{attempt.Id}:1_answer", "1"),
            new QuizAnswer($"q{attempt.Id}:2_answer", "0"),
            new QuizAnswer($"q{attempt.Id}:3_answer", "1"),
            new QuizAnswer($"q{attempt.Id}:4_answer", "  Quicksort "),
            new QuizAnswer($"q{attempt.Id}:5_answer", "1"),
            new QuizAnswer($"q{attempt.Id}:6_answer", "10"),
            new QuizAnswer($"q{attempt.Id}:1_:sequencecheck", "2")
        ], finish: true);
        Assert.Equal(QuizAttemptState.Finished, state);

        var review = await a.GetQuizAttemptReviewAsync(attempt.Id);
        Assert.Equal(QuizAttemptState.Finished, review.Attempt.State);
        Assert.Equal(7.0, review.Attempt.SumGrades); // 1+1+1+2+1+1
        Assert.Equal(10.0, review.Grade);
        Assert.Equal(10.0, review.MaxGrade);
        Assert.Equal(6, review.Questions.Count);
        Assert.All(review.Questions, q =>
        {
            Assert.Equal("gradedright", q.State);
            Assert.Equal(q.MaxMark, q.Mark);
            Assert.Contains("disabled=\"disabled\"", q.Html);
        });

        var attempts = await a.GetQuizAttemptsAsync(quiz.Id);
        var listed = Assert.Single(attempts);
        Assert.Equal(QuizAttemptState.Finished, listed.State);
        Assert.NotNull(listed.TimeFinish);

        // po zakończeniu nie można ani zapisywać, ani pobierać danych do rozwiązywania
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => a.SaveQuizAttemptAsync(attempt.Id, [new QuizAnswer($"q{attempt.Id}:1_answer", "0")]));
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => a.GetQuizAttemptDataAsync(attempt.Id));
    }

    [Fact]
    public async Task Review_grade_is_computed_from_the_answers()
    {
        var (a, quiz) = await OfflineQuiz();
        var attempt = await a.StartQuizAttemptAsync(quiz.Id);
        // poprawne tylko pytania 1 (1 pkt) i 4 (2 pkt) = 3 z 7 punktów
        await a.ProcessQuizAttemptAsync(attempt.Id,
        [
            new QuizAnswer($"q{attempt.Id}:1_answer", "1"),
            new QuizAnswer($"q{attempt.Id}:2_answer", "1"),
            new QuizAnswer($"q{attempt.Id}:4_answer", "sortowanie szybkie"),
            new QuizAnswer($"q{attempt.Id}:6_answer", "11")
        ], finish: true);
        var review = await a.GetQuizAttemptReviewAsync(attempt.Id);
        Assert.Equal(3.0, review.Attempt.SumGrades);
        Assert.Equal(Math.Round(3.0 / 7.0 * 10, 2), review.Grade);
        var byslot = review.Questions.ToDictionary(q => q.Slot);
        Assert.Equal("gradedright", byslot[1].State);
        Assert.Equal("gradedwrong", byslot[2].State);
        Assert.Equal("gradedwrong", byslot[3].State); // bez odpowiedzi
        Assert.Equal(2.0, byslot[4].Mark);
        Assert.Equal(0.0, byslot[6].Mark);
        Assert.Contains("Poprawna odpowiedź: Fałsz", byslot[2].Html);
    }

    [Fact]
    public async Task Process_without_finish_keeps_the_attempt_open()
    {
        var (a, quiz) = await OfflineQuiz();
        var attempt = await a.StartQuizAttemptAsync(quiz.Id);
        var state = await a.ProcessQuizAttemptAsync(attempt.Id, [new QuizAnswer($"q{attempt.Id}:1_answer", "0")], finish: false);
        Assert.Equal(QuizAttemptState.InProgress, state);
        var listed = Assert.Single(await a.GetQuizAttemptsAsync(quiz.Id));
        Assert.Equal(QuizAttemptState.InProgress, listed.State);
        Assert.Equal(0, listed.CurrentPage);
    }

    [Fact]
    public async Task Clearing_an_answer_and_foreign_fields_are_handled()
    {
        var (a, quiz) = await OfflineQuiz();
        var attempt = await a.StartQuizAttemptAsync(quiz.Id);
        await a.SaveQuizAttemptAsync(attempt.Id,
        [
            new QuizAnswer($"q{attempt.Id}:1_answer", "1"),
            new QuizAnswer("q999999:2_answer", "1"),   // cudze podejście
            new QuizAnswer($"q{attempt.Id}:99_answer", "1") // nieistniejący slot
        ]);
        await a.SaveQuizAttemptAsync(attempt.Id, [new QuizAnswer($"q{attempt.Id}:1_answer", "-1")]);
        var data = await a.GetQuizAttemptDataAsync(attempt.Id);
        Assert.Equal("todo", BySlot(data)[1].State);
        Assert.Equal("todo", BySlot(data)[2].State);
    }

    [Fact]
    public async Task Starting_again_resumes_the_open_attempt_unless_forced_and_attempts_are_limited()
    {
        var (a, quiz) = await OfflineQuiz(); // 2 podejścia
        var first = await a.StartQuizAttemptAsync(quiz.Id);
        Assert.Equal(first.Id, (await a.StartQuizAttemptAsync(quiz.Id)).Id);

        var second = await a.StartQuizAttemptAsync(quiz.Id, forceNew: true);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, second.Attempt);
        var all = await a.GetQuizAttemptsAsync(quiz.Id);
        Assert.Equal(QuizAttemptState.Abandoned, all[0].State);

        await a.ProcessQuizAttemptAsync(second.Id, [], finish: true);
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => a.StartQuizAttemptAsync(quiz.Id));
    }

    [Fact]
    public async Task Attempt_ids_are_unique_across_quizzes()
    {
        var a = Mock();
        var quizzes = await a.GetQuizzesAsync();
        var ids = new List<string>();
        foreach (var q in quizzes) ids.Add((await a.StartQuizAttemptAsync(q.Id)).Id);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task Time_limit_turns_an_open_attempt_overdue_but_it_can_still_be_finished()
    {
        var clock = new FixedClock(Now);
        var (a, quiz) = await OfflineQuiz(clock);
        var attempt = await a.StartQuizAttemptAsync(quiz.Id);
        clock.Now = Now.AddSeconds(quiz.TimeLimitSeconds!.Value + 1);
        Assert.Equal(QuizAttemptState.Overdue, Assert.Single(await a.GetQuizAttemptsAsync(quiz.Id)).State);
        Assert.Equal(QuizAttemptState.Finished, await a.ProcessQuizAttemptAsync(attempt.Id, [], finish: true));
    }

    [Fact]
    public async Task Closed_quizzes_cannot_be_started_and_unknown_ids_fail()
    {
        var clock = new FixedClock(Now);
        var (a, quiz) = await OfflineQuiz(clock);
        clock.Now = quiz.TimeClose!.Value.AddMinutes(1);
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => a.StartQuizAttemptAsync(quiz.Id));
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => a.StartQuizAttemptAsync("nope"));
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => a.GetQuizAttemptsAsync("nope"));
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => a.GetQuizAttemptDataAsync("nope"));
        await Assert.ThrowsAsync<AdapterUnavailableException>(() => a.GetQuizAttemptReviewAsync("nope"));
    }

    [Fact]
    public async Task Concurrent_quiz_calls_do_not_corrupt_state()
    {
        var (a, quiz) = await OfflineQuiz();
        var attempt = await a.StartQuizAttemptAsync(quiz.Id);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            a.SaveQuizAttemptAsync(attempt.Id, [new QuizAnswer($"q{attempt.Id}:{1 + i % 3}_answer", (i % 2).ToString(CultureInfo.InvariantCulture))])));
        var data = await a.GetQuizAttemptDataAsync(attempt.Id);
        Assert.Equal(6, data.Questions.Count);
    }

    [Fact]
    public void Questions_html_field_names_are_extracted_consistently_with_the_functions_layer()
    {
        // Ten sam parser pól działa na HTML-u z mocka i z Moodle (FieldNames w obu ścieżkach).
        Assert.Equal(["q1:1_:sequencecheck", "q1:1_answer"],
            PlatonFunctions.ExtractFieldNames("<input type=\"hidden\" name=\"q1:1_:sequencecheck\" value=\"1\" /><input type=\"radio\" name=\"q1:1_answer\" value=\"0\" /><input type=\"radio\" name=\"q1:1_answer\" value=\"1\" />"));
    }
}
