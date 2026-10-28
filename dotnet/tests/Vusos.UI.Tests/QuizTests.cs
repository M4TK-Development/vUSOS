using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Vusos.Core.Domain;
using Vusos.Core.Services;
using Vusos.UI.Pages;

namespace Vusos.UI.Tests;

public class QuizTests : UiTestBase
{
    private async Task<string> StartAttemptAsync(string quizId = "9001")
    {
        var quizzes = Services.GetRequiredService<IQuizService>();
        var quiz = (await quizzes.GetQuizzesAsync()).First(q => q.Id == quizId);
        var session = await quizzes.OpenAttemptAsync(quiz);
        return session.Data.Attempt.Id;
    }

    [Fact]
    public void Quiz_list_shows_offline_badge_dates_and_start_button()
    {
        var cut = Render<Quizzes>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("article.quiz-card").Count), Timeout);
        Assert.Contains("offline możliwy", cut.Markup);
        Assert.Contains("Zamknięcie", cut.Markup);
        Assert.Contains("Rozpocznij", cut.Markup);
        Assert.Contains("Przejrzyj", cut.Markup); // zakończone podejście w quizie próbnym
    }

    [Fact]
    public void Starting_a_quiz_navigates_to_the_runner_and_continue_appears()
    {
        var cut = Render<Quizzes>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("article.quiz-card")), Timeout);
        cut.FindAll("button").First(b => b.TextContent.Contains("Rozpocznij")).Click();
        cut.FindAll(".dialog button").First(b => b.TextContent.Trim() == "Rozpocznij").Click();
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        cut.WaitForAssertion(() => Assert.Contains("/testy/podejscie/att-", nav.Uri), Timeout);

        var again = Render<Quizzes>();
        again.WaitForAssertion(() => Assert.Contains("Kontynuuj", again.Markup), Timeout);
    }

    [Fact]
    public void Runner_renders_questions_page_by_page_and_finishes()
    {
        var id = StartAttemptAsync().GetAwaiter().GetResult();
        var cut = Render<QuizRunner>(p => p.Add(x => x.AttemptId, id));
        cut.WaitForAssertion(() => Assert.Contains("Pytanie 1", cut.Markup), Timeout);
        Assert.Contains("Strona 1 z 3", cut.Markup);
        Assert.Contains("name=\"q42:1_answer\"", cut.Markup);   // formularz Moodle po sanityzacji
        Assert.DoesNotContain("Pytanie 3", cut.Markup);
        Assert.NotEmpty(cut.FindAll("[role=timer]"));            // limit 30 min

        cut.FindAll("button").First(b => b.TextContent.Contains("Następna")).Click();
        cut.WaitForAssertion(() => Assert.Contains("Pytanie 3", cut.Markup), Timeout);
        Assert.NotEmpty(JSInterop.Invocations["vusosQuiz.collect"]);  // odpowiedzi zebrane przy zmianie strony
        Assert.NotEmpty(JSInterop.Invocations["vusosQuiz.restore"]);

        cut.FindAll("button.dot-btn").Last().Click();
        cut.WaitForAssertion(() => Assert.Contains("Pytanie 5", cut.Markup), Timeout);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Zakończ").Click();
        cut.FindAll(".dialog button").First(b => b.TextContent.Contains("Zakończ test")).Click();
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        cut.WaitForAssertion(() => Assert.EndsWith("/przeglad", nav.Uri), Timeout);
    }

    [Fact]
    public void Runner_saves_answers_through_the_quiz_service()
    {
        var id = StartAttemptAsync().GetAwaiter().GetResult();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Render<QuizRunner>(p => p.Add(x => x.AttemptId, id));
        cut.WaitForAssertion(() => Assert.Contains("Pytanie 1", cut.Markup), Timeout);
        cut.FindAll("button.dot-btn")[1].Click();
        cut.WaitForAssertion(() => Assert.Contains("Zapisano", cut.Markup), Timeout);
    }

    [Fact]
    public void Offline_runner_shows_offline_state_and_queues_finish()
    {
        var id = StartAttemptAsync().GetAwaiter().GetResult();
        Connectivity.SetOnline(false);
        var cut = Render<QuizRunner>(p => p.Add(x => x.AttemptId, id));
        cut.WaitForAssertion(() => Assert.Contains("Tryb offline", cut.Markup), Timeout);

        cut.FindAll("button.dot-btn").Last().Click();
        cut.WaitForAssertion(() => Assert.Contains("Pytanie 5", cut.Markup), Timeout);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Zakończ").Click();
        cut.FindAll(".dialog button").First(b => b.TextContent.Contains("Zakończ test")).Click();
        cut.WaitForAssertion(() => Assert.Contains("czeka na synchronizację", cut.Markup), Timeout);

        var sync = Services.GetRequiredService<ISyncStatus>();
        Assert.True(sync.Get(SourceSystem.Platon).PendingActions > 0);
        Connectivity.SetOnline(true);
        Services.GetRequiredService<IQuizService>().SyncPendingAsync().GetAwaiter().GetResult();
        Assert.Equal(0, sync.Get(SourceSystem.Platon).PendingActions);
    }

    [Fact]
    public void Review_page_shows_grade_and_disabled_controls()
    {
        var cut = Render<QuizReview>(p => p.Add(x => x.AttemptId, "att-9003-1"));
        cut.WaitForAssertion(() => Assert.Contains("Pytanie 1", cut.Markup), Timeout);
        Assert.Contains("Wynik", cut.Markup);
        Assert.All(cut.FindAll(".question-html input[type=radio]"), i => Assert.NotNull(i.GetAttribute("disabled")));
    }

    [Fact]
    public void Finished_attempt_redirects_to_review()
    {
        var cut = Render<QuizRunner>(p => p.Add(x => x.AttemptId, "att-9003-1"));
        var nav = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        cut.WaitForAssertion(() => Assert.EndsWith("/att-9003-1/przeglad", nav.Uri), Timeout);
    }
}
