using Vusos.UI.Util;

namespace Vusos.UI.Tests;

public class SanitizerTests
{
    [Fact]
    public void Strips_scripts_styles_and_event_handlers()
    {
        var html = Sanitize("<p onclick=\"x()\" style=\"color:red\">Cześć<script>alert(1)</script><style>p{}</style></p><img src=\"a.png\" onerror=\"alert(1)\" alt=\"a\">");
        Assert.DoesNotContain("script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", html);
        Assert.DoesNotContain("onclick", html);
        Assert.DoesNotContain("onerror", html);
        Assert.DoesNotContain("style", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<p>Cześć</p>", html);
        Assert.Contains("<img src=\"a.png\" alt=\"a\">", html);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("  JaVa\tScRiPt:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    public void Drops_dangerous_link_targets(string href)
    {
        var html = Sanitize($"<a href=\"{href}\">x</a>");
        Assert.DoesNotContain("href", html);
        Assert.Contains("x", html);
    }

    [Fact]
    public void Links_open_in_new_tab_with_rel()
    {
        var html = Sanitize("<a href=\"https://platon.vistula.edu.pl/x?a=1&amp;b=2\">link</a>");
        Assert.Contains("href=\"https://platon.vistula.edu.pl/x?a=1&amp;b=2\"", html);
        Assert.Contains("target=\"_blank\"", html);
        Assert.Contains("rel=\"noopener noreferrer\"", html);
    }

    [Fact]
    public void Keeps_allowed_structure_and_unwraps_unknown_tags()
    {
        var html = Sanitize("<h2>Tytuł</h2><ul><li><b>a</b></li></ul><table><thead><tr><th>h</th></tr></thead><tbody><tr><td colspan=\"2\">c</td></tr></tbody></table><font color=\"red\">tekst</font>");
        Assert.Contains("<h2>Tytuł</h2>", html);
        Assert.Contains("<ul><li><b>a</b></li></ul>", html);
        Assert.Contains("<td colspan=\"2\">c</td>", html);
        Assert.Contains("tekst", html);
        Assert.DoesNotContain("font", html);
    }

    [Fact]
    public void Image_data_uris_only_for_images()
    {
        Assert.Contains("data:image/png;base64,AAAA", Sanitize("<img src=\"data:image/png;base64,AAAA\">"));
        Assert.DoesNotContain("src", Sanitize("<img src=\"data:text/html;base64,AAAA\">"));
    }

    [Fact]
    public void Content_profile_drops_form_controls_but_quiz_profile_keeps_them()
    {
        const string form = "<form action=\"/x\"><input type=\"radio\" name=\"q1:1_answer\" value=\"0\" id=\"a\"><label for=\"a\">tak</label><select name=\"s\"><option value=\"1\" selected>1</option></select><textarea name=\"t\">x</textarea><input type=\"file\" name=\"f\"></form>";
        var content = Sanitize(form);
        Assert.DoesNotContain("<input", content);
        Assert.DoesNotContain("<select", content);
        Assert.DoesNotContain("<form", content);

        var quiz = Sanitize(form, SanitizeProfile.QuizForm);
        Assert.Contains("<input type=\"radio\" name=\"q1:1_answer\" value=\"0\" id=\"a\">", quiz);
        Assert.Contains("<label for=\"a\">tak</label>", quiz);
        Assert.Contains("<select name=\"s\">", quiz);
        Assert.Contains("<option value=\"1\" selected>", quiz);
        Assert.Contains("<textarea name=\"t\">x</textarea>", quiz);
        Assert.DoesNotContain("type=\"file\"", quiz);
        Assert.DoesNotContain("<form", quiz);
    }

    [Fact]
    public void Review_profile_disables_controls()
    {
        var html = Sanitize("<input type=\"text\" name=\"a\" value=\"b\">", SanitizeProfile.QuizReview);
        Assert.Contains("disabled", html);
    }

    [Fact]
    public void Deep_nesting_does_not_overflow_the_stack()
    {
        var html = string.Concat(Enumerable.Repeat("<div>", 5000)) + "x" + string.Concat(Enumerable.Repeat("</div>", 5000));
        Assert.Contains("x", Sanitize(html));
    }

    [Fact]
    public void Empty_input_gives_empty_output()
    {
        Assert.Equal("", HtmlSanitizer.Sanitize(null));
        Assert.Equal("", HtmlSanitizer.Sanitize("   "));
    }

    private static string Sanitize(string html, SanitizeProfile profile = SanitizeProfile.Content) => HtmlSanitizer.Sanitize(html, profile);
}
