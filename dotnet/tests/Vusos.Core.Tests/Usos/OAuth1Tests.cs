using Vusos.Core.Adapters.Usos;

namespace Vusos.Core.Tests.Usos;

/// <summary>
/// Known-answer vectors were recomputed independently (Python hmac/hashlib) from the published base strings:
/// OAuth 1.0 spec appendix A.1, the Twitter signing example, and the RFC 5849 section 3.4.1 request.
/// </summary>
public class OAuth1Tests
{
    private static string SignatureOf(string header) => OAuthVerifier.ParseHeader(header)["oauth_signature"];

    [Fact]
    public void PercentEncode_keeps_only_unreserved_characters_and_encodes_utf8()
    {
        Assert.Equal("a%20b%26c%2A%C5%82~%21%27%28%29", OAuth1.PercentEncode("a b&c*ł~!'()"));
        Assert.Equal("AZaz09-._~", OAuth1.PercentEncode("AZaz09-._~"));
        Assert.Equal("%2B%2F%3D%25%3A", OAuth1.PercentEncode("+/=%:"));
        Assert.Equal("", OAuth1.PercentEncode(""));
    }

    [Fact]
    public void PercentEncode_agrees_with_Uri_EscapeDataString()
    {
        const string sample = "Cześć! (test) *+/=?&#%~_-.";
        Assert.Equal(Uri.EscapeDataString(sample), OAuth1.PercentEncode(sample));
    }

    [Fact]
    public void KnownAnswer_oauth_spec_appendix_A1_get_with_query()
    {
        var header = OAuth1.CreateAuthorizationHeader(
            "GET", new Uri("http://photos.example.net/photos?file=vacation.jpg&size=original"), [],
            "dpf43f3p2l4k3l03", "kd94hf93k423kf44", "nnch734d00sl2jdk", "pfkkdhi9sl3r4s00",
            nonce: "kllo9940pd9333jh", timestamp: 1191242096);
        Assert.Equal("tR3+Ty81lMeYAr/Fid0kMTYa/WM=", SignatureOf(header));
        Assert.Contains("oauth_signature=\"tR3%2BTy81lMeYAr%2FFid0kMTYa%2FWM%3D\"", header);
    }

    [Fact]
    public void KnownAnswer_twitter_post_with_query_and_body_parameter()
    {
        var header = OAuth1.CreateAuthorizationHeader(
            "POST", new Uri("https://api.twitter.com/1.1/statuses/update.json?include_entities=true"),
            [new("status", "Hello Ladies + Gentlemen, a signed OAuth request!")],
            "xvz1evFS4wEEPTGEFPHBog", "kAcSOqF21Fu85e7zjz7ZN2U4ZRhfV3WpwPAoE3Z7kBw",
            "370773112-GmHxMAgYyLbNEtIKZeRNFsMKPR9EyMZeS9weJAEb", "LswwdoUaIvS8ltyTt5jkRh4J50vUPVVHtR2YPi5kE",
            nonce: "kYjzVBB8Y0ZFabxSWbWovY3uYSQ2pTgmZeNu2VS4cg", timestamp: 1318622958);
        Assert.Equal("hCtSmYh+iHYCEqBWrE7C7hYmtUk=", SignatureOf(header));
    }

    [Fact]
    public void KnownAnswer_rfc5849_section_3_4_1_base_string_with_duplicate_names()
    {
        // Request from RFC 5849 3.4.1: query a3=a&b5=%3D%253D&c%40=&a2=r%20b, form body c2&a3=2+q.
        var oauth = new KeyValuePair<string, string>[]
        {
            new("oauth_consumer_key", "9djdj82h48djs9d2"), new("oauth_token", "kkk9d7dh3k39sjv7"),
            new("oauth_signature_method", "HMAC-SHA1"), new("oauth_timestamp", "137131201"), new("oauth_nonce", "7d8f3e4a"),
        };
        var body = new KeyValuePair<string, string>[] { new("c2", ""), new("a3", "2 q") };
        var baseString = OAuth1.BuildBaseString(
            "POST", new Uri("http://EXAMPLE.com:80/request?b5=%3D%253D&a3=a&c%40=&a2=r%20b"), body.Concat(oauth));
        Assert.Equal(
            "POST&http%3A%2F%2Fexample.com%2Frequest&a2%3Dr%2520b%26a3%3D2%2520q%26a3%3Da%26b5%3D%253D%25253D%26c%2540%3D%26c2%3D"
            + "%26oauth_consumer_key%3D9djdj82h48djs9d2%26oauth_nonce%3D7d8f3e4a%26oauth_signature_method%3DHMAC-SHA1"
            + "%26oauth_timestamp%3D137131201%26oauth_token%3Dkkk9d7dh3k39sjv7",
            baseString);
        Assert.Equal("r6/TJjbCOr97/+UU0NsvSne7s5g=", OAuth1.ComputeSignature(baseString, "j49sk3j29djd", "dh893hdasih9"));
    }

    [Fact]
    public void KnownAnswer_usos_style_get_with_reserved_and_unicode_characters()
    {
        var header = OAuth1.CreateAuthorizationHeader(
            "GET", new Uri("https://usosapps.vistula.example/services/users/user"),
            [new("note", "a b&c*ł~!'()"), new("n", "3")],
            "ckey", "csecret", "atoken", "asecret", nonce: "abc123", timestamp: 1700000000);
        Assert.Equal("PisKJB/f7SeU+E+S/5HG0xY+PBs=", SignatureOf(header));
    }

    [Fact]
    public void KnownAnswer_request_token_without_token_uses_empty_token_secret()
    {
        var header = OAuth1.CreateAuthorizationHeader(
            "POST", new Uri("https://usosapps.vistula.example/services/oauth/request_token"),
            [new("oauth_callback", "vusos://usos"), new("scopes", "offline_access|studies")],
            "ckey", "csecret", nonce: "n0nce", timestamp: 1700000001);
        Assert.Equal("V/B+ZTXPBE5POXR6USRjEbQRb1k=", SignatureOf(header));
        Assert.DoesNotContain("oauth_token", header);
    }

    [Fact]
    public void Header_is_sorted_quoted_and_leaves_body_parameters_out()
    {
        var header = OAuth1.CreateAuthorizationHeader(
            "POST", new Uri("https://x.example/a"), [new("secret_body_param", "v")], "ck", "cs", "tok", "ts", "nn", 5);
        Assert.StartsWith("OAuth oauth_consumer_key=\"ck\", oauth_nonce=\"nn\", oauth_signature=\"", header);
        Assert.Contains("oauth_signature_method=\"HMAC-SHA1\", oauth_timestamp=\"5\", oauth_token=\"tok\", oauth_version=\"1.0\"", header);
        Assert.DoesNotContain("secret_body_param", header);
    }

    [Fact]
    public void BaseStringUri_normalises_scheme_host_port_and_drops_query()
    {
        Assert.Equal("https://usosapps.vistula.example/services/x", OAuth1.BaseStringUri(new Uri("HTTPS://UsosApps.Vistula.Example:443/services/x?a=1#f")));
        Assert.Equal("http://h.example:8080/p", OAuth1.BaseStringUri(new Uri("http://h.example:8080/p?q")));
        Assert.Equal("https://h.example/", OAuth1.BaseStringUri(new Uri("https://h.example")));
    }

    [Fact]
    public void NormalizeParameters_sorts_by_encoded_name_then_value()
    {
        var s = OAuth1.NormalizeParameters([new("b", "2"), new("a", "z"), new("a", "b"), new("a b", "1"), new("a~", "0")]);
        Assert.Equal("a=b&a=z&a%20b=1&a~=0&b=2", s);
    }

    [Fact]
    public void Nonce_is_random_and_timestamp_is_current()
    {
        Assert.NotEqual(OAuth1.GenerateNonce(), OAuth1.GenerateNonce());
        Assert.Equal(32, OAuth1.GenerateNonce().Length);
        Assert.InRange(OAuth1.UnixTimestamp(), DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5);
    }

    [Fact]
    public void FormEncode_and_ParseForm_round_trip()
    {
        var pairs = new List<KeyValuePair<string, string>> { new("scopes", "a|b"), new("txt", "Cześć & co=?"), new("empty", "") };
        var encoded = OAuth1.FormEncode(pairs);
        Assert.DoesNotContain(" ", encoded);
        Assert.Equal(pairs, OAuth1.ParseForm(encoded));
        Assert.Equal([new("a", "x y")], OAuth1.ParseForm("a=x+y"));
    }
}
