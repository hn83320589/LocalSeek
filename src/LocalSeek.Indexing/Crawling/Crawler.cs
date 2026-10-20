using LocalSeek.Indexing.Diagnostics;
using LocalSeek.Indexing.Extraction;
using LocalSeek.Indexing.FileSystem;

namespace LocalSeek.Indexing.Crawling;

public sealed record CrawlOptions(bool SkipHiddenAndSystem = true, bool FollowReparsePoints = true);

public sealed class CrawlStatistics
{
    public int Directories { get; internal set; }

    public int Files { get; internal set; }

    public int Excluded { get; internal set; }
}

/// <summary>
/// Depth-first walk of a root that never fails as a whole: unreadable directories, broken links and
/// loops are recorded in the diagnostics log and skipped.
/// <para>Loops: directories are tracked by canonical path (links resolved to their final target).
/// A link whose target lies inside the root is skipped (the real path is crawled anyway), and a
/// target reached a second time is skipped, so junction cycles terminate.</para>
/// </summary>
public sealed class Crawler(ISourceFileSystem fileSystem, DiagnosticsLog diagnostics)
{
    public IEnumerable<SourceEntry> Crawl(string root, ExclusionRules rules, CrawlOptions options, CrawlStatistics? statistics = null, CancellationToken cancellation = default)
    {
        statistics ??= new CrawlStatistics();
        var rootEntry = fileSystem.GetEntry(root);
        if (rootEntry is null || !rootEntry.IsDirectory)
        {
            diagnostics.Record(root, DiagnosticKind.RootUnavailable, "根資料夾不存在或無法存取");
            yield break;
        }

        diagnostics.Clear(root);
        var rootCanonical = Canonical(fileSystem.GetLinkTarget(root) ?? root);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootCanonical };
        var stack = new Stack<(string Path, string Canonical, string Relative)>();
        stack.Push((root, rootCanonical, ""));

        while (stack.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var (directory, canonical, relative) = stack.Pop();
            statistics.Directories++;
            IReadOnlyList<SourceEntry> children;
            try
            {
                children = fileSystem.EnumerateDirectory(directory);
            }
            catch (UnauthorizedAccessException ex)
            {
                diagnostics.Record(directory, DiagnosticKind.AccessDenied, $"權限不足，無法列出資料夾：{ex.Message}");
                continue;
            }
            catch (IOException ex)
            {
                diagnostics.Record(directory, DiagnosticKind.ReadError, $"無法列出資料夾：{ex.Message}");
                continue;
            }

            var subdirectories = new List<(string, string, string)>();
            foreach (var entry in children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                var childRelative = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
                if (options.SkipHiddenAndSystem && entry.IsHiddenOrSystem)
                {
                    diagnostics.Record(entry.FullPath, DiagnosticKind.HiddenOrSystem, "隱藏或系統屬性，依設定略過");
                    continue;
                }

                if (rules.IsExcluded(childRelative, entry.IsDirectory))
                {
                    statistics.Excluded++;
                    continue;
                }

                if (!entry.IsDirectory)
                {
                    if (ContentExtractor.IsSupported(entry.Name))
                    {
                        statistics.Files++;
                        yield return entry;
                    }

                    continue;
                }

                if (TryEnterDirectory(entry, canonical, rootCanonical, visited, options, out var childCanonical))
                {
                    subdirectories.Add((entry.FullPath, childCanonical, childRelative));
                }
            }

            // Push in reverse so directories are visited in name order.
            for (var i = subdirectories.Count - 1; i >= 0; i--)
            {
                stack.Push(subdirectories[i]);
            }
        }
    }

    private bool TryEnterDirectory(SourceEntry entry, string parentCanonical, string rootCanonical, HashSet<string> visited, CrawlOptions options, out string canonical)
    {
        canonical = parentCanonical + '\\' + entry.Name;
        if (entry.IsReparsePoint)
        {
            if (!options.FollowReparsePoints)
            {
                diagnostics.Record(entry.FullPath, DiagnosticKind.ReparsePointSkipped, "接合點或符號連結，依設定不跟隨");
                return false;
            }

            var target = fileSystem.GetLinkTarget(entry.FullPath);
            if (target is null || fileSystem.GetEntry(target) is not { IsDirectory: true })
            {
                diagnostics.Record(entry.FullPath, DiagnosticKind.BrokenLink, $"連結目標不存在：{target}");
                return false;
            }

            canonical = Canonical(target);
            if (IsSameOrUnder(canonical, rootCanonical))
            {
                diagnostics.Record(entry.FullPath, DiagnosticKind.LoopDetected, $"連結指向根資料夾內的 {target}，略過以避免迴圈或重複");
                return false;
            }
        }

        if (!visited.Add(canonical))
        {
            diagnostics.Record(entry.FullPath, DiagnosticKind.LoopDetected, $"資料夾 {canonical} 已經索引過（迴圈或重複連結）");
            return false;
        }

        return true;
    }

    private static string Canonical(string path) => path.TrimEnd('\\', '/').Replace('/', '\\');

    private static bool IsSameOrUnder(string path, string directory) =>
        path.Equals(directory, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(directory + '\\', StringComparison.OrdinalIgnoreCase);
}
