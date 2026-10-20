using LocalSeek.Core.Text;

namespace LocalSeek.Tests.Text;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("ＡＢＣ", "abc")]
    [InlineData("HeLLo", "hello")]
    [InlineData("臺灣", "台灣")]
    [InlineData("ﬁ", "fi")]
    [InlineData("㈱", "(株)")]
    [InlineData("㍻", "平成")]
    [InlineData("　", " ")]
    [InlineData("é", "é")]
    [InlineData("", "")]
    public void Normalize_applies_nfkc_then_lowercase_then_tai(string input, string expected)
    {
        Assert.Equal(expected, TextNormalizer.Normalize(input));
    }

    [Fact]
    public void Only_tai_variant_is_mapped()
    {
        // 臺 is the single extra mapping; other traditional/simplified pairs are untouched.
        Assert.Equal("灣台", TextNormalizer.Normalize("灣臺"));
        Assert.Equal("體灣", TextNormalizer.Normalize("體灣"));
    }

    [Fact]
    public void Expanded_chars_all_map_back_to_their_source_unit()
    {
        var normalized = new NormalizedText();
        TextNormalizer.Normalize("a㈱b", normalized);

        Assert.Equal("a(株)b", normalized.ToString());
        Assert.Equal([0, 1, 1, 1, 2], Enumerable.Range(0, normalized.Length).Select(normalized.SourceStart));
        Assert.Equal([1, 2, 2, 2, 3], Enumerable.Range(0, normalized.Length).Select(normalized.SourceEnd));
    }

    [Fact]
    public void Surrogate_pair_is_a_single_unit()
    {
        var normalized = new NormalizedText();
        TextNormalizer.Normalize("x𠮷", normalized);

        Assert.Equal(3, normalized.Length);
        Assert.Equal((1, 3), (normalized.SourceStart(1), normalized.SourceEnd(1)));
        Assert.Equal((1, 3), (normalized.SourceStart(2), normalized.SourceEnd(2)));
    }

    [Fact]
    public void Combining_mark_joins_its_base_unit()
    {
        var normalized = new NormalizedText();
        TextNormalizer.Normalize("éx", normalized);

        Assert.Equal("éx", normalized.ToString());
        Assert.Equal((0, 2), (normalized.SourceStart(0), normalized.SourceEnd(0)));
        Assert.Equal((2, 3), (normalized.SourceStart(1), normalized.SourceEnd(1)));
    }

    [Theory]
    [InlineData("\uD800")]
    [InlineData("\uDC00")]
    [InlineData("￿")]
    [InlineData("￾")]
    [InlineData("")]
    public void Ill_formed_or_noncharacter_input_does_not_throw(string input)
    {
        var normalized = TextNormalizer.Normalize("a" + input + "b");

        Assert.StartsWith("a", normalized);
        Assert.EndsWith("b", normalized);
    }

    [Fact]
    public void Buffer_is_reusable_across_calls()
    {
        var normalized = new NormalizedText();
        TextNormalizer.Normalize(new string('x', 1000), normalized);
        TextNormalizer.Normalize("AB", normalized);

        Assert.Equal("ab", normalized.ToString());
    }
}
