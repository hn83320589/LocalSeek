using System.Text.Json;
using LocalSeek.Core.Text;

namespace LocalSeek.Tests.Text;

public class TokenizerExpectedFileTests
{
    private static readonly Lazy<Dictionary<string, JsonElement>> Cases = new(LoadCases);

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Cases.Value.Keys)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Tokens_and_positions_match_expected_file(string caseName)
    {
        var c = Cases.Value[caseName];
        var actual = Tokenizer.TokenizeToList(c.GetProperty("input").GetString()!);

        var expectedTokens = c.GetProperty("tokens").EnumerateArray()
            .Select(t => new Token(
                t[0].GetString()!,
                Enum.Parse<TokenKind>(t[1].GetString()!),
                t[2].GetInt32(),
                t[3].GetInt32(),
                t[4].GetInt32()))
            .ToList();
        var expectedChars = c.GetProperty("cjkChars").EnumerateArray()
            .Select(t => new CjkCharHit(t[0].GetString()!, t[1].GetInt32(), t[2].GetInt32()))
            .ToList();

        Assert.Equal(expectedTokens, actual.Tokens);
        Assert.Equal(expectedChars, actual.CjkChars);
        Assert.Equal(expectedTokens.Count, actual.Length);
    }

    // Lone surrogates cannot be represented in JSON, so this case lives here instead of the expected file.
    [Fact]
    public void Lone_surrogate_is_a_separator()
    {
        var actual = Tokenizer.TokenizeToList("a\uD800b");

        Assert.Equal(
            [new Token("a", TokenKind.Latin, 0, 0, 1), new Token("b", TokenKind.Latin, 1, 2, 3)],
            actual.Tokens);
        Assert.Empty(actual.CjkChars);
    }

    private static Dictionary<string, JsonElement> LoadCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "tokenizer-expected.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("cases").EnumerateArray()
            .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.Clone());
    }
}
