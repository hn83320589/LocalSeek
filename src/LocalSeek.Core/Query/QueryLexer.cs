using System.Globalization;

namespace LocalSeek.Core.Query;

internal enum LexKind
{
    LParen,
    RParen,
    And,
    Or,
    Not,
    Word,
    Prefix,
    Phrase,
    Filter,
    End,
}

internal readonly record struct LexToken(LexKind Kind, int Start, int Length, string Text = "", Filter? Filter = null);

/// <summary>
/// Splits a query into tokens. Operator chars are '(', ')', '"' and a leading '-'; a word is any run
/// of other non-whitespace chars. AND/OR/NOT are operators only in uppercase.
/// </summary>
internal static class QueryLexer
{
    private static readonly string[] FilterKeys = ["ext:", "path:", "modified:", "size:"];

    public static List<LexToken> Lex(string query)
    {
        var tokens = new List<LexToken>();
        var i = 0;
        while (i < query.Length)
        {
            var c = query[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '(')
            {
                tokens.Add(new LexToken(LexKind.LParen, i++, 1));
            }
            else if (c == ')')
            {
                tokens.Add(new LexToken(LexKind.RParen, i++, 1));
            }
            else if (c == '"')
            {
                var end = ClosingQuote(query, i);
                tokens.Add(new LexToken(LexKind.Phrase, i, end + 1 - i, query[(i + 1)..end]));
                i = end + 1;
            }
            else if (c == '-' && i + 1 < query.Length && !char.IsWhiteSpace(query[i + 1]) && query[i + 1] != ')')
            {
                tokens.Add(new LexToken(LexKind.Not, i++, 1));
            }
            else
            {
                i = LexWord(query, i, tokens);
            }
        }

        tokens.Add(new LexToken(LexKind.End, query.Length, 0));
        return tokens;
    }

    private static int LexWord(string query, int start, List<LexToken> tokens)
    {
        var i = start;
        while (i < query.Length && !IsWordBreak(query[i]))
        {
            i++;
        }

        var text = query[start..i];
        var key = FilterKeys.FirstOrDefault(k => text.StartsWith(k, StringComparison.OrdinalIgnoreCase));
        if (key is not null)
        {
            var valueStart = start + key.Length;
            string value;
            if (text.Length == key.Length && i < query.Length && query[i] == '"')
            {
                var end = ClosingQuote(query, i);
                value = query[(i + 1)..end];
                i = end + 1;
            }
            else
            {
                value = text[key.Length..];
            }

            var filter = ParseFilter(key, value, start, valueStart);
            tokens.Add(new LexToken(LexKind.Filter, start, i - start, Filter: filter));
            return i;
        }

        var kind = text switch
        {
            "AND" => LexKind.And,
            "OR" => LexKind.Or,
            "NOT" => LexKind.Not,
            _ when text.EndsWith('*') => LexKind.Prefix,
            _ => LexKind.Word,
        };
        tokens.Add(new LexToken(kind, start, i - start, kind == LexKind.Prefix ? text[..^1] : text));
        return i;
    }

    private static bool IsWordBreak(char c) => char.IsWhiteSpace(c) || c is '(' or ')' or '"';

    private static int ClosingQuote(string query, int openIndex)
    {
        var end = query.IndexOf('"', openIndex + 1);
        if (end < 0)
        {
            throw new QuerySyntaxException("引號沒有結束", openIndex, query.Length - openIndex);
        }

        return end;
    }

    private static Filter ParseFilter(string key, string value, int keyStart, int valueStart)
    {
        switch (key)
        {
            case "ext:":
                var extension = value.TrimStart('.');
                if (extension.Length == 0)
                {
                    throw new QuerySyntaxException("ext: 需要副檔名", keyStart, key.Length + value.Length);
                }

                return new ExtensionFilter(extension.ToLowerInvariant());

            case "path:":
                if (value.Length == 0)
                {
                    throw new QuerySyntaxException("path: 需要路徑片段", keyStart, key.Length);
                }

                return new PathFilter(value);

            case "modified:":
                return ParseModified(value, keyStart, valueStart);

            default:
                return ParseSize(value, valueStart);
        }
    }

    private static ModifiedFilter ParseModified(string value, int keyStart, int valueStart)
    {
        if (value.Length == 0)
        {
            throw new QuerySyntaxException("modified: 需要日期", keyStart, "modified:".Length);
        }

        var (comparison, opLength) = value switch
        {
            _ when value.StartsWith(">=", StringComparison.Ordinal) => (DateComparison.AtOrAfter, 2),
            _ when value.StartsWith("<=", StringComparison.Ordinal) => (DateComparison.AtOrBefore, 2),
            _ when value.StartsWith('=') => (DateComparison.Equal, 1),
            _ when value[0] is '<' or '>' => throw new QuerySyntaxException("modified: 只支援 >=、<=、=", valueStart, 1),
            _ => (DateComparison.Equal, 0),
        };

        var dateText = value[opLength..];
        if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new QuerySyntaxException("日期格式必須是 yyyy-MM-dd", valueStart + opLength, dateText.Length);
        }

        return new ModifiedFilter(comparison, date);
    }

    private static SizeFilter ParseSize(string value, int valueStart)
    {
        if (value.Length == 0 || value[0] is not ('<' or '>'))
        {
            throw new QuerySyntaxException("size: 必須以 < 或 > 開頭", valueStart, Math.Max(value.Length, 1));
        }

        var comparison = value[0] == '<' ? SizeComparison.LessThan : SizeComparison.GreaterThan;
        var digits = 1;
        while (digits < value.Length && char.IsAsciiDigit(value[digits]))
        {
            digits++;
        }

        if (digits == 1)
        {
            throw new QuerySyntaxException("size: 需要數字", valueStart + 1, Math.Max(value.Length - 1, 1));
        }

        var unit = value[digits..];
        long multiplier = unit.ToUpperInvariant() switch
        {
            "" => 1,
            "KB" => 1024,
            "MB" => 1024 * 1024,
            _ => throw new QuerySyntaxException("size: 單位只支援 KB 或 MB", valueStart + digits, unit.Length),
        };

        if (!long.TryParse(value.AsSpan(1, digits - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number > long.MaxValue / multiplier)
        {
            throw new QuerySyntaxException("size: 數值過大", valueStart + 1, digits - 1);
        }

        return new SizeFilter(comparison, number * multiplier);
    }
}
