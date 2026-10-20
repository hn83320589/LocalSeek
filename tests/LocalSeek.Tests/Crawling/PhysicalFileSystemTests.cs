using System.Diagnostics;
using LocalSeek.Indexing.Crawling;
using LocalSeek.Indexing.Diagnostics;
using LocalSeek.Indexing.FileSystem;

namespace LocalSeek.Tests.Crawling;

/// <summary>Acceptance #13 on the real Windows file system: junction loop, long path, locked file.</summary>
public sealed class PhysicalFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "localseek-crawl-" + Guid.NewGuid().ToString("N"));
    private readonly PhysicalSourceFileSystem _fs = new();
    private readonly DiagnosticsLog _diagnostics = new(TimeProvider.System);

    public PhysicalFileSystemTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // Remove junctions first (without entering them) so deleting the tree never follows a link.
        RemoveLinks(_root);
        Directory.Delete(_root, recursive: true);
    }

    private static void RemoveLinks(string directory)
    {
        foreach (var child in new DirectoryInfo(directory).EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            if (child.LinkTarget is not null)
            {
                child.Delete();
            }
            else
            {
                RemoveLinks(child.FullName);
            }
        }
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void Real_junction_loop_terminates()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "b.txt"), "b");
        CreateJunction(Path.Combine(_root, "sub", "loop"), _root);

        var found = new Crawler(_fs, _diagnostics).Crawl(_root, ExclusionRules.None, new CrawlOptions()).Select(e => e.Name).ToList();

        Assert.Equal(["a.txt", "b.txt"], found);
        Assert.True(_diagnostics.TryGet(Path.Combine(_root, "sub", "loop"), out var entry));
        Assert.Equal(DiagnosticKind.LoopDetected, entry.Kind);
    }

    [Fact]
    public void Real_long_path_is_read()
    {
        var deep = _root;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, "長路徑資料夾abcdefghijklmnopqrstuvwxyz");
        }

        Directory.CreateDirectory(deep);
        var file = Path.Combine(deep, "深.txt");
        File.WriteAllText(file, "deep content");

        var found = new Crawler(_fs, _diagnostics).Crawl(_root, ExclusionRules.None, new CrawlOptions()).Single();

        Assert.Equal(file, found.FullPath);
        Assert.IsType<ReadOutcome.Success>(SourceFileReader.Read(_fs, file));
    }

    [Fact]
    public void Real_exclusively_locked_file_is_reported_as_locked()
    {
        var file = Path.Combine(_root, "locked.txt");
        File.WriteAllText(file, "x");

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.IsType<ReadOutcome.Locked>(SourceFileReader.Read(_fs, file));
        }

        Assert.IsType<ReadOutcome.Success>(SourceFileReader.Read(_fs, file));
    }

    [Fact]
    public void Real_file_open_for_writing_by_another_program_can_still_be_read()
    {
        var file = Path.Combine(_root, "open.txt");
        File.WriteAllText(file, "shared");

        using var writer = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        Assert.IsType<ReadOutcome.Success>(SourceFileReader.Read(_fs, file));
    }
}
