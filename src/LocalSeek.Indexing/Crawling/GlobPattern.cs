using System.Text;
using System.Text.RegularExpressions;

namespace LocalSeek.Indexing.Crawling;

/// <summary>
/// An exclusion glob matched against the whole path relative to a root ('/'-separated, case-insensitive):
/// '*' = any chars except '/', '**' = any depth (a leading "**/" may match zero directories),
/// '?' = one char except '/'. A trailing '/' restricts the pattern to directories.
/// '\' is accepted as a separator and a leading '/' is ignored.
/// </summary>
public sealed class GlobPattern
{
    private readonly Regex _regex;

    public GlobPattern(string pattern)
    {
        Pattern = pattern;
        var normalized = pattern.Trim().Replace('\\', '/');
        DirectoryOnly = normalized.EndsWith('/');
        _regex = new Regex("^" + Translate(normalized.Trim('/')) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public string Pattern { get; }

    public bool DirectoryOnly { get; }

    public bool IsMatch(string relativePath, bool isDirectory) =>
        (!DirectoryOnly || isDirectory) && _regex.IsMatch(relativePath.Replace('\\', '/'));

    private static string Translate(string glob)
    {
        var regex = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                var startsSegment = i == 0 || glob[i - 1] == '/';
                if (startsSegment && i + 2 < glob.Length && glob[i + 2] == '/')
                {
                    regex.Append("(?:.*/)?");
                    i += 2;
                }
                else
                {
                    regex.Append(".*");
                    i++;
                }
            }
            else if (c == '*')
            {
                regex.Append("[^/]*");
            }
            else if (c == '?')
            {
                regex.Append("[^/]");
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString()));
            }
        }

        return regex.ToString();
    }
}

/// <summary>A set of exclusion globs; blank patterns are ignored.</summary>
public sealed class ExclusionRules(IEnumerable<string> patterns)
{
    private readonly GlobPattern[] _patterns = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => new GlobPattern(p)).ToArray();

    public static ExclusionRules None { get; } = new([]);

    public int Count => _patterns.Length;

    public bool IsExcluded(string relativePath, bool isDirectory) =>
        _patterns.Any(p => p.IsMatch(relativePath, isDirectory));
}
