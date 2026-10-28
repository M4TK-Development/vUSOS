using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using Microsoft.Windows.AppLifecycle;
using Vusos.App.Services;
using Windows.ApplicationModel.Activation;

namespace Vusos.App.WinUI;

/// <summary>Aplikacja WinUI (Windows). Punkt wejścia z przekierowaniem jednej instancji: <see cref="Program"/>.</summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        this.InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    /// <summary>
    /// Aktywacja protokołem vusos://… (zimny start albo przekierowana z drugiej instancji przez <see cref="Program"/>).
    /// Wołane z dowolnego wątku; <see cref="DeepLinkRouter"/> kolejkuje link, jeśli aplikacja jeszcze się uruchamia.
    /// </summary>
    internal static void HandleActivation(AppActivationArguments args)
    {
        // TODO(compile-check): ExtendedActivationKind.Protocol + args.Data jako IProtocolActivatedEventArgs (WindowsAppSDK AppLifecycle).
        if (args.Kind == ExtendedActivationKind.Protocol &&
            args.Data is IProtocolActivatedEventArgs protocol)
        {
            DeepLinkRouter.Handle(protocol.Uri?.OriginalString);
        }
    }

    /// <summary>Przy przekierowanej aktywacji wyciąga okno na wierzch.</summary>
    internal static void BringToFront()
    {
        // TODO(compile-check): dostęp do natywnego okna WinUI z okna MAUI.
        Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
        {
            var window = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
            (window?.Handler?.PlatformView as Microsoft.UI.Xaml.Window)?.Activate();
        });
    }
}
