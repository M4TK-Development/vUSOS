using Microsoft.AspNetCore.DataProtection;

namespace Vusos.WebHost.Hosting;

/// <summary>
/// Krótkotrwałe tokeny pobierania pliku (<c>/dl/{token}</c>). Token zawiera skrót użytkownika i ścieżkę; endpoint odrzuca
/// token wystawiony dla innego użytkownika i ścieżki spoza jego katalogu materiałów.
/// </summary>
public sealed class DownloadTokens(IDataProtectionProvider dataProtection)
{
    public const string Purpose = "Vusos.WebHost.FileDownload";

    private readonly ITimeLimitedDataProtector _protector = dataProtection.CreateProtector(Purpose).ToTimeLimitedDataProtector();

    public string Issue(string userHash, string localPath, TimeSpan? lifetime = null)
        => _protector.Protect(userHash + "|" + localPath, lifetime ?? TimeSpan.FromMinutes(10));

    public bool TryRead(string token, out string userHash, out string path)
    {
        userHash = path = "";
        try
        {
            var plain = _protector.Unprotect(token);
            var sep = plain.IndexOf('|', StringComparison.Ordinal);
            if (sep <= 0) return false;
            userHash = plain[..sep];
            path = plain[(sep + 1)..];
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
