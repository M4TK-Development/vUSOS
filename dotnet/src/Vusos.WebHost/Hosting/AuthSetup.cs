using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Vusos.Core.Services;

namespace Vusos.WebHost.Hosting;

/// <summary>
/// Uwierzytelnianie WebHost: domyślnie anonimowe cookie sesji (Auth:Mode=anonymous, tożsamość z połączeń z USOS/Platonem),
/// opcjonalnie cookie + OpenID Connect (Auth:Mode=entra) albo logowanie e-mailem (Auth:Mode=dev).
/// </summary>
public static partial class AuthSetup
{
    public const string CookieName = "vusos.auth";

    [GeneratedRegex(@"^https://login\.microsoftonline\.com/[0-9a-fA-F-]{36}/v2\.0/?$")]
    private static partial Regex EntraIssuer();

    public static AuthOptions ReadOptions(IConfiguration configuration)
    {
        var auth = new AuthOptions();
        configuration.GetSection("Auth").Bind(auth);
        return auth;
    }

    public static IServiceCollection AddVusosAuth(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var auth = ReadOptions(configuration);
        if (!auth.IsAnonymous && !auth.IsDev && !auth.IsEntra)
            throw new InvalidOperationException($"Nieznany Auth:Mode '{auth.Mode}' (dozwolone: anonymous, dev, entra).");
        if (auth.IsDev && !(environment.IsDevelopment() || environment.EnvironmentName == "Testing") && !auth.AllowDevInProduction)
            throw new InvalidOperationException(
                "Auth:Mode=dev pozwala zalogować się dowolnym adresem e-mail, więc jest dostępny tylko w środowisku Development. " +
                "Ustaw Auth:Mode=entra albo (świadomie) Auth:AllowDevInProduction=true.");

        services.AddOptions<AuthOptions>().Bind(configuration.GetSection("Auth"));
        services.AddHttpContextAccessor();
        services.AddAuthorization();
        services.AddSingleton<AnonymousSessions>();

        var builder = services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, o =>
            {
                o.Cookie.Name = CookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(12);
                o.SlidingExpiration = true;
                o.LoginPath = "/logowanie";
                o.LogoutPath = "/auth/logout";
                o.AccessDeniedPath = "/logowanie";
            });

        if (auth.IsEntra)
        {
            if (string.IsNullOrWhiteSpace(auth.ClientId) || string.IsNullOrWhiteSpace(auth.ClientSecret))
                throw new InvalidOperationException("Auth:Mode=entra wymaga Auth:ClientId i Auth:ClientSecret.");

            builder.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, o =>
            {
                var uni = configuration.GetSection("University").Get<UniversityOptions>() ?? new UniversityOptions();
                o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.Authority = string.IsNullOrWhiteSpace(auth.Authority) ? uni.EntraAuthority : auth.Authority;
                o.ClientId = auth.ClientId;
                o.ClientSecret = auth.ClientSecret;
                o.ResponseType = OpenIdConnectResponseType.Code;
                o.UsePkce = true;
                o.SaveTokens = false;
                o.GetClaimsFromUserInfoEndpoint = false;
                o.MapInboundClaims = false;
                o.CallbackPath = "/signin-oidc";
                o.SignedOutCallbackPath = "/signout-callback-oidc";
                o.Scope.Clear();
                o.Scope.Add("openid");
                o.Scope.Add("profile");
                o.Scope.Add("email");
                o.TokenValidationParameters.NameClaimType = "name";
                // Authority /organizations jest wielotenantowe: wystawca zawiera tid zalogowanego użytkownika, więc
                // zamiast stałego wystawcy akceptujemy wzorzec tenanta Entra v2.0 (dozwolone tenanty ogranicza Auth:AllowedTenantIds).
                o.TokenValidationParameters.IssuerValidator = (issuer, _, _) =>
                    EntraIssuer().IsMatch(issuer) ? issuer : throw new SecurityTokenInvalidIssuerException($"Nieprawidłowy wystawca tokenu: {issuer}");

                o.Events.OnRedirectToIdentityProvider = ctx =>
                {
                    var server = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<ServerOptions>>().Value;
                    if (!string.IsNullOrWhiteSpace(server.PublicUrl))
                        ctx.ProtocolMessage.RedirectUri = server.PublicUrl.TrimEnd('/') + o.CallbackPath;
                    return Task.CompletedTask;
                };
                o.Events.OnTokenValidated = ctx => ValidateEntraToken(ctx, auth);
                o.Events.OnRemoteFailure = ctx =>
                {
                    ctx.Response.Redirect("/logowanie?error=" + Uri.EscapeDataString(
                        "Logowanie kontem uczelni nie powiodło się" + (ctx.Failure?.Message is { Length: > 0 } m ? ": " + Truncate(m) : ".")));
                    ctx.HandleResponse();
                    return Task.CompletedTask;
                };
            });
        }

        return services;
    }

    private static Task ValidateEntraToken(TokenValidatedContext ctx, AuthOptions auth)
    {
        var principal = ctx.Principal;
        var uni = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<UniversityOptions>>().Value;
        var tid = principal?.FindFirst("tid")?.Value;
        var oid = principal?.FindFirst("oid")?.Value ?? principal?.FindFirst("sub")?.Value;
        // preferred_username/upn wymagają zweryfikowanej domeny tenanta, więc są wiarygodniejsze niż claim "email".
        var email = new[] { "preferred_username", "upn", "email" }
            .Select(c => principal?.FindFirst(c)?.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) && v.Contains('@', StringComparison.Ordinal));

        if (principal is null || string.IsNullOrEmpty(oid) || string.IsNullOrEmpty(tid))
        {
            ctx.Fail("Nie udało się ustalić tożsamości konta Microsoft 365.");
            return Task.CompletedTask;
        }
        if (auth.AllowedTenantIds.Length > 0 && !auth.AllowedTenantIds.Contains(tid, StringComparer.OrdinalIgnoreCase))
        {
            ctx.Fail("To konto należy do niedozwolonej organizacji. Zaloguj się kontem uczelni.");
            return Task.CompletedTask;
        }
        if (!UserIdentity.IsAllowedEmail(email, uni.AllowedEmailDomains))
        {
            ctx.Fail("To konto nie należy do uczelni. " + UserIdentity.DomainsMessage(uni.AllowedEmailDomains));
            return Task.CompletedTask;
        }

        var identity = (ClaimsIdentity)principal.Identity!;
        var name = principal.FindFirst("name")?.Value is { Length: > 0 } n ? n : UserIdentity.FormatName(email!);
        identity.AddClaim(new Claim(UserIdentity.UidClaim, $"entra:{tid.ToLowerInvariant()}:{oid.ToLowerInvariant()}"));
        identity.AddClaim(new Claim(ClaimTypes.Email, email!.Trim().ToLowerInvariant()));
        identity.AddClaim(new Claim(ClaimTypes.Name, name));
        return Task.CompletedTask;
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";

    public static ClaimsPrincipal CreateDevPrincipal(string email)
    {
        email = email.Trim().ToLowerInvariant();
        var identity = new ClaimsIdentity(
        [
            new Claim(UserIdentity.UidClaim, "dev:" + email),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Name, UserIdentity.FormatName(email)),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    /// <summary>Wszystko poza stronami logowania wymaga sesji; żądania dokumentów bez niej trafiają na /logowanie.</summary>
    public static IApplicationBuilder UseLoginRedirect(this IApplicationBuilder app, IConfiguration configuration)
    {
        var autoSignIn = !string.IsNullOrWhiteSpace(configuration["Dev:AutoSignInEmail"]) && ReadOptions(configuration).IsDev;
        return app.Use(async (ctx, next) =>
        {
            if ((HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))
                && ctx.User.Identity?.IsAuthenticated != true
                && RequiresLogin(ctx.Request.Path))
            {
                var returnUrl = Uri.EscapeDataString(UserIdentity.SafeReturnUrl(ctx.Request.Path + ctx.Request.QueryString));
                ctx.Response.Redirect(autoSignIn ? $"/auth/login?returnUrl={returnUrl}" : $"/logowanie?returnUrl={returnUrl}");
                return;
            }
            await next();
        });
    }

    /// <summary>Włącza <c>UseAnonymousSessions</c> tylko w trybie anonymous (wołać po UseAuthentication, przed UseLoginRedirect).</summary>
    public static IApplicationBuilder UseVusosAuthMode(this IApplicationBuilder app, IConfiguration configuration)
        => ReadOptions(configuration).IsAnonymous ? app.UseAnonymousSessions() : app;

    private static readonly string[] OpenPrefixes =
        ["/logowanie", "/dev-logowanie", "/auth/", "/signin-oidc", "/signout-callback-oidc", "/connect/", "/dl/",
         "/_framework", "/_blazor", "/_content", "/_vs", "/not-found", "/Error", "/health"];

    private static bool RequiresLogin(PathString path)
    {
        var p = path.Value ?? "/";
        if (OpenPrefixes.Any(prefix => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return false;
        // Zasoby statyczne mają rozszerzenie; strony Razor nie.
        return string.IsNullOrEmpty(Path.GetExtension(p));
    }
}
