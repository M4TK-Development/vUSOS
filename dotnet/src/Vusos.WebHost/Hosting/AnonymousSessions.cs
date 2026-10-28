using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace Vusos.WebHost.Hosting;

/// <summary>
/// Tryb <c>Auth:Mode=anonymous</c>: WebHost nie loguje przez Entra ID (nie ma rejestracji aplikacji). Pierwsza wizyta dostaje
/// podpisane cookie <see cref="CookieName"/> z losowym identyfikatorem (32 bajty, chronione Data Protection, HttpOnly),
/// identyfikatorem użytkownika jest <c>anon:{id}</c>, a tożsamość studenta wynika z połączeń z USOS/Platonem
/// (<see cref="Vusos.Core.Hosting.ConnectionIdentity"/>). Cookie identyfikuje „skrytkę” z danymi, nie konto: kto je ma, ma dane.
/// </summary>
public sealed class AnonymousSessions(IDataProtectionProvider dataProtection)
{
    public const string CookieName = "vusos.sid";
    public const string UserIdPrefix = "anon:";
    private const string Purpose = "Vusos.WebHost.AnonymousSession.v1";
    private const int IdBytes = 32;

    private readonly IDataProtector _protector = dataProtection.CreateProtector(Purpose);

    /// <summary>Okres ważności cookie, odnawiany przy każdym otwarciu strony (dane żyją na dysku, dopóki użytkownik się nie wyloguje).</summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromDays(180);

    public static string UserIdOf(string sessionId) => UserIdPrefix + sessionId;

    public static ClaimsPrincipal CreatePrincipal(string sessionId)
        => new(new ClaimsIdentity([new Claim(UserIdentity.UidClaim, UserIdOf(sessionId))],
            authenticationType: "vusos-anonymous", nameType: ClaimTypes.Name, roleType: ClaimTypes.Role));

    /// <summary>Zwraca identyfikator z poprawnego cookie albo null (brak, uszkodzone, podrobione).</summary>
    public string? TryRead(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrEmpty(value)) return null;
        try
        {
            var id = _protector.Unprotect(value);
            return Base64UrlTextEncoder.Decode(id).Length == IdBytes ? id : null;
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Tworzy nową sesję i zapisuje cookie w odpowiedzi.</summary>
    public string Issue(HttpContext http)
    {
        var id = Base64UrlTextEncoder.Encode(RandomNumberGenerator.GetBytes(IdBytes));
        Write(http, id);
        return id;
    }

    /// <summary>Ponownie zapisuje cookie istniejącej sesji (przesuwa termin ważności).</summary>
    public void Renew(HttpContext http, string sessionId) => Write(http, sessionId);

    private void Write(HttpContext http, string id)
        => http.Response.Cookies.Append(CookieName, _protector.Protect(id), CookieOptions(http));

    public void Delete(HttpContext http) => http.Response.Cookies.Delete(CookieName, CookieOptions(http));

    private static CookieOptions CookieOptions(HttpContext http) => new()
    {
        HttpOnly = true,
        IsEssential = true, // bez tego cookie to jedyny sposób na dostęp do własnych danych
        SameSite = SameSiteMode.Lax, // wraca z callbacku USOS (nawigacja najwyższego poziomu)
        Secure = http.Request.IsHttps,
        Path = "/",
        MaxAge = Lifetime,
    };
}

/// <summary>Potok HTTP trybu anonymous.</summary>
public static class AnonymousSessionMiddleware
{
    /// <summary>
    /// Middleware: ustawia <c>HttpContext.User</c> na użytkownika anonimowego z cookie; dla żądań stron i endpointów bez
    /// poprawnego cookie wystawia nowe (zasoby statyczne i /health go nie tworzą).
    /// </summary>
    public static IApplicationBuilder UseAnonymousSessions(this IApplicationBuilder app)
        => app.Use(async (ctx, next) =>
        {
            var sessions = ctx.RequestServices.GetRequiredService<AnonymousSessions>();
            var id = sessions.TryRead(ctx);
            var reExecuted = ctx.Features.Get<IStatusCodeReExecuteFeature>() is not null; // 404 dla zasobu statycznego itp.
            if (id is null && !reExecuted && CreatesSession(ctx.Request.Path)) id = sessions.Issue(ctx);
            else if (id is not null && !reExecuted && HttpMethods.IsGet(ctx.Request.Method) && CreatesSession(ctx.Request.Path)
                     && !ctx.Request.Path.StartsWithSegments("/_blazor") && !ctx.Request.Path.StartsWithSegments("/auth/logout"))
                sessions.Renew(ctx, id);
            if (id is not null) ctx.User = AnonymousSessions.CreatePrincipal(id);
            await next();
        });

    private static bool CreatesSession(PathString path)
    {
        var p = path.Value ?? "/";
        if (p.StartsWith("/health", StringComparison.OrdinalIgnoreCase)) return false;
        return string.IsNullOrEmpty(Path.GetExtension(p)); // pliki statyczne mają rozszerzenie, strony i endpointy nie
    }
}
