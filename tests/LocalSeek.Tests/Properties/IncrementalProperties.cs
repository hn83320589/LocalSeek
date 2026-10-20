using LocalSeek.Indexing.Watching;
using LocalSeek.Tests.Fakes;
using LocalSeek.Tests.Pipeline;
using static LocalSeek.Tests.Pipeline.EngineFixture;

namespace LocalSeek.Tests.Properties;

/// <summary>
/// Acceptance #11: after random adds, edits, renames, moves across folders, deletes, directory renames
/// and buffer overflows (events lost, followed by a difference scan), the logical index equals a fresh
/// build from the final file system.
/// </summary>
public class IncrementalProperties
{
    private static readonly string[] Folders = [$@"{Root}\a", $@"{Root}\b", $@"{Root}\b\deep", $@"{Root}\資料"];
    private static readonly string[] Extensions = [".txt", ".md", ".html", ".csv"];

    [Fact]
    public async Task Incremental_updates_converge_to_a_fresh_build()
    {
        var operations = 0;
        for (var iteration = 0; iteration < 25; iteration++)
        {
            var seed = new Random(8001 + iteration).Next();
            try
            {
                operations += await RunScenario(new Random(seed));
            }
            catch (Exception ex)
            {
                throw new PropertyFailedException($"Incremental property failed for seed {seed}: {ex.Message}", ex);
            }
        }

        Assert.True(operations > 500);
    }

    private static async Task<int> RunScenario(Random random)
    {
        var fs = new FakeSourceFileSystem().Directory(Root);
        var files = new List<string>();
        var next = 0;
        string NewPath() => $@"{Folders[random.Next(Folders.Length)]}\f{next++}{Extensions[random.Next(Extensions.Length)]}";
        string Content() => string.Join(' ', Enumerable.Range(0, random.Next(1, 12)).Select(_ => CorpusGen.Fragment(random)));

        for (var i = 0; i < 12; i++)
        {
            var path = NewPath();
            fs.File(path, Content());
            files.Add(path);
        }

        using var f = new EngineFixture(fs);
        await f.ScanAll();

        var steps = random.Next(20, 40);
        for (var step = 0; step < steps; step++)
        {
            // A lost-events step: the change happens, the watcher overflows, nothing else is reported.
            var lost = random.Next(8) == 0;
            void Emit(FileChangeKind kind, string path, string? old = null)
            {
                if (!lost)
                {
                    f.Event(kind, path, old);
                }
            }

            switch (files.Count == 0 ? 0 : random.Next(8))
            {
                case 0:
                {
                    var path = NewPath();
                    fs.File(path, Content());
                    files.Add(path);
                    Emit(FileChangeKind.Created, path);
                    break;
                }

                case 1:
                {
                    var path = files[random.Next(files.Count)];
                    fs.Write(path, Content());
                    Emit(FileChangeKind.Changed, path);
                    Emit(FileChangeKind.Changed, path);
                    break;
                }

                case 2:
                {
                    var old = files[random.Next(files.Count)];
                    var renamed = Path.Combine(Path.GetDirectoryName(old)!, $"r{next++}{Extensions[random.Next(Extensions.Length)]}");
                    fs.Move(old, renamed);
                    files[files.IndexOf(old)] = renamed;
                    Emit(FileChangeKind.Renamed, renamed, old);
                    break;
                }

                case 3:
                {
                    var old = files[random.Next(files.Count)];
                    var moved = $@"{Folders[random.Next(Folders.Length)]}\m{next++}{Path.GetExtension(old)}";
                    fs.Move(old, moved);
                    files[files.IndexOf(old)] = moved;
                    Emit(FileChangeKind.Deleted, old);
                    Emit(FileChangeKind.Created, moved);
                    break;
                }

                case 4:
                {
                    var path = files[random.Next(files.Count)];
                    fs.Delete(path);
                    files.Remove(path);
                    Emit(FileChangeKind.Deleted, path);
                    break;
                }

                case 5:
                {
                    var directory = $@"{Root}\a";
                    var renamed = $@"{Root}\a{next++}";
                    if (fs.GetEntry(directory) is null)
                    {
                        break;
                    }

                    // Later creations under "a" simply recreate the folder.
                    fs.Move(directory, renamed);
                    for (var i = 0; i < files.Count; i++)
                    {
                        if (files[i].StartsWith(directory + '\\', StringComparison.OrdinalIgnoreCase))
                        {
                            files[i] = renamed + files[i][directory.Length..];
                        }
                    }

                    Emit(FileChangeKind.Renamed, renamed, directory);
                    break;
                }

                case 6:
                {
                    // Touch: same bytes, newer timestamp.
                    var path = files[random.Next(files.Count)];
                    var current = System.Text.Encoding.UTF8.GetString(fs.ReadAllBytes(path));
                    fs.Write(path, current);
                    Emit(FileChangeKind.Changed, path);
                    break;
                }

                default:
                {
                    // Spurious events for paths that did not change or never existed.
                    Emit(FileChangeKind.Changed, files[random.Next(files.Count)]);
                    Emit(FileChangeKind.Deleted, $@"{Root}\never\existed.txt");
                    break;
                }
            }

            if (lost)
            {
                f.Engine.RequestScan(Root);
            }

            if (random.Next(3) == 0)
            {
                await f.Settle(TimeSpan.FromMilliseconds(random.Next(0, 2000)));
            }
        }

        for (var round = 0; round < 5 && f.Engine.PendingCount > 0; round++)
        {
            await f.Settle(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(0, f.Engine.PendingCount);
        Assert.Equal(await f.RebuiltDump(), f.Dump());
        return steps;
    }
}
