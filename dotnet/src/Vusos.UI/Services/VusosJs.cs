using Microsoft.JSInterop;
using Vusos.Core.Domain;

namespace Vusos.UI.Services;

/// <summary>Cienka otoczka na wwwroot/js/vusos.js. Każda metoda jest odporna na rozłączony obwód / brak JS.</summary>
public sealed class VusosJs(IJSRuntime js)
{
    private sealed record JsField(string Name, string Value);

    public async Task<bool> CopyTextAsync(string text)
    {
        try { return await js.InvokeAsync<bool>("vusos.copyText", text); }
        catch (Exception e) when (e is JSException or JSDisconnectedException or InvalidOperationException) { return false; }
    }

    public async Task<IReadOnlyList<QuizAnswer>> CollectQuizAsync(string containerId, int? page = null)
    {
        try
        {
            var fields = await js.InvokeAsync<JsField[]>("vusosQuiz.collect", containerId, page);
            return (fields ?? []).Select(f => new QuizAnswer(f.Name, f.Value ?? "")).ToList();
        }
        catch (Exception e) when (e is JSException or JSDisconnectedException or InvalidOperationException) { return []; }
    }

    /// <summary>Przywraca odpowiedzi; <paramref name="page"/> = numer strony z atrybutu data-page kontenera (JS czeka, aż DOM tej strony faktycznie się pojawi).</summary>
    public async Task RestoreQuizAsync(string containerId, IEnumerable<QuizAnswer> answers, int page)
    {
        try { await js.InvokeVoidAsync("vusosQuiz.restore", containerId, answers.Select(a => new { name = a.Name, value = a.Value }).ToArray(), page); }
        catch (Exception e) when (e is JSException or JSDisconnectedException or InvalidOperationException) { }
    }

    public async Task ScrollIntoViewAsync(string elementId, bool onlyOnNarrow = false)
    {
        try { await js.InvokeVoidAsync("vusos.scrollIntoView", elementId, onlyOnNarrow); }
        catch (Exception e) when (e is JSException or JSDisconnectedException or InvalidOperationException) { }
    }

    public async Task ScrollTopAsync()
    {
        try { await js.InvokeVoidAsync("vusos.scrollTop"); }
        catch (Exception e) when (e is JSException or JSDisconnectedException or InvalidOperationException) { }
    }

    public async Task LockScrollAsync(bool locked)
    {
        try { await js.InvokeVoidAsync("vusos.lockScroll", locked); }
        catch (Exception e) when (e is JSException or JSDisconnectedException or InvalidOperationException) { }
    }

    public async Task FocusAsync(string elementId)
    {
        try { await js.InvokeVoidAsync("vusos.focus", elementId); }
        catch (Exception e) when (e is JSException or JSDisconnectedException or InvalidOperationException) { }
    }
}
