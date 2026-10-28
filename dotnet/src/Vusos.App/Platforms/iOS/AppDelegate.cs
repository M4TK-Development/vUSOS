using Foundation;
using Microsoft.Identity.Client;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using UIKit;
using Vusos.App.Services;

namespace Vusos.App;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    // Deep linki vusos://… (USOS callback, token Platona) oraz powrót z logowania MSAL (msal{clientId}://auth).
    // TODO(compile-check): przy cyklu życia UIScene (SceneDelegate) URL nie trafia do AppDelegate.OpenUrl –
    // wtedy użyć ConfigureLifecycleEvents(...AddiOS(i => i.OpenUrl(...))) albo SceneDelegate.OpenUrlContexts.
    public override bool OpenUrl(UIApplication application, NSUrl url, NSDictionary options)
    {
        // Powrót z logowania MSAL (msal{clientId}://auth) – wartość zwracana przez helper pomijamy (różni się między wersjami).
        // TODO(compile-check): AuthenticationContinuationHelper.SetAuthenticationContinuationEventArgs(NSUrl) w zasobie iOS pakietu MSAL.
        if (url.Scheme?.StartsWith("msal", StringComparison.OrdinalIgnoreCase) == true)
        {
            AuthenticationContinuationHelper.SetAuthenticationContinuationEventArgs(url);
            return true;
        }

        DeepLinkRouter.Handle(url.AbsoluteString);
        return base.OpenUrl(application, url, options);
    }
}
