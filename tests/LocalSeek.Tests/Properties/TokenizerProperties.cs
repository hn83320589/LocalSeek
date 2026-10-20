using System.Globalization;
using LocalSeek.Core.Text;
using LocalSeek.Tests.Reference;

namespace LocalSeek.Tests.Properties;

public class TokenizerProperties
{
    [Fact]
    public void Tokens_positions_and_char_counts_match_reference_tokenizer()
    {
        Property.ForAll(seed: 1001, iterations: 2000, random =>
        {
            var text = TextGen.Text(random, TextGen.All, maxFragments: 30);
            var actual = Tokenizer.TokenizeToList(text);
            var expected = ReferenceTokenizer.Tokenize(text);

            Assert.Equal(
                expected.Tokens.Select(t => (t.Text, t.Kind, t.Position)),
                actual.Tokens.Select(t => (t.Text, t.Kind.ToString(), t.Position)));
            Assert.Equal(
                expected.CjkCharCounts.OrderBy(p => p.Key, StringComparer.Ordinal),
                actual.CjkChars.GroupBy(c => c.Char).Select(g => KeyValuePair.Create(g.Key, g.Count()))
                    .OrderBy(p => p.Key, StringComparer.Ordinal));
        });
    }

    /// <summary>Acceptance #2, general form (decision A): the source range contains the token and is minimal.</summary>
    [Fact]
    public void Source_range_contains_token_and_is_minimal()
    {
        Property.ForAll(seed: 1002, iterations: 2000, random =>
        {
            var text = TextGen.Text(random, TextGen.All, maxFragments: 25);
            foreach (var token in Tokenizer.TokenizeToList(text).Tokens)
            {
                var range = text[token.SourceStart..token.SourceEnd];
                Assert.Contains(token.Text, ReferenceTokenizer.Normalize(range), StringComparison.Ordinal);

                var units = Units(range);
                Assert.True(units.Count > 0);
                var withoutFirst = range[units[0].Length..];
                var withoutLast = range[..^units[^1].Length];
                Assert.DoesNotContain(token.Text, ReferenceTokenizer.Normalize(withoutFirst), StringComparison.Ordinal);
                Assert.DoesNotContain(token.Text, ReferenceTokenizer.Normalize(withoutLast), StringComparison.Ordinal);
            }
        });
    }

    /// <summary>Acceptance #2, strict form: without expanding characters the range normalizes to exactly the token.</summary>
    [Fact]
    public void Source_range_normalizes_to_exactly_the_token_for_non_expanding_text()
    {
        Property.ForAll(seed: 1003, iterations: 2000, random =>
        {
            var text = TextGen.Text(random, TextGen.NonExpanding, maxFragments: 30);
            foreach (var token in Tokenizer.TokenizeToList(text).Tokens)
            {
                Assert.Equal(token.Text, ReferenceTokenizer.Normalize(text[token.SourceStart..token.SourceEnd]));
            }
        });
    }

    [Fact]
    public void Source_ranges_never_split_surrogate_pairs()
    {
        Property.ForAll(seed: 1004, iterations: 1000, random =>
        {
            var text = TextGen.Text(random, TextGen.All, maxFragments: 30);
            var result = Tokenizer.TokenizeToList(text);
            var boundaries = result.Tokens.SelectMany(t => new[] { t.SourceStart, t.SourceEnd })
                .Concat(result.CjkChars.SelectMany(c => new[] { c.SourceStart, c.SourceEnd }));
            foreach (var b in boundaries)
            {
                Assert.False(b > 0 && b < text.Length && char.IsLowSurrogate(text[b]) && char.IsHighSurrogate(text[b - 1]));
            }
        });
    }

    private static List<string> Units(string s)
    {
        var units = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            var start = i;
            i += char.IsSurrogatePair(s, i) ? 2 : 1;
            while (i < s.Length && ReferenceTokenizer.IsCombiningMark(s, i))
            {
                i += char.IsSurrogatePair(s, i) ? 2 : 1;
            }

            units.Add(s[start..i]);
        }

        return units;
    }

    [Fact]
    public void Units_helper_groups_combining_marks()
    {
        Assert.Equal(["é", "x"], Units("éx"));
        Assert.Equal(UnicodeCategory.NonSpacingMark, CharUnicodeInfo.GetUnicodeCategory('́'));
    }
}
