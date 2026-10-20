using LocalSeek.Indexing.Crawling;

namespace LocalSeek.Tests.Settings;

/// <summary>Acceptance #21: glob semantics (relative path, '/' separator, case-insensitive).</summary>
public class GlobTests
{
    [Theory]
    // '*' matches within one segment only.
    [InlineData("*.tmp", "a.tmp", false, true)]
    [InlineData("*.tmp", "dir/a.tmp", false, false)]
    [InlineData("*.tmp", "a.tmpx", false, false)]
    [InlineData("dir/*.log", "dir/x.log", false, true)]
    [InlineData("dir/*.log", "dir/sub/x.log", false, false)]
    [InlineData("*", "anything", false, true)]
    [InlineData("*", "a/b", false, false)]
    // '**' spans any depth, including zero directories.
    [InlineData("**/*.tmp", "a.tmp", false, true)]
    [InlineData("**/*.tmp", "x/y/z/a.tmp", false, true)]
    [InlineData("a/**/b.txt", "a/b.txt", false, true)]
    [InlineData("a/**/b.txt", "a/x/y/b.txt", false, true)]
    [InlineData("a/**/b.txt", "ab.txt", false, false)]
    [InlineData("docs/**", "docs/a/b.md", false, true)]
    [InlineData("**", "any/thing/at/all", false, true)]
    [InlineData("**/backup*", "x/backup2024.txt", false, true)]
    // '?' is exactly one non-separator character.
    [InlineData("file?.txt", "file1.txt", false, true)]
    [InlineData("file?.txt", "file12.txt", false, false)]
    [InlineData("file?.txt", "file.txt", false, false)]
    [InlineData("a?b", "a/b", false, false)]
    // Case-insensitive, including CJK and full paths.
    [InlineData("**/*.TMP", "Dir/A.tmp", false, true)]
    [InlineData("Temp/", "TEMP", true, true)]
    [InlineData("**/舊資料/", "客戶/舊資料", true, true)]
    // Trailing '/' only matches directories.
    [InlineData("build/", "build", true, true)]
    [InlineData("build/", "build", false, false)]
    [InlineData("**/node_modules/", "web/app/node_modules", true, true)]
    [InlineData("**/node_modules/", "web/app/node_modules", false, false)]
    // Without trailing '/', both files and directories match.
    [InlineData("build", "build", false, true)]
    [InlineData("build", "build", true, true)]
    // Backslashes in patterns and paths are treated as separators; a leading '/' is the root.
    [InlineData(@"dir\*.log", "dir/x.log", false, true)]
    [InlineData("/top.txt", "top.txt", false, true)]
    [InlineData("**/*.tmp", @"x\y\a.tmp", false, true)]
    // Regex metacharacters are literal.
    [InlineData("a+b(1).txt", "a+b(1).txt", false, true)]
    [InlineData("a.txt", "abtxt", false, false)]
    [InlineData("[x].md", "[x].md", false, true)]
    public void Glob_matching(string pattern, string relativePath, bool isDirectory, bool expected)
    {
        Assert.Equal(expected, new GlobPattern(pattern).IsMatch(relativePath, isDirectory));
    }

    [Fact]
    public void Exclusion_rules_match_any_pattern_and_ignore_blank_lines()
    {
        var rules = new ExclusionRules(["**/*.tmp", "", "  ", "build/"]);

        Assert.True(rules.IsExcluded("a/b.tmp", isDirectory: false));
        Assert.True(rules.IsExcluded("build", isDirectory: true));
        Assert.False(rules.IsExcluded("build", isDirectory: false));
        Assert.False(rules.IsExcluded("readme.md", isDirectory: false));
        Assert.Equal(2, rules.Count);
    }
}
