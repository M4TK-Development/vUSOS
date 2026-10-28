using System.Security.Cryptography;
using System.Text;

namespace Vusos.Core.Adapters.Usos;

/// <summary>
/// OAuth 1.0a (RFC 5849), HMAC-SHA1. Query parameters of the URL and the supplied form-body parameters are
/// both part of the signature; the protocol parameters travel in the Authorization header.
/// </summary>
public static class OAuth1
{
    /// <summary>RFC 3986 percent-encoding of UTF-8 bytes; only ALPHA / DIGIT / "-" / "." / "_" / "~" stay literal.</summary>
    public static string PercentEncode(string value)
    {
        if (value.Length == 0) return value;
        var sb = new StringBuilder(value.Length + 8);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9'
                or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~')
                sb.Append((char)b);
            else
                sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>name=value pairs joined with "&amp;", both parts percent-encoded (application/x-www-form-urlencoded, RFC 3986 flavour).</summary>
    public static string FormEncode(IEnumerable<KeyValuePair<string, string>> pairs) =>
        string.Join('&', pairs.Select(p => $"{PercentEncode(p.Key)}={PercentEncode(p.Value)}"));

    /// <summary>Decodes a query string / form body ("+" means space). Used for tests and for the OAuth endpoints' replies.</summary>
    public static List<KeyValuePair<string, string>> ParseForm(string? form)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(form)) return list;
        foreach (var part in form.Trim().TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            var k = i < 0 ? part : part[..i];
            var v = i < 0 ? "" : part[(i + 1)..];
            list.Add(new(Decode(k), Decode(v)));
        }
        return list;

        static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
    }

    /// <summary>scheme://host[:port]/path (scheme and host lower-case, default port dropped, no query or fragment).</summary>
    public static string BaseStringUri(Uri url)
    {
        var scheme = url.Scheme.ToLowerInvariant();
        var host = url.Host.ToLowerInvariant();
        if (url.HostNameType == UriHostNameType.IPv6) host = $"[{host.Trim('[', ']')}]";
        var defaultPort = (scheme == "http" && url.Port == 80) || (scheme == "https" && url.Port == 443);
        var authority = url.IsDefaultPort || defaultPort ? host : $"{host}:{url.Port}";
        var path = url.AbsolutePath;
        return $"{scheme}://{authority}{(path.Length == 0 ? "/" : path)}";
    }

    /// <summary>RFC 5849 3.4.1.3.2: encode names and values, sort by name then value (byte order), join.</summary>
    public static string NormalizeParameters(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var encoded = parameters
            .Select(p => (Name: PercentEncode(p.Key), Value: PercentEncode(p.Value)))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal);
        return string.Join('&', encoded.Select(p => $"{p.Name}={p.Value}"));
    }

    /// <summary>
    /// Signature base string: METHOD &amp; enc(base uri) &amp; enc(normalized parameters). Parameters = the URL's own query
    /// parameters + <paramref name="parameters"/> (form body and oauth_* protocol parameters).
    /// </summary>
    public static string BuildBaseString(string method, Uri url, IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var all = ParseForm(url.Query).Concat(parameters);
        return $"{method.ToUpperInvariant()}&{PercentEncode(BaseStringUri(url))}&{PercentEncode(NormalizeParameters(all))}";
    }

    /// <summary>Base64 HMAC-SHA1 of the base string with key enc(consumerSecret)&amp;enc(tokenSecret).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "OAuth 1.0a (RFC 5849) mandates HMAC-SHA1; USOS accepts nothing else.")]
    public static string ComputeSignature(string baseString, string consumerSecret, string? tokenSecret)
    {
        var key = $"{PercentEncode(consumerSecret)}&{PercentEncode(tokenSecret ?? "")}";
        var hash = HMACSHA1.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(baseString));
        return Convert.ToBase64String(hash);
    }

    public static string GenerateNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static long UnixTimestamp() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>
    /// Builds the <c>Authorization: OAuth ...</c> header value. <paramref name="parameters"/> are the form-body
    /// parameters (they are signed but not repeated in the header). <paramref name="nonce"/> and
    /// <paramref name="timestamp"/> are injectable for known-answer tests.
    /// </summary>
    public static string CreateAuthorizationHeader(
        string method,
        Uri url,
        IEnumerable<KeyValuePair<string, string>> parameters,
        string consumerKey,
        string consumerSecret,
        string? token = null,
        string? tokenSecret = null,
        string? nonce = null,
        long? timestamp = null)
    {
        var oauth = new List<KeyValuePair<string, string>>
        {
            new("oauth_consumer_key", consumerKey),
            new("oauth_nonce", nonce ?? GenerateNonce()),
            new("oauth_signature_method", "HMAC-SHA1"),
            new("oauth_timestamp", (timestamp ?? UnixTimestamp()).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("oauth_version", "1.0"),
        };
        if (!string.IsNullOrEmpty(token)) oauth.Add(new("oauth_token", token));

        var baseString = BuildBaseString(method, url, parameters.Concat(oauth));
        oauth.Add(new("oauth_signature", ComputeSignature(baseString, consumerSecret, tokenSecret)));

        return "OAuth " + string.Join(", ", oauth
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{PercentEncode(p.Key)}=\"{PercentEncode(p.Value)}\""));
    }
}
