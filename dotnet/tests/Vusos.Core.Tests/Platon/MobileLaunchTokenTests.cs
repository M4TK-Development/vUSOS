using System.Text;
using Vusos.Core.Adapters.Platon;

namespace Vusos.Core.Tests.Platon;

public class MobileLaunchTokenTests
{
    private const string Ws = "0123456789abcdef0123456789abcdef";
    private const string Private = "FEDCBA9876543210fedcba9876543210";

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void Accepts_a_raw_wstoken()
    {
        var t = MobileLaunchToken.Parse($"  {Ws}\n");
        Assert.Equal(Ws, t.WsToken);
        Assert.Null(t.PrivateToken);
    }

    [Fact]
    public void Decodes_scheme_url_with_private_token()
    {
        var payload = B64($"siteidmd5hash:::{Ws}:::{Private}");
        var t = MobileLaunchToken.Parse($"moodlemobile://token={payload}");
        Assert.Equal((Ws, Private), (t.WsToken, t.PrivateToken));
    }

    [Fact]
    public void Accepts_the_app_own_scheme()
    {
        var t = MobileLaunchToken.Parse($"vusos://token={B64($"x:::{Ws}:::{Private}")}");
        Assert.Equal(Ws, t.WsToken);
        Assert.Equal(Private, t.PrivateToken);
    }

    [Fact]
    public void Handles_url_encoded_padding_and_the_site_url_form()
    {
        var payload = B64($"abc:::{Ws}");
        Assert.EndsWith("=", payload);
        Assert.Equal(Ws, MobileLaunchToken.Parse($"moodlemobile://token={Uri.EscapeDataString(payload)}").WsToken);
        Assert.Equal(Ws, MobileLaunchToken.Parse($"moodlemobile://https://platon.vistula.edu.pl?token={payload}").WsToken);
    }

    [Fact]
    public void Decodes_bare_base64_and_base64url()
    {
        var payload = B64($"site:::{Ws}:::{Private}");
        var t = MobileLaunchToken.Parse(payload);
        Assert.Equal((Ws, Private), (t.WsToken, t.PrivateToken));
        var url = payload.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        Assert.Equal(Ws, MobileLaunchToken.Parse(url).WsToken);
    }

    [Fact]
    public void Deconstructs_like_a_tuple()
    {
        var (ws, priv) = MobileLaunchToken.Parse(B64($"s:::{Ws}:::{Private}"));
        Assert.Equal(Ws, ws);
        Assert.Equal(Private, priv);
    }

    [Fact]
    public void Omits_an_empty_private_token()
    {
        var t = MobileLaunchToken.Parse(B64($"site:::{Ws}:::"));
        Assert.Equal(Ws, t.WsToken);
        Assert.Null(t.PrivateToken);
    }

    [Fact]
    public void Accepts_other_alphanumeric_tokens_leniently()
    {
        Assert.Equal("Abcdefghijklmnop1234", MobileLaunchToken.Parse("Abcdefghijklmnop1234").WsToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("moodlemobile://")]
    [InlineData("not a token!!")]
    [InlineData("short")]
    public void Rejects_garbage(string input)
    {
        Assert.Throws<FormatException>(() => MobileLaunchToken.Parse(input));
        Assert.False(MobileLaunchToken.TryParse(input, out _));
    }

    [Fact]
    public void Rejects_a_payload_without_a_token_part()
    {
        Assert.Throws<FormatException>(() => MobileLaunchToken.Parse(B64("only:::")));
    }
}
