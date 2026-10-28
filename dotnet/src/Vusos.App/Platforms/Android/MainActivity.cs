using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Microsoft.Identity.Client;
using Microsoft.Maui;
using Vusos.App.Services;

namespace Vusos.App;

// SingleTask: kliknięcie vusos://… w przeglądarce wraca do istniejącej aktywności (OnNewIntent), a nie tworzy nowej.
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density |
                           ConfigChanges.KeyboardHidden | ConfigChanges.Keyboard)]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "vusos")]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        // Zimny start z deep linku. Przy odtwarzaniu po zabiciu procesu (savedInstanceState != null) nie powtarzamy linku.
        if (savedInstanceState is null)
            Forward(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Forward(intent);
    }

    // MSAL: wynik logowania w przeglądarce (Custom Tabs) wraca przez BrowserTabActivity → ta aktywność.
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        // TODO(compile-check): sygnatura (int, Result, Intent) w Microsoft.Identity.Client.AuthenticationContinuationHelper.
        AuthenticationContinuationHelper.SetAuthenticationContinuationEventArgs(requestCode, resultCode, data);
    }

    private static void Forward(Intent? intent)
    {
        if (intent?.Action == Intent.ActionView && intent.Data is { } data)
            DeepLinkRouter.Handle(data.ToString());
    }
}
