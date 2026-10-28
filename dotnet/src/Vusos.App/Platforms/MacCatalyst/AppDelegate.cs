using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using UIKit;
using Vusos.App.Services;

namespace Vusos.App;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    // Deep linki vusos://… (callback USOS, token Platona). Logowanie MSAL na Mac Catalyst idzie przez http://localhost
    // (pakiet MSAL nie ma zasobu maccatalyst – NuGet wybiera net6.0), więc AuthenticationContinuationHelper nie jest tu potrzebny.
    // TODO(compile-check): przy cyklu życia UIScene (SceneDelegate) URL nie trafia do AppDelegate.OpenUrl –
    // wtedy użyć ConfigureLifecycleEvents(...AddiOS(i => i.OpenUrl(...))) albo SceneDelegate.OpenUrlContexts.
    public override bool OpenUrl(UIApplication application, NSUrl url, NSDictionary options)
    {
        DeepLinkRouter.Handle(url.AbsoluteString);
        return base.OpenUrl(application, url, options);
    }
}
