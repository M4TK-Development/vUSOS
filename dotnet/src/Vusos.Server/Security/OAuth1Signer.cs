using System.Security.Cryptography;
using System.Text;

namespace Vusos.Server.Security;

/// <summary>
/// Minimalny podpisujący OAuth 1.0a (RFC 5849), HMAC-SHA1. Parametry zapytania i formularza
/// wchodzą do podstawy podpisu; parametry oauth_* trafiają do nagłówka Authorization.
/// </summary>
public sealed class OAuth1Signer(
    string consumerKey,
    string consumerSecret,
    Func<string>? nonceFactory = null,
    Func<long>? timestampFactory = null)
{
    private readonly Func<string> _nonce = nonceFactory ?? (() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
    private readonly Func<long> _timestamp = timestampFactory ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>RFC 3986: niezarezerwowane A-Z a-z 0-9 - . _ ~, reszta jako %XX (UTF-8, wielkie litery).</summary>
    public static string PercentEncode(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~')) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>Kanoniczny URL podstawy: schemat/host małymi literami, bez domyślnego portu, query i fragmentu.</summary>
    public static string NormalizeBaseUrl(string url)
    {
        var u = new Uri(url, UriKind.Absolute);
        var port = u.IsDefaultPort ? "" : ":" + u.Port;
        return $"{u.Scheme.ToLowerInvariant()}://{u.Host.ToLowerInvariant()}{port}{u.AbsolutePath}";
    }

    public static string BuildSignatureBase(string method, string url, IEnumerable<KeyValuePair<string, string>> allParams)
    {
        var paramString = string.Join("&", allParams
            .Select(p => (K: PercentEncode(p.Key), V: PercentEncode(p.Value)))
            .OrderBy(p => p.K, StringComparer.Ordinal)
            .ThenBy(p => p.V, StringComparer.Ordinal)
            .Select(p => $"{p.K}={p.V}"));
        return $"{method.ToUpperInvariant()}&{PercentEncode(NormalizeBaseUrl(url))}&{PercentEncode(paramString)}";
    }

    public static string ComputeSignature(string signatureBase, string consumerSecret, string? tokenSecret)
    {
        var key = Encoding.UTF8.GetBytes($"{PercentEncode(consumerSecret)}&{PercentEncode(tokenSecret ?? "")}");
        return Convert.ToBase64String(HMACSHA1.HashData(key, Encoding.UTF8.GetBytes(signatureBase)));
    }

    /// <summary>
    /// Zwraca wartość nagłówka Authorization. <paramref name="requestParams"/> to parametry zapytania i
    /// formularza (zdekodowane), które wchodzą do podpisu, ale nie do nagłówka.
    /// </summary>
    public string CreateAuthorizationHeader(
        string method,
        string url,
        IReadOnlyCollection<KeyValuePair<string, string>> requestParams,
        string? token = null,
        string? tokenSecret = null)
    {
        var oauth = new List<KeyValuePair<string, string>>
        {
            new("oauth_consumer_key", consumerKey),
            new("oauth_nonce", _nonce()),
            new("oauth_signature_method", "HMAC-SHA1"),
            new("oauth_timestamp", _timestamp().ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("oauth_version", "1.0"),
        };
        if (!string.IsNullOrEmpty(token)) oauth.Add(new("oauth_token", token));

        var signatureBase = BuildSignatureBase(method, url, requestParams.Concat(oauth));
        var signature = ComputeSignature(signatureBase, consumerSecret, tokenSecret);
        oauth.Add(new("oauth_signature", signature));

        return "OAuth " + string.Join(", ", oauth.Select(p => $"{PercentEncode(p.Key)}=\"{PercentEncode(p.Value)}\""));
    }

    /// <summary>Kodowanie application/x-www-form-urlencoded zgodne z podpisem (RFC 3986).</summary>
    public static string EncodeForm(IEnumerable<KeyValuePair<string, string>> pairs) =>
        string.Join("&", pairs.Select(p => $"{PercentEncode(p.Key)}={PercentEncode(p.Value)}"));
}
