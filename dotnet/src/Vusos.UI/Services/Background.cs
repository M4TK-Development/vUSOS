namespace Vusos.UI.Services;

public static class Background
{
    /// <summary>Uruchamia pracę „w tle” (np. kolejka pobierania) i pokazuje błąd jako toast zamiast gubić wyjątek.</summary>
    public static void Run(Func<Task> work, ToastService? toasts = null)
    {
        _ = Task.Run(async () =>
        {
            try { await work(); }
            catch (OperationCanceledException) { }
            catch (Exception e) { toasts?.Error(e.Message); }
        });
    }

    public static async Task<(T? Value, Exception? Error)> TryAsync<T>(Func<Task<T>> load)
    {
        try { return (await load(), null); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return (default, e); }
    }
}
