using System.Net;
using System.Text;

namespace LocalSeek.Indexing.Extraction;

/// <summary>
/// Tolerant HTML-to-text conversion: drops comments, declarations, &lt;script&gt; and &lt;style&gt; content
/// and tags, then decodes entities. Inline tags vanish (so "台&lt;b&gt;灣&lt;/b&gt;" stays one word), other
/// tags become line breaks. Unterminated constructs swallow the rest of the input.
/// </summary>
internal static class HtmlTextExtractor
{
    private static readonly HashSet<string> InlineTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "b", "bdi", "bdo", "cite", "code", "data", "dfn", "em", "font", "i", "kbd", "mark", "q",
        "rp", "rt", "ruby", "s", "samp", "small", "span", "strong", "sub", "sup", "time", "u", "var", "wbr",
    };

    public static string ToText(string html)
    {
        var text = new StringBuilder(html.Length);
        var i = 0;
        while (i < html.Length)
        {
            var c = html[i];
            if (c != '<')
            {
                text.Append(c);
                i++;
                continue;
            }

            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3;
                continue;
            }

            var j = i + 1;
            var closing = j < html.Length && html[j] == '/';
            if (closing)
            {
                j++;
            }

            var nameStart = j;
            while (j < html.Length && char.IsAsciiLetterOrDigit(html[j]))
            {
                j++;
            }

            var name = html[nameStart..j];
            var declaration = name.Length == 0 && j < html.Length && html[j] is '!' or '?';
            if (name.Length == 0 && !declaration)
            {
                // A bare '<' such as "a < b" is text.
                text.Append(c);
                i++;
                continue;
            }

            var tagEnd = FindTagEnd(html, j);
            if (tagEnd < 0)
            {
                break;
            }

            i = tagEnd + 1;
            if (!closing && (name.Equals("script", StringComparison.OrdinalIgnoreCase) || name.Equals("style", StringComparison.OrdinalIgnoreCase)))
            {
                var close = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                var closeEnd = close < 0 ? -1 : FindTagEnd(html, close + 2);
                i = closeEnd < 0 ? html.Length : closeEnd + 1;
                text.Append('\n');
                continue;
            }

            if (!declaration && !InlineTags.Contains(name))
            {
                text.Append('\n');
            }
        }

        return WebUtility.HtmlDecode(text.ToString());
    }

    /// <summary>Index of the '&gt;' closing a tag, ignoring '&gt;' inside quoted attribute values.</summary>
    private static int FindTagEnd(string html, int from)
    {
        char? quote = null;
        for (var k = from; k < html.Length; k++)
        {
            var c = html[k];
            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return k;
            }
        }

        return -1;
    }
}
