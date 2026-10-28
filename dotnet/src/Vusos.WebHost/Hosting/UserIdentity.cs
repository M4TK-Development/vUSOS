using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Vusos.WebHost.Hosting;

/// <summary>
/// Tożsamość użytkownika WebHost: jeden stabilny identyfikator w claimie <see cref="UidClaim"/> (dev: e-mail,
/// Entra: tid + oid) oraz jego skrót, który nazywa katalog danych i zakres cache użytkownika.
/// </summary>
public static class UserIdentity
{
    public const string UidClaim = "vusos:uid";

    /// <summary>Identyfikator użytkownika z zalogowanego principala albo null.</summary>
    public static string? GetUserId(ClaimsPrincipal? principal)
        => principal?.Identity?.IsAuthenticated == true && principal.FindFirst(UidClaim)?.Value is { Length: > 0 } uid ? uid : null;

    /// <summary>Pierwsze 16 znaków hex z SHA-256 identyfikatora (nazwa katalogu użytkownika, UserScope).</summary>
    public static string Hash(string userId)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userId)))[..16];

    public static string? GetEmail(ClaimsPrincipal? principal) => principal?.FindFirst(ClaimTypes.Email)?.Value;

    public static string? GetDisplayName(ClaimsPrincipal? principal)
        => principal?.FindFirst(ClaimTypes.Name)?.Value is { Length: > 0 } n ? n : GetEmail(principal);

    public static string FormatName(string email)
    {
        var local = email.Split('@')[0];
        var parts = local.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..]);
        var name = string.Join(' ', parts);
        return name.Length == 0 ? email : name;
    }

    /// <summary>Czy adres e-mail należy do jednej z dozwolonych domen (lub jej subdomeny).</summary>
    public static bool IsAllowedEmail(string? email, IEnumerable<string> allowedDomains)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        var at = email.LastIndexOf('@');
        if (at <= 0 || at == email.Length - 1) return false;
        var domain = email[(at + 1)..].Trim().ToLowerInvariant();
        return allowedDomains.Any(d => domain == d.ToLowerInvariant() || domain.EndsWith("." + d.ToLowerInvariant(), StringComparison.Ordinal));
    }

    public static string DomainsMessage(IEnumerable<string> allowedDomains)
        => $"Dozwolone są tylko konta uczelni ({string.Join(", ", allowedDomains.Distinct(StringComparer.OrdinalIgnoreCase).Select(d => "@" + d))}).";

    /// <summary>Zwraca tylko adres lokalny (ochrona przed open redirect); w innym wypadku "/".</summary>
    public static string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl)) return "/";
        if (returnUrl[0] != '/' || returnUrl.StartsWith("//", StringComparison.Ordinal) || returnUrl.StartsWith("/\\", StringComparison.Ordinal)) return "/";
        return returnUrl;
    }
}
