using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace Vusos.App.WinUI;

/// <summary>
/// Własny Main (w csproj: DISABLE_XAML_GENERATED_MAIN). Pilnuje jednej instancji aplikacji: gdy użytkownik kliknie
/// vusos://… przy działającej aplikacji, aktywacja jest przekierowywana do pierwszej instancji, a druga kończy pracę.
/// Wzorzec z dokumentacji Windows App SDK („Single-instancing with AppLifecycle API”).
/// </summary>
public static class Program
{
    private const string InstanceKey = "pl.m4tk.vusos.main";

    [global::System.STAThread]
    private static int Main(string[] args)
    {
        global::WinRT.ComWrappersSupport.InitializeComWrappers();

        if (DecideRedirection())
            return 0;

        global::Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }

    /// <returns>true, gdy aktywacja została przekierowana do działającej instancji (ten proces ma się zakończyć).</returns>
    private static bool DecideRedirection()
    {
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        var keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey);

        if (keyInstance.IsCurrent)
        {
            keyInstance.Activated += OnActivated;
            App.HandleActivation(args); // zimny start protokołem
            return false;
        }

        RedirectActivationTo(args, keyInstance);
        return true;
    }

    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        App.HandleActivation(args);
        App.BringToFront();
    }

    // Przekierowanie na wątku roboczym: wołanie .Wait() na wątku STA grozi zakleszczeniem.
    private static void RedirectActivationTo(AppActivationArguments args, AppInstance keyInstance)
    {
        using var done = new Semaphore(0, 1);
        Task.Run(() =>
        {
            try { keyInstance.RedirectActivationToAsync(args).AsTask().Wait(); }
            finally { done.Release(); }
        });
        done.WaitOne();
    }
}
