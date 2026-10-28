using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using Vusos.Core.Platform;
using Vusos.Core.Hosting;
using Vusos.Core.Services;

namespace Vusos.WebHost.Hosting;

public static class WebHostEndpoints
{
    public static WebApplication MapVusosWebHost(this WebApplication app)
    {
        MapAuth(app);
        MapDownloads(app);
        MapConnect(app);
        return app;
    }

    private static void MapAuth(WebApplication app)
    {
        // GET /auth/login?returnUrl=… – Entra: wyzwanie OIDC; dev: formularz /dev-logowanie (albo Dev:AutoSignInEmail).
        app.MapGet("/auth/login", async (HttpContext http, IOptions<AuthOptions> authOptions, IConfiguration config, string? returnUrl) =>
        {
            var target = UserIdentity.SafeReturnUrl(returnUrl);
            if (http.User.Identity?.IsAuthenticated == true) return Results.LocalRedirect(target);

            if (authOptions.Value.IsEntra)
                return Results.Challenge(new AuthenticationProperties { RedirectUri = target }, [OpenIdConnectDefaults.AuthenticationScheme]);

            if (config["Dev:AutoSignInEmail"] is { Length: > 0 } email)
            {
                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AuthSetup.CreateDevPrincipal(email),
                    new AuthenticationProperties { IsPersistent = true });
                return Results.LocalRedirect(target);
            }
            return Results.LocalRedirect("/dev-logowanie?returnUrl=" + Uri.EscapeDataString(target));
        }).DisableAntiforgery();

        // POST /auth/dev-login (email, returnUrl) – tylko Auth:Mode=dev.
        app.MapPost("/auth/dev-login", async (HttpContext http, IOptions<AuthOptions> authOptions, IOptions<UniversityOptions> university) =>
        {
            if (!authOptions.Value.IsDev) return Results.NotFound();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var email = form["email"].ToString().Trim();
            var returnUrl = UserIdentity.SafeReturnUrl(form["returnUrl"].ToString());
            var domains = university.Value.AllowedEmailDomains;
            if (!UserIdentity.IsAllowedEmail(email, domains) || !System.Net.Mail.MailAddress.TryCreate(email, out _))
            {
                return Results.LocalRedirect("/dev-logowanie?returnUrl=" + Uri.EscapeDataString(returnUrl)
                    + "&error=" + Uri.EscapeDataString(UserIdentity.DomainsMessage(domains)));
            }
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AuthSetup.CreateDevPrincipal(email),
                new AuthenticationProperties { IsPersistent = true });
            return Results.LocalRedirect(returnUrl);
        }).DisableAntiforgery();

        // Wylogowanie z aplikacji (bez wylogowania z całego Microsoft 365).
        // anonymous: rozłącza USOS i Platon, kasuje katalog danych użytkownika i cookie sesji; Sec-Fetch-Site: cross-site
        // (np. obrazek / link z obcej strony) tylko wylogowuje, niczego nie kasuje.
        app.MapGet("/auth/logout", async (HttpContext http, IOptions<AuthOptions> authOptions, AnonymousSessions sessions) =>
        {
            if (authOptions.Value.IsAnonymous)
            {
                var sid = sessions.TryRead(http);
                var crossSite = string.Equals(http.Request.Headers["Sec-Fetch-Site"].ToString(), "cross-site", StringComparison.OrdinalIgnoreCase);
                if (sid is not null && !crossSite && http.RequestServices.GetService<UserCoreFactory>() is { } cores)
                    await cores.WipeAsync(AnonymousSessions.UserIdOf(sid));
                sessions.Delete(http);
                return Results.LocalRedirect("/logowanie");
            }
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/logowanie");
        }).DisableAntiforgery();
    }

    private static void MapDownloads(WebApplication app)
    {
        // Pobrane materiały: token wystawia JsExternalOpener i jest związany z użytkownikiem.
        app.MapGet("/dl/{token}", (string token, HttpContext http, DownloadTokens tokens) =>
        {
            var userId = UserIdentity.GetUserId(http.User);
            if (userId is null) return Results.Unauthorized();
            if (!tokens.TryRead(token, out var tokenUser, out var path)) return Results.NotFound();
            if (!string.Equals(tokenUser, UserIdentity.Hash(userId), StringComparison.Ordinal)) return Results.NotFound();

            var paths = http.RequestServices.GetRequiredService<IAppPaths>();
            var root = Path.GetFullPath(paths.MaterialsDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.Ordinal) || !File.Exists(full)) return Results.NotFound();

            new FileExtensionContentTypeProvider().TryGetContentType(full, out var contentType);
            return Results.File(full, contentType ?? "application/octet-stream", enableRangeProcessing: true);
        }).DisableAntiforgery();
    }

    private static void MapConnect(WebApplication app)
    {
        // Callback OAuth 1.0a USOS: ?oauth_token&oauth_verifier. Użytkownik jest rozpoznawany po cookie sesji.
        app.MapGet("/connect/usos/callback", async (HttpContext http, string? oauth_token, string? oauth_verifier) =>
        {
            var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Vusos.WebHost.Connect");
            var userId = UserIdentity.GetUserId(http.User);
            if (userId is null)
                return Results.LocalRedirect("/auth/login?returnUrl=" + Uri.EscapeDataString(http.Request.Path + http.Request.QueryString));

            var factory = http.RequestServices.GetService<UserCoreFactory>(); // brak w trybie Adapters:Mode=fake
            if (factory is null) return ConnectError("Połączenie z USOS jest niedostępne w trybie Adapters:Mode=fake.");
            if (string.IsNullOrEmpty(oauth_token) || string.IsNullOrEmpty(oauth_verifier))
                return ConnectError("Autoryzacja w USOS została anulowana albo nie zakończyła się powodzeniem.");
            try
            {
                var flow = factory.GetOrCreate(userId).Get<UsosConnectFlow>();
                await flow.CompleteAsync(oauth_token, oauth_verifier, http.RequestAborted);
                return Results.LocalRedirect("/ustawienia?connected=usos");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Callback USOS nie powiódł się");
                return ConnectError(e.Message);
            }
        }).DisableAntiforgery();
    }

    private static IResult ConnectError(string message)
        => Results.LocalRedirect("/ustawienia?error=" + Uri.EscapeDataString(message.Length <= 300 ? message : message[..300] + "…"));
}
