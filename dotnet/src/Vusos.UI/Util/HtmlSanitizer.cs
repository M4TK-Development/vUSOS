using System.Net;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Vusos.UI.Util;

public enum SanitizeProfile
{
    /// <summary>Ogłoszenia, wiadomości, opisy modułów: tylko treść.</summary>
    Content,
    /// <summary>Formularz pytania testu: dopuszcza kontrolki (input/select/textarea) i klasy.</summary>
    QuizForm,
    /// <summary>Przegląd podejścia: jak <see cref="QuizForm"/>, ale kontrolki są zablokowane.</summary>
    QuizReview,
}

/// <summary>
/// Sanitizer HTML oparty o allowlistę (parser HTML5 z AngleSharp, ponowna serializacja własnym kodem).
/// Wszystko spoza listy jest rozpakowywane (zostaje tekst), a skrypty/style/ramki usuwane razem z zawartością.
/// Atrybuty on*, style i adresy javascript:/vbscript:/data: (poza obrazkami) nigdy nie przechodzą.
/// </summary>
public static class HtmlSanitizer
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "p", "br", "b", "strong", "i", "em", "u", "ul", "ol", "li", "a", "img",
        "table", "thead", "tbody", "tr", "td", "th", "h1", "h2", "h3", "h4", "h5", "h6",
        "blockquote", "code", "pre", "span", "div",
    };

    private static readonly HashSet<string> QuizOnly = new(StringComparer.Ordinal)
    {
        "input", "select", "option", "optgroup", "textarea", "label", "fieldset", "legend",
    };

    private static readonly HashSet<string> DropWithContent = new(StringComparer.Ordinal)
    {
        "script", "style", "noscript", "template", "iframe", "frame", "frameset", "object", "embed", "applet",
        "svg", "math", "head", "title", "link", "meta", "base", "canvas", "audio", "video", "source", "track",
        "button", "datalist", "dialog", "form-data",
    };

    // tagi kontrolek, które w profilu Content są usuwane z zawartością (nie rozpakowywane)
    private static readonly HashSet<string> ControlsDroppedInContent = new(StringComparer.Ordinal)
    {
        "input", "select", "textarea", "option", "optgroup",
    };

    private static readonly HashSet<string> Void = new(StringComparer.Ordinal) { "br", "img", "input", "hr" };

    private static readonly HashSet<string> InputTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "radio", "checkbox", "text", "number", "hidden", "email", "search", "tel",
    };

    private const int MaxDepth = 64;

    public static string Sanitize(string? html, SanitizeProfile profile = SanitizeProfile.Content)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var parser = new HtmlParser();
        using var doc = parser.ParseDocument("<!DOCTYPE html><html><body>" + html + "</body></html>");
        var sb = new StringBuilder(html.Length + 64);
        if (doc.Body is { } body)
            foreach (var child in body.ChildNodes) Write(child, sb, profile, 0);
        return sb.ToString();
    }

    private static void Write(INode node, StringBuilder sb, SanitizeProfile profile, int depth)
    {
        switch (node.NodeType)
        {
            case NodeType.Text:
                sb.Append(WebUtility.HtmlEncode(node.TextContent));
                return;
            case NodeType.Element:
                WriteElement((IElement)node, sb, profile, depth);
                return;
            default:
                return; // komentarze, instrukcje przetwarzania itp.
        }
    }

    private static void WriteElement(IElement el, StringBuilder sb, SanitizeProfile profile, int depth)
    {
        var tag = el.LocalName.ToLowerInvariant();
        var quiz = profile != SanitizeProfile.Content;

        if (DropWithContent.Contains(tag)) return;
        if (!quiz && ControlsDroppedInContent.Contains(tag)) return;

        var allowed = Allowed.Contains(tag) || (quiz && QuizOnly.Contains(tag));
        if (!allowed || depth >= MaxDepth)
        {
            // rozpakuj: zachowaj tylko dzieci (przy przekroczeniu głębokości – sam tekst)
            if (depth >= MaxDepth) { sb.Append(WebUtility.HtmlEncode(el.TextContent)); return; }
            foreach (var c in el.ChildNodes) Write(c, sb, profile, depth + 1);
            return;
        }

        if (tag == "input" && (!InputTypes.Contains(el.GetAttribute("type") ?? "text"))) return;

        sb.Append('<').Append(tag);
        WriteAttributes(el, tag, sb, profile);
        sb.Append('>');
        if (Void.Contains(tag)) return;
        foreach (var c in el.ChildNodes) Write(c, sb, profile, depth + 1);
        sb.Append("</").Append(tag).Append('>');
    }

    private static void WriteAttributes(IElement el, string tag, StringBuilder sb, SanitizeProfile profile)
    {
        var quiz = profile != SanitizeProfile.Content;
        foreach (var a in el.Attributes)
        {
            var name = a.Name.ToLowerInvariant();
            var value = a.Value;
            if (name.StartsWith("on", StringComparison.Ordinal) || name == "style") continue;
            if (!AttributeAllowed(tag, name, quiz)) continue;

            if (name is "href" && !IsSafeUrl(value, allowDataImage: false)) continue;
            if (name is "src" && !IsSafeUrl(value, allowDataImage: true)) continue;
            if (tag == "input" && name == "type" && !InputTypes.Contains(value)) continue;

            AppendAttr(sb, name, value, boolean: IsBoolean(name));
        }

        if (tag == "a")
            sb.Append(" target=\"_blank\" rel=\"noopener noreferrer\"");
        if (profile == SanitizeProfile.QuizReview && tag is "input" or "select" or "textarea")
            sb.Append(" disabled");
    }

    private static bool IsBoolean(string name) => name is "checked" or "selected" or "multiple" or "disabled" or "readonly";

    private static void AppendAttr(StringBuilder sb, string name, string value, bool boolean)
    {
        sb.Append(' ').Append(name);
        if (boolean) return;
        sb.Append("=\"").Append(WebUtility.HtmlEncode(value)).Append('"');
    }

    private static bool AttributeAllowed(string tag, string name, bool quiz)
    {
        if (quiz && name is "class" or "id" or "aria-label") return true;
        switch (tag)
        {
            case "a": return name is "href" or "title";
            case "img": return name is "src" or "alt" or "title";
            case "td" or "th": return name is "colspan" or "rowspan";
            case "ol": return name is "start";
        }
        if (!quiz) return false;
        return tag switch
        {
            "input" => name is "type" or "name" or "value" or "checked" or "disabled" or "readonly" or "size" or "maxlength" or "min" or "max" or "step",
            "select" => name is "name" or "multiple" or "disabled",
            "option" => name is "value" or "selected" or "disabled",
            "optgroup" => name is "label" or "disabled",
            "textarea" => name is "name" or "rows" or "cols" or "disabled" or "readonly" or "maxlength",
            "label" => name is "for",
            _ => false,
        };
    }

    private static bool IsSafeUrl(string raw, bool allowDataImage)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        // wytnij białe znaki i znaki sterujące, którymi obchodzi się filtry („java\tscript:”)
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
            if (!char.IsControl(ch) && !char.IsWhiteSpace(ch)) sb.Append(ch);
        var url = sb.ToString();

        var colon = url.IndexOf(':');
        var firstDelim = url.IndexOfAny(['/', '?', '#']);
        var hasScheme = colon > 0 && (firstDelim < 0 || colon < firstDelim);
        if (!hasScheme) return true; // względny
        var scheme = url[..colon].ToLowerInvariant();
        if (scheme is "http" or "https") return true;
        if (!allowDataImage) return scheme is "mailto" or "tel";
        if (scheme != "data") return false;
        var rest = url[(colon + 1)..].ToLowerInvariant();
        return rest.StartsWith("image/png;base64,") || rest.StartsWith("image/jpeg;base64,")
            || rest.StartsWith("image/jpg;base64,") || rest.StartsWith("image/gif;base64,")
            || rest.StartsWith("image/webp;base64,");
    }
}
