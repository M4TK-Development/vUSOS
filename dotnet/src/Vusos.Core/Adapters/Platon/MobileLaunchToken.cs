using System.Text;
using System.Text.RegularExpressions;

namespace Vusos.Core.Adapters.Platon;

public readonly record struct ParsedMobileToken(string WsToken, string? PrivateToken = null);

/// <summary>
/// Parser tego, co wraca z przepływu aplikacji mobilnej Moodle (<c>admin/tool/mobile/launch.php</c>):
/// surowy token (32 znaki hex), <c>&lt;schemat&gt;://token=BASE64</c> (także z zakodowanym URL i w formie
/// <c>schemat://https://site?token=BASE64</c>) albo goły base64/base64url z <c>siteid:::token[:::privatetoken]</c>.
/// </summary>
public static partial class MobileLaunchToken
{
    [GeneratedRegex("^[A-Za-z0-9]{16,64}$")] private static partial Regex RawToken();
    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.IgnoreCase)] private static partial Regex HexToken();
    [GeneratedRegex("^[A-Za-z0-9+/]+={0,2}$")] private static partial Regex Base64Alphabet();
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*://")] private static partial Regex UrlScheme();
    [GeneratedRegex(@"(^|[?&])token=")] private static partial Regex TokenParameter();
    [GeneratedRegex(@"token=([^&\s#]+)")] private static partial Regex TokenValue();

    /// <exception cref="FormatException">Wejście nie wygląda na token Moodle.</exception>
    public static ParsedMobileToken Parse(string input)
    {
        var value = (input ?? string.Empty).Trim();
        if (value.Length == 0) throw new FormatException("Pusty token");

        if (UrlScheme().IsMatch(value) || TokenParameter().IsMatch(value))
        {
            var m = TokenValue().Match(value);
            if (!m.Success) throw new FormatException("Brak parametru token= w adresie");
            try
            {
                value = Uri.UnescapeDataString(m.Groups[1].Value);
            }
            catch (UriFormatException)
            {
                value = m.Groups[1].Value;
            }
        }

        // 32 znaki hex to surowy token; sprawdzamy przed base64 (hex jest poprawnym alfabetem base64).
        if (HexToken().IsMatch(value)) return new ParsedMobileToken(value);

        var decoded = DecodeBase64(value);
        if (decoded is not null && ParseDecoded(decoded) is { } parsed) return parsed;

        // TODO(verify): tokeny Moodle mają 32 znaki hex; inne alfanumeryczne przyjmujemy łagodnie.
        if (RawToken().IsMatch(value)) return new ParsedMobileToken(value);

        throw new FormatException("Nierozpoznany format tokenu Moodle");
    }

    public static bool TryParse(string? input, out ParsedMobileToken token)
    {
        try
        {
            token = Parse(input ?? string.Empty);
            return true;
        }
        catch (FormatException)
        {
            token = default;
            return false;
        }
    }

    private static string? DecodeBase64(string input)
    {
        var normalized = input.Replace('-', '+').Replace('_', '/');
        normalized = string.Concat(normalized.Where(c => !char.IsWhiteSpace(c)));
        if (!Base64Alphabet().IsMatch(normalized)) return null;
        var core = normalized.TrimEnd('=');
        if (core.Length % 4 == 1) return null;
        var padded = core + new string('=', (4 - core.Length % 4) % 4);
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Dekoduje <c>siteid:::token[:::privatetoken]</c>.</summary>
    private static ParsedMobileToken? ParseDecoded(string decoded)
    {
        if (!decoded.Contains(":::", StringComparison.Ordinal)) return null;
        var parts = decoded.Split(":::");
        var ws = parts.Length > 1 ? parts[1].Trim() : string.Empty;
        if (ws.Length == 0 || !RawToken().IsMatch(ws)) return null;
        var priv = parts.Length > 2 ? parts[2].Trim() : string.Empty;
        return priv.Length > 0 ? new ParsedMobileToken(ws, priv) : new ParsedMobileToken(ws);
    }
}
