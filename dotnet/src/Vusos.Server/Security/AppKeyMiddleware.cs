using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Vusos.Server.Options;

namespace Vusos.Server.Security;

/// <summary>
/// Gdy Usos:AppKey jest ustawiony, /usos/* wymaga nagłówka X-Vusos-App-Key.
/// Wyjątek: /usos/oauth/callback (wywołuje go przeglądarka po autoryzacji, bez naszych nagłówków).
/// </summary>
public sealed class AppKeyMiddleware(RequestDelegate next, IOptionsMonitor<UsosOptions> options)
{
    public const string HeaderName = "X-Vusos-App-Key";

    public Task InvokeAsync(HttpContext ctx)
    {
        var expected = options.CurrentValue.AppKey;
        var path = ctx.Request.Path;
        if (!string.IsNullOrEmpty(expected) &&
            path.StartsWithSegments("/usos") &&
            !path.StartsWithSegments("/usos/oauth/callback"))
        {
            var provided = ctx.Request.Headers[HeaderName].ToString();
            if (!FixedEquals(provided, expected))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return ctx.Response.WriteAsJsonAsync(new { code = "APP_KEY_REQUIRED" });
            }
        }
        return next(ctx);
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
