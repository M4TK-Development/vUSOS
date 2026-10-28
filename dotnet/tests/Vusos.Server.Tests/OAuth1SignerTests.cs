using System.Security.Cryptography;
using System.Text;
using Vusos.Server.Security;

namespace Vusos.Server.Tests;

public class OAuth1SignerTests
{
    // Znany wektor (dokumentacja Twittera "Creating a signature"): niezależny od naszej implementacji.
    [Fact]
    public void Known_answer_twitter_vector()
    {
        var signer = new OAuth1Signer("xvz1evFS4wEEPTGEFPHBog", "kAcSOqF21Fu85e7zjz7ZN2U4ZRhfV3WpwPAoE3Z7kBw",
            () => "kYjzVBB8Y0ZFabxSWbWovY3uYSQ2pTgmZeNu2VS4cg", () => 1318622958);
        var header = signer.CreateAuthorizationHeader("POST", "https://api.twitter.com/1/statuses/update.json",
        [
            new("include_entities", "true"),
            new("status", "Hello Ladies + Gentlemen, a signed OAuth request!"),
        ], "370773112-GmHxMAgYyLbNEtIKZeRNFsMKPR9EyMZeS9weJAEb", "LswwdoUaIvS8ltyTt5jkRh4J50vUPVVHtR2YPi5kE");

        Assert.Contains("oauth_signature=\"tnnArxj06cWHq44gCs1OSKk%2FjLY%3D\"", header);
    }

    [Fact]
    public void Known_answer_rfc5849_base_string()
    {
        // RFC 5849 sekcja 3.4.1.1 (przykład podstawy podpisu)
        var p = new List<KeyValuePair<string, string>>
        {
            new("b5", "=%3D"), new("a3", "a"), new("c@", ""), new("a2", "r b"),
            new("oauth_consumer_key", "9djdj82h48djs9d2"), new("oauth_token", "kkk9d7dh3k39sjv7"),
            new("oauth_signature_method", "HMAC-SHA1"), new("oauth_timestamp", "137131201"),
            new("oauth_nonce", "7d8f3e4a"), new("c2", ""), new("a3", "2 q"),
        };
        var b = OAuth1Signer.BuildSignatureBase("POST", "http://example.com/request", p);
        Assert.Equal(
            "POST&http%3A%2F%2Fexample.com%2Frequest&a2%3Dr%2520b%26a3%3D2%2520q%26a3%3Da%26b5%3D%253D%25253D%26c%2540%3D%26c2%3D%26oauth_consumer_key%3D9djdj82h48djs9d2%26oauth_nonce%3D7d8f3e4a%26oauth_signature_method%3DHMAC-SHA1%26oauth_timestamp%3D137131201%26oauth_token%3Dkkk9d7dh3k39sjv7",
            b);
    }

    [Fact]
    public void Signature_matches_independent_hmac_with_ts_style_parameters()
    {
        // Odtworzenie logiki oauth.test.ts: ten sam sposób liczenia podpisu, ale z kodem w teście.
        var signer = new OAuth1Signer("ckey", "csecret", () => "n0nce", () => 1700000000);
        var form = new List<KeyValuePair<string, string>>
        {
            new("oauth_callback", "vusos://usos"), new("scopes", "offline_access|studies|grades"),
        };
        var url = "https://usosapps.vistula.example/services/oauth/request_token";
        var header = signer.CreateAuthorizationHeader("POST", url, form);

        var all = new Dictionary<string, string>
        {
            ["oauth_callback"] = "vusos://usos", ["scopes"] = "offline_access|studies|grades",
            ["oauth_consumer_key"] = "ckey", ["oauth_nonce"] = "n0nce", ["oauth_signature_method"] = "HMAC-SHA1",
            ["oauth_timestamp"] = "1700000000", ["oauth_version"] = "1.0",
        };
        string Enc(string s) => Uri.EscapeDataString(s).Replace("!", "%21").Replace("'", "%27").Replace("(", "%28").Replace(")", "%29").Replace("*", "%2A");
        var paramString = string.Join("&", all.Select(kv => (Enc(kv.Key), Enc(kv.Value))).OrderBy(x => x.Item1, StringComparer.Ordinal).Select(x => $"{x.Item1}={x.Item2}"));
        var sigBase = $"POST&{Enc(url)}&{Enc(paramString)}";
        var expected = Convert.ToBase64String(new HMACSHA1(Encoding.UTF8.GetBytes("csecret&")).ComputeHash(Encoding.UTF8.GetBytes(sigBase)));

        Assert.Contains($"oauth_signature=\"{Uri.EscapeDataString(expected)}\"", header);
        Assert.Contains("oauth_consumer_key=\"ckey\"", header);
        Assert.DoesNotContain("oauth_token=", header);
        Assert.DoesNotContain("scopes", header);
    }

    [Theory]
    [InlineData("abc-._~", "abc-._~")]
    [InlineData("a b", "a%20b")]
    [InlineData("!'()*", "%21%27%28%29%2A")]
    [InlineData("zażółć", "za%C5%BC%C3%B3%C5%82%C4%87")]
    [InlineData("a+b/c=", "a%2Bb%2Fc%3D")]
    public void PercentEncode_follows_rfc3986(string input, string expected) =>
        Assert.Equal(expected, OAuth1Signer.PercentEncode(input));

    [Fact]
    public void Base_url_is_normalized_and_query_dropped()
    {
        Assert.Equal("https://example.com/a/b", OAuth1Signer.NormalizeBaseUrl("HTTPS://Example.com:443/a/b?x=1"));
        Assert.Equal("http://example.com:8080/a", OAuth1Signer.NormalizeBaseUrl("http://example.com:8080/a"));
    }
}
